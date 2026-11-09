using System.Text;

namespace DesktopSpotlightFix.Core;

public static class DiagnosisComposer
{
    public static IReadOnlyList<Finding> BuildFindings(
        OsInfo os,
        SpotlightSnapshot snapshot,
        TaskReport tasks,
        IrisProbeResult iris,
        DateTime utcNow)
    {
        var list = new List<Finding>();
        var missing = TaskCatalogMissing(tasks.Tasks);

        list.Add(new Finding(
            os.CbsFolderExists ? FindingLevel.Ok : FindingLevel.Fail,
            "CBS 系统包",
            os.CbsFolderExists ? os.CbsVersion : "未找到 MicrosoftWindows.Client.CBS",
            "关键项"));

        list.Add(new Finding(
            CbsOk(tasks) ? FindingLevel.Ok : FindingLevel.Fail,
            "包身份",
            !tasks.EnteredDesktopPackage
                ? "未能进入 Global.DesktopSpotlight" + Suffix(tasks.DesktopError)
                : CbsOk(tasks)
                    ? tasks.Aumid
                    : "已执行，但没有确认到 Global.DesktopSpotlight 身份：" + (string.IsNullOrWhiteSpace(tasks.Aumid) ? "(无)" : tasks.Aumid)));

        list.Add(new Finding(
            tasks.HasUpdateTimer ? FindingLevel.Ok : FindingLevel.Fail,
            "每日拉图任务",
            tasks.HasUpdateTimer ? "已注册 UpdateTimer" : "缺失 UpdateTimer，系统不会自己去拉新图"));

        list.Add(WallpaperFinding(snapshot));
        list.Add(new Finding(
            snapshot.PolicyHits.Count > 0 ? FindingLevel.Fail : FindingLevel.Ok,
            "策略拦截",
            snapshot.PolicyHits.Count > 0
                ? string.Join("；", snapshot.PolicyHits)
                : "未发现 CloudContent 禁用策略"));

        list.Add(new Finding(
            missing.Count > 0 ? FindingLevel.Warn : FindingLevel.Ok,
            "8 个核心后台任务",
            missing.Count > 0 ? "缺 " + missing.Count + " 个：" + string.Join(", ", missing) : "8/8",
            "重要项"));

        list.Add(AccessFinding(tasks.Access));
        var fullText = "完整图 " + snapshot.IrisFullSizeCount + " 张，文件共 " + snapshot.IrisFileCount + " 个"
            + (snapshot.IrisCountTruncated ? "（统计已截断）" : "")
            + (snapshot.IrisFolderExists ? "" : "；下载目录不存在");
        list.Add(new Finding(
            snapshot.IrisFullSizeCount > 0 ? FindingLevel.Ok : FindingLevel.Warn,
            "已下载的桌面图",
            fullText));

        list.Add(CacheFinding(snapshot));
        var successText = snapshot.StateSuccess switch
        {
            true => "true",
            false => "false",
            _ => "未知"
        };
        list.Add(new Finding(
            snapshot.StateSuccess == true ? FindingLevel.Ok : FindingLevel.Warn,
            "上次官方拉取",
            "success=" + successText
            + "；上次尝试 " + GateCooldown.FormatWhen(snapshot.LastAttempt, utcNow)
            + "；上次任务 " + GateCooldown.FormatWhen(snapshot.LastTaskRun, utcNow)
            + "；UpdateTimer " + GateCooldown.FormatWhen(snapshot.UpdateTimer, utcNow)
            + (string.IsNullOrWhiteSpace(snapshot.StatusCode) ? "" : "；状态码 " + snapshot.StatusCode)
            + (snapshot.StateJsonUnreadable ? "；State 无法按已知结构读取，已跳过" : "")));

        list.Add(new Finding(
            FindingLevel.Info,
            "背景类型",
            snapshot.BackgroundType == 3
                ? "3（Windows 聚焦）"
                : snapshot.BackgroundType + "（当前不是 Windows 聚焦，后台任务不会拉桌面图）",
            "信息"));

        var reason = snapshot.DisabledReason?.ToString() ?? "(无)";
        var reasonNote = snapshot.DisabledReason == 100
            ? "。reason=100 单独出现通常不是故障"
            : "";
        list.Add(new Finding(
            FindingLevel.Info,
            "EnabledState",
            (snapshot.EnabledState?.ToString() ?? "(无)") + "    DisabledReason=" + reason + reasonNote));

        var gate = GateCooldown.GetOpensUtc(snapshot.WallpaperRefresh, snapshot.Rotation, utcNow);
        if (gate.HasValue)
        {
            list.Add(new Finding(
                FindingLevel.Warn,
                "60 分钟冷却",
                "关闭中，约 " + gate.Value.ToLocalTime().ToString("HH:mm") + " 后才会允许拉新图。最近改壁纸 "
                + GateCooldown.FormatWhen(snapshot.WallpaperRefresh, utcNow)));
        }
        else
        {
            list.Add(new Finding(FindingLevel.Info, "60 分钟冷却", "当前开放。最近改壁纸 " + GateCooldown.FormatWhen(snapshot.WallpaperRefresh, utcNow)));
        }

        var irisLevel = !iris.Reachable ? FindingLevel.Fail : iris.HasImages ? FindingLevel.Ok : FindingLevel.Warn;
        list.Add(new Finding(irisLevel, "微软图库接口", iris.Detail));

        if (tasks.HasOneShot)
        {
            list.Add(new Finding(
                FindingLevel.Info,
                "已安排的单次拉图",
                "存在 oneshot 定时器。它不是正式每日任务，大约 15–30 分钟内可能触发，到点不一定准时。"));
        }

        return list;
    }

