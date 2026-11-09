namespace DesktopSpotlightFix.Core;

public enum GuidanceAction
{
    None,
    Repair,
    Refresh
}

public enum GuidanceTone
{
    Healthy,
    Repair,
    Refresh,
    Blocked,
    External,
    Neutral
}

public sealed record Guidance(string Title, string Message, GuidanceAction Action, GuidanceTone Tone);

/// <summary>
/// 把体检结果收成一句给普通用户看的结论：要不要修，以及主按钮该是哪一个。
/// </summary>
public static class UserGuidance
{
    public static Guidance ForResult(ToolResult result, DateTime utcNow)
    {
        if (result.Report == null)
            return WithoutReport(result);

        var guidance = Decide(result.Report, utcNow);
        if (result.Outcome is RunOutcome.DiagnoseOk or RunOutcome.DiagnoseNeedsAttention or RunOutcome.Error)
            return guidance;

        return AfterAction(guidance, result);
    }

    public static Guidance Decide(DiagnosisReport report, DateTime utcNow)
    {
        if (AdviceBuilder.IsHealthy(report.Advice))
            return Healthy(report);

        var snapshot = report.Snapshot;
        var tasks = report.Tasks;
        var iris = report.Iris;
        var denied = tasks.Access.Contains("Denied", StringComparison.OrdinalIgnoreCase);
        var canFix = report.Os.CbsFolderExists
            && tasks.EnteredDesktopPackage
            && snapshot.PolicyHits.Count == 0
            && !denied;

        var parts = new List<string>();
        if (!report.Os.CbsFolderExists)
            parts.Add("这台电脑缺少系统自带的聚焦组件，没法自动补任务。");
        if (!tasks.EnteredDesktopPackage)
            parts.Add("没能确认系统里的聚焦组件，所以不能补任务。");
        if (snapshot.PolicyHits.Count > 0)
            parts.Add("组策略关掉了聚焦。本工具不会改组策略。");
        if (denied)
            parts.Add("系统拒绝了后台任务。本工具不会改这项权限。");

        if (!canFix)
        {
            AppendIris(parts, iris);
            if (parts.Count == 0)
                parts.Add("检查到异常，但这里没有可以自动做的修复。");
            return new Guidance("这里修不了", Join(parts), GuidanceAction.None, GuidanceTone.Blocked);
        }

        var wrongBackground = snapshot.BackgroundType != 3;
        var missingTimer = !tasks.HasUpdateTimer;
        var weak = tasks.HasUpdateTimer && !HealthRules.ShouldSkipOneShot(snapshot, true);

        if (wrongBackground || missingTimer)
        {
            if (wrongBackground)
                parts.Add("当前背景不是「Windows 聚焦」，系统不会去拉桌面新图。");
            if (missingTimer)
                parts.Add("每天自动换图的任务不见了，壁纸会停在旧图上。");
            else if (weak)
                parts.Add("最近也没有成功拉到新图的迹象。");

            AppendIris(parts, iris);
            if (missingTimer || weak)
            {
                parts.Add("点「一键修复」。它会补上能补的部分；冷却结束后会安排一次刷新，大约 15–30 分钟后才可能看到新图。");
                AppendCooldown(parts, snapshot, utcNow);
            }
            else
            {
                parts.Add("点「一键修复」，把背景改回 Windows 聚焦。如果没有生效，请到「设置 → 个性化 → 背景」再选一次。");
            }

            return new Guidance("需要修复", Join(parts), GuidanceAction.Repair, GuidanceTone.Repair);
        }

        if (!iris.Reachable)
        {
            if (weak)
                parts.Add("换图任务还在，但最近没有成功拉到新图的迹象。");
            parts.Add("现在连不上微软的图库，安排刷新也拉不到。请检查网络、代理或防火墙。");
            return new Guidance("先不用修复", Join(parts), GuidanceAction.None, GuidanceTone.External);
        }

        if (weak)
        {
            parts.Add("换图任务还在，但最近没有成功拉到新图的迹象。");
            if (!iris.HasImages)
                parts.Add("图库暂时也没有返回壁纸。");
            parts.Add("点「强制刷新」。大约 15–30 分钟后才可能看到新图，不是马上换好。");
            AppendCooldown(parts, snapshot, utcNow);
            return new Guidance("建议刷新", Join(parts), GuidanceAction.Refresh, GuidanceTone.Refresh);
        }

        if (!iris.HasImages)
        {
            parts.Add("换图任务不用修。图库暂时没有返回壁纸，可以过一会儿再检查，或点「放入备用图」。");
            return new Guidance("先不用修复", Join(parts), GuidanceAction.None, GuidanceTone.External);
        }

        if (parts.Count == 0)
            parts.AddRange(report.Advice);
        return new Guidance("先不用修复", Join(parts), GuidanceAction.None, GuidanceTone.External);
    }

