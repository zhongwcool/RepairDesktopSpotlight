using DesktopSpotlightFix.Backup;
using DesktopSpotlightFix.Logging;
using DesktopSpotlightFix.PackageIdentity;

namespace DesktopSpotlightFix.Core;

public sealed class RepairOrchestrator
{
    private readonly FileLogger _log;
    private readonly IPackageTaskClient _package;
    private readonly SnapshotReader _snapshots;
    private readonly IrisProbe _iris;
    private readonly FallbackInjector _fallback;

    public RepairOrchestrator(
        FileLogger log,
        IPackageTaskClient package,
        SnapshotReader snapshots,
        IrisProbe iris,
        FallbackInjector fallback)
    {
        _log = log;
        _package = package;
        _snapshots = snapshots;
        _iris = iris;
        _fallback = fallback;
    }

    public TimeSpan WaitLimit { get; init; } = TimeSpan.FromMinutes(35);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(20);

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            Say(progress, "操作：" + request.Describe());
            if (request.Rollback)
                return Rollback(progress);

            if (request.ClearOneShot && !request.Repair && !request.Force && !request.InjectFallback && !request.OpenGate)
                return await ClearOneShotAsync(progress, cancellationToken).ConfigureAwait(false);

            if (request.Repair || request.Force)
            {
                if (request.ClearOneShot)
                    await ClearOneShotAsync(progress, cancellationToken).ConfigureAwait(false);
                return await RepairAsync(request, progress, cancellationToken).ConfigureAwait(false);
            }

            if (request.InjectFallback)
            {
                if (request.Diagnose)
                    await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
                var notes = await _fallback.InjectAsync(cancellationToken).ConfigureAwait(false);
                foreach (var note in notes)
                    Say(progress, note);
                var report = await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
                return Done(RunOutcome.Completed, "备用图已写入清单。请在桌面右键使用「下一个背景」验证，并保持 Windows 聚焦。", report);
            }

            if (request.OpenGate)
            {
                OpenGateOnly(progress);
                var report = await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
                return Done(RunOutcome.Completed, "已按请求回拨冷却时间戳。", report);
            }

            if (request.Wait)
                Say(progress, "等待观察只在 --repair 或 --force 安排拉图之后生效，本次没有等待。");