    public static DiagnosisReport Create(
        OsInfo os,
        SpotlightSnapshot snapshot,
        TaskReport tasks,
        IrisProbeResult iris,
        DateTime utcNow,
        string machineName)
    {
        var findings = BuildFindings(os, snapshot, tasks, iris, utcNow);
        var advice = AdviceBuilder.Build(snapshot, tasks, iris, utcNow, os.CbsFolderExists);
        var needs = !AdviceBuilder.IsHealthy(advice) || !tasks.EnteredDesktopPackage;
        return new DiagnosisReport
        {
            Os = os,
            Snapshot = snapshot,
            Tasks = tasks,
            Iris = iris,
            Findings = findings,
            Advice = advice,
            NeedsAttention = needs,
            Text = Render(os, findings, advice, utcNow.ToLocalTime(), machineName)
        };
    }

    public static string Render(
        OsInfo os,
        IReadOnlyList<Finding> findings,
        IReadOnlyList<string> advice,
        DateTime localNow,
        string machineName)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("桌面聚焦体检  " + machineName + "  " + localNow.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine("系统：" + os.ProductName + "  " + os.DisplayVersion + "  " + os.CurrentBuild + "." + os.Ubr
            + "    CBS：" + (os.CbsFolderExists ? os.CbsVersion : "未找到"));
        sb.AppendLine();
        string? section = null;
        foreach (var finding in findings)
        {
            if (!string.IsNullOrEmpty(finding.Section) && finding.Section != section)
            {
                section = finding.Section;
                sb.AppendLine("== " + section + " ==");
            }

            var tag = finding.Level switch
            {
                FindingLevel.Ok => "[正常]",
                FindingLevel.Warn => "[注意]",
                FindingLevel.Fail => "[问题]",
                _ => "[信息]"
            };
            sb.AppendLine(tag.PadRight(6) + " " + finding.Label.PadRight(18) + " " + finding.Detail);
        }

        sb.AppendLine();
        sb.AppendLine("建议：");
        foreach (var line in advice)
            sb.AppendLine("- " + line);
        sb.AppendLine();
        sb.AppendLine("说明：安排成功只表示系统任务已登记，大约 15–30 分钟后才可能拉图，不表示壁纸已经换好。");
        return sb.ToString();
    }

    private static bool CbsOk(TaskReport tasks) =>
        tasks.EnteredDesktopPackage &&
        tasks.Aumid.Contains("!Global.DesktopSpotlight", StringComparison.OrdinalIgnoreCase);

    private static List<string> TaskCatalogMissing(IReadOnlyList<string> present) =>
        PackageIdentity.TaskCatalog.Missing(present).ToList();

    private static Finding WallpaperFinding(SpotlightSnapshot snapshot)
    {
        if (snapshot.IsSystemFallbackWallpaper)
            return new Finding(FindingLevel.Warn, "当前壁纸", "仍是系统自带底图 image_N.jpg");
        if (string.IsNullOrWhiteSpace(snapshot.Wallpaper))
            return new Finding(FindingLevel.Warn, "当前壁纸", "(空)");
        var leaf = Path.GetFileName(snapshot.Wallpaper);
        var note = snapshot.IsInjectedWallpaper ? "（本工具或同类方式写入的备用图，不能单独证明官方每日拉图）" : "";
        return new Finding(FindingLevel.Ok, "当前壁纸", leaf + note);
    }

    private static Finding CacheFinding(SpotlightSnapshot snapshot)
    {
        if (!snapshot.HasCache88000820)
        {
            return new Finding(
                FindingLevel.Warn,
                "桌面内容缓存",
                "没有 placement=88000820 记录。这只说明当前缺少近期桌面拉图成功的证据，缓存可能被清掉过，不能据此断言历史上从未成功。");
        }

        if (snapshot.CacheJsonHasImage)
        {
            return new Finding(
                FindingLevel.Ok,
                "桌面内容缓存",
                "存在 88000820 记录，状态码 " + Display(snapshot.CacheStatusCode) + "，内容里有图片字段");
        }

        return new Finding(
            FindingLevel.Warn,
            "桌面内容缓存",
            "存在 88000820 记录，状态码 " + Display(snapshot.CacheStatusCode) + "，没有看到图片字段");
    }

    private static Finding AccessFinding(string access)
    {
        if (access.Contains("Denied", StringComparison.OrdinalIgnoreCase))
            return new Finding(FindingLevel.Fail, "后台执行权限", access);
        if (access.Contains("Allowed", StringComparison.OrdinalIgnoreCase))
            return new Finding(FindingLevel.Ok, "后台执行权限", access);
        return new Finding(FindingLevel.Warn, "后台执行权限", string.IsNullOrWhiteSpace(access) ? "未知" : access);
    }

    private static string Suffix(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "" : "。" + error;

    private static string Display(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(无)" : value;
}