    private static Guidance Healthy(DiagnosisReport report)
    {
        var message = "桌面聚焦正常，不用修复。新图通常每天自动换一次；点「下一个背景」只会换已有的图。";
        if (report.Findings.Any(f => f.Level is FindingLevel.Warn or FindingLevel.Fail))
            message += "下面标成「注意」的项目，现在不用处理。";
        return new Guidance("不需要修复", message, GuidanceAction.None, GuidanceTone.Healthy);
    }

    private static Guidance AfterAction(Guidance guidance, ToolResult result)
    {
        var summary = result.Summary?.Trim() ?? "";
        switch (result.Outcome)
        {
            case RunOutcome.Error:
                return new Guidance("没有完成", Join([summary, guidance.Message]), GuidanceAction.None, GuidanceTone.Blocked);
            case RunOutcome.Cancelled:
                return new Guidance("已取消", summary, GuidanceAction.None, GuidanceTone.Neutral);
            case RunOutcome.WaitTimeout:
                return new Guidance(
                    "还没有看到新图",
                    Join([summary, "可以过几分钟再点「重新检查」。"]),
                    GuidanceAction.None,
                    GuidanceTone.Refresh);
        }

        if (guidance.Action == GuidanceAction.Repair)
            return new Guidance("需要修复", Join([summary, guidance.Message]), GuidanceAction.Repair, GuidanceTone.Repair);

        if (summary.Contains("已安排", StringComparison.Ordinal))
        {
            var follow = guidance.Tone == GuidanceTone.External ? guidance.Message : "到点之前不用再点。";
            var tone = guidance.Tone == GuidanceTone.External ? GuidanceTone.External : GuidanceTone.Refresh;
            return new Guidance("已经安排好", Join([summary, follow]), GuidanceAction.None, tone);
        }

        if (guidance.Tone == GuidanceTone.Blocked)
            return new Guidance(guidance.Title, Join([summary, guidance.Message]), GuidanceAction.None, GuidanceTone.Blocked);

        if (guidance.Tone == GuidanceTone.External)
            return new Guidance(guidance.Title, Join([summary, guidance.Message]), GuidanceAction.None, GuidanceTone.External);

        if (guidance.Tone == GuidanceTone.Healthy)
            return new Guidance("不需要修复", Join([summary, "现在不需要再修复。"]), GuidanceAction.None, GuidanceTone.Healthy);

        return new Guidance(guidance.Title, Join([summary, guidance.Message]), guidance.Action, guidance.Tone);
    }

    private static Guidance WithoutReport(ToolResult result)
    {
        var summary = string.IsNullOrWhiteSpace(result.Summary) ? "没有更多说明。" : result.Summary.Trim();
        return result.Outcome switch
        {
            RunOutcome.Error => new Guidance("没有完成", summary, GuidanceAction.None, GuidanceTone.Blocked),
            RunOutcome.Cancelled => new Guidance("已取消", summary, GuidanceAction.None, GuidanceTone.Neutral),
            RunOutcome.WaitTimeout => new Guidance("还没有看到新图", summary, GuidanceAction.None, GuidanceTone.Refresh),
            _ => new Guidance("已完成", summary, GuidanceAction.None, GuidanceTone.Neutral)
        };
    }

    private static void AppendIris(List<string> parts, IrisProbeResult iris)
    {
        if (!iris.Reachable)
            parts.Add("另外，现在连不上微软的图库。请检查网络、代理或防火墙。");
        else if (!iris.HasImages)
            parts.Add("另外，图库暂时没有返回壁纸。可以过一会儿再检查，或点「放入备用图」。");
    }

    private static void AppendCooldown(List<string> parts, SpotlightSnapshot snapshot, DateTime utcNow)
    {
        var gate = GateCooldown.GetOpensUtc(snapshot.WallpaperRefresh, snapshot.Rotation, utcNow);
        if (!gate.HasValue)
            return;

        parts.Add("现在还在冷却，大约 " + gate.Value.ToLocalTime().ToString("HH:mm")
            + " 之后才会允许拉图。点「强制刷新」会把这段等待清掉。");
    }

    private static string Join(IEnumerable<string> lines) =>
        string.Join(Environment.NewLine, lines.Where(static line => !string.IsNullOrWhiteSpace(line)));
}