            var diagnosis = await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
            if (!diagnosis.Tasks.EnteredDesktopPackage)
                return Done(RunOutcome.Error, "无法进入包身份。" + (diagnosis.Tasks.DesktopError ?? ""), diagnosis);
            if (diagnosis.NeedsAttention)
                return Done(RunOutcome.DiagnoseNeedsAttention, "体检完成，发现需要处理的问题。", diagnosis);
            return Done(RunOutcome.DiagnoseOk, "体检完成，当前没有发现需要处理的问题。", diagnosis);
        }
        catch (OperationCanceledException)
        {
            Say(progress, "已取消。");
            return Done(RunOutcome.Cancelled, "已取消。", null);
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            Say(progress, "出错：" + ex.Message);
            return Done(RunOutcome.Error, ex.Message, null);
        }
    }

    private async Task<ToolResult> RepairAsync(ToolRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Say(progress, request.Force
            ? "强制安排会先补齐缺失任务，再登记一次单次拉图。登记成功不等于已经换上新壁纸。"
            : "正在补齐缺失的系统后台任务，并保持 Windows 聚焦相关开关。");

        using (var session = RegistrySession.Begin(request.Force ? "force" : "repair"))
        {
            foreach (var note in SpotlightMutator.EnableSpotlight(session))
                Say(progress, note);
            if (request.OpenGate)
            {
                var before = _snapshots.Read();
                foreach (var note in SpotlightMutator.OpenGate(session, before, DateTime.UtcNow))
                    Say(progress, note);
            }

            session.Commit();
            Say(progress, "本次修改已备份到：" + session.DirectoryPath);
        }

        var afterEnable = _snapshots.Read();
        if (afterEnable.BackgroundType != 3)
            Say(progress, "警告：注册表里的背景类型还不是 Windows 聚焦。请打开设置确认一次。");

        Say(progress, "正在检查并补齐后台任务…");
        var registered = await _package.RegisterMissingAsync(cancellationToken).ConfigureAwait(false);
        foreach (var line in registered)
            Say(progress, line);

        var snapshot = _snapshots.Read();
        var tasks = await _package.GetTaskReportAsync(cancellationToken).ConfigureAwait(false);
        if (!request.Force && HealthRules.ShouldSkipOneShot(snapshot, tasks.HasUpdateTimer))
        {
            Say(progress, "每日拉图任务已在，而且已有官方拉图证据，不安排单次拉图。若仍想立刻刷新，请加 --force。");
            if (request.InjectFallback)
                await InjectNotesAsync(progress, cancellationToken).ConfigureAwait(false);
            var healthyReport = await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
            return Done(RunOutcome.Completed, "缺失任务已检查。当前不需要强制安排拉图。", healthyReport);
        }

        var gate = GateCooldown.GetOpensUtc(snapshot.WallpaperRefresh, snapshot.Rotation, DateTime.UtcNow);
        if (gate.HasValue)
        {
            Say(progress, "当前仍在 60 分钟冷却内，系统会忽略拉图，直到 " + gate.Value.ToLocalTime().ToString("HH:mm")
                + "。可稍后再运行 --force，或加上 --open-gate。本次没有安排 oneshot。");
            if (request.InjectFallback)
                await InjectNotesAsync(progress, cancellationToken).ConfigureAwait(false);
            var cooled = await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
            return Done(RunOutcome.Completed, "任务已检查。冷却未结束，所以没有安排拉图。", cooled);
        }

        Say(progress, "正在安排一次单次拉图。大约 15–30 分钟后系统才可能去拉取，不是精确的 15 分钟整点。");
        var baseline = _snapshots.Read();
        var scheduled = await _package.ScheduleOneShotAsync(cancellationToken).ConfigureAwait(false);
        foreach (var line in scheduled.Lines)
            Say(progress, "  " + line);
        if (!scheduled.Scheduled)
            return Done(RunOutcome.Error, "安排失败。" + scheduled.Detail, null);

        if (scheduled.AlreadyExisted)
            Say(progress, "已有同名单次任务，没有重复注册。这仍然只表示任务在队列里。");
        else
            Say(progress, "已安排。系统大约会在接下来 15 到 30 分钟内自己拉取。锁屏或注销一般不会取消它。");

        var review = await _package.GetTaskReportAsync(cancellationToken).ConfigureAwait(false);
        if (review.HasOneShot)
            Say(progress, "复查：AllTasks 里能看到 oneshot。它不是正式的每日 UpdateTimer。");
        else
            Say(progress, "复查：注册调用已返回，但当前 AllTasks 里还没有看到 oneshot。请以复查为准，这不表示已经拉到图。");

        if (request.InjectFallback)
            await InjectNotesAsync(progress, cancellationToken).ConfigureAwait(false);

        if (!request.Wait)
        {
            Say(progress, "若要在这里观察结果，请加上 --wait，或勾选「安排后等待观察」。也可以稍后重新体检。");
            var scheduledReport = await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
            return Done(RunOutcome.Completed, "单次拉图已安排。大约 15–30 分钟后才可能看到新图。", scheduledReport);
        }

        Say(progress, "开始等待，最多约 35 分钟。到点没有变化也不表示定时器一定失败，它可能稍后再触发。");
        var observed = await WaitForChangeAsync(baseline, progress, cancellationToken).ConfigureAwait(false);
        var finalReport = await DiagnoseAsync(progress, cancellationToken).ConfigureAwait(false);
        if (observed)
            return Done(RunOutcome.Completed, "等待期间观察到壁纸、拉取记录或缓存有变化。", finalReport);

        Say(progress, "等待超时。定时器可能仍会稍后触发，请过几分钟再体检。");
        return Done(RunOutcome.WaitTimeout, "等待约 35 分钟后没有观察到变化。", finalReport);
    }

    private void OpenGateOnly(IProgress<string>? progress)
    {
        using var session = RegistrySession.Begin("open-gate");
        var snapshot = _snapshots.Read();
        foreach (var note in SpotlightMutator.OpenGate(session, snapshot, DateTime.UtcNow))
            Say(progress, note);
        session.Commit();
        Say(progress, "本次修改已备份到：" + session.DirectoryPath);
    }

    private ToolResult Rollback(IProgress<string>? progress)
    {
        var message = BackupStore.RollbackLatest();
        Say(progress, message);
        if (message.StartsWith("已按备份", StringComparison.Ordinal) || message.Contains("无需回滚", StringComparison.Ordinal))
            return Done(RunOutcome.Completed, message, null);
        return Done(RunOutcome.Error, message, null);
    }

    private async Task<ToolResult> ClearOneShotAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Say(progress, "只注销名字完全等于 " + TaskCatalog.OneShotName + " 的单次任务，不会删除正式的 " + TaskCatalog.UpdateTimer + "。");
        var result = await _package.RemoveOneShotAsync(cancellationToken).ConfigureAwait(false);
        foreach (var line in result.Lines)
            Say(progress, "  " + line);
        if (!result.Success)
            return Done(RunOutcome.Error, result.Detail, null);
        if (result.Lines.Any(l => l == "REMOVED=1"))
            return Done(RunOutcome.Completed, "已注销单次任务。", null);
        return Done(RunOutcome.Completed, "当前没有这个单次任务。", null);
    }

    private async Task InjectNotesAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var notes = await _fallback.InjectAsync(cancellationToken).ConfigureAwait(false);
        foreach (var note in notes)
            Say(progress, note);
    }

    private async Task<bool> WaitForChangeAsync(SpotlightSnapshot baseline, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var current = _snapshots.Read();
            if (ChangeDetector.HasObservedChange(baseline, current))
            {
                Say(progress, "观察到变化：壁纸=" + Path.GetFileName(current.Wallpaper)
                    + "；success=" + (current.StateSuccess?.ToString() ?? "未知")
                    + "；完整图 " + current.IrisFullSizeCount
                    + "；88000820 缓存=" + (current.HasCache88000820 ? "有" : "无"));
                return true;
            }

            Say(progress, DateTime.Now.ToString("HH:mm:ss") + "  仍在等待…");
        }

        return false;
    }

    private async Task<DiagnosisReport> DiagnoseAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Say(progress, "正在读取本机状态…");
        var os = OsInfoReader.Read();
        var snapshot = _snapshots.Read();
        Say(progress, "正在进入系统包身份枚举后台任务…");
        var tasks = await _package.GetTaskReportAsync(cancellationToken).ConfigureAwait(false);
        Say(progress, "正在探测微软图库接口…");
        var iris = await _iris.ProbeAsync(
            string.IsNullOrWhiteSpace(snapshot.CacheRequestUri) ? null : snapshot.CacheRequestUri,
            cancellationToken).ConfigureAwait(false);
        var report = DiagnosisComposer.Create(os, snapshot, tasks, iris, DateTime.UtcNow, Environment.MachineName);
        Say(progress, report.Text);
        return report;
    }

    private void Say(IProgress<string>? progress, string message)
    {
        _log.Info(message);
        progress?.Report(message);
    }

    private static ToolResult Done(RunOutcome outcome, string summary, DiagnosisReport? report) =>
        new()
        {
            Outcome = outcome,
            Summary = summary,
            Report = report
        };
}
