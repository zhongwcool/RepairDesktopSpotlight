namespace DesktopSpotlightFix.Core;

public static class AdviceBuilder
{
    public const string HealthyMessage =
        "看起来桌面聚焦管道是健康的，一般无需处理。新图通常每天自动换一次，不是每点一次「下一个背景」就从网上抓一张。";

    public static IReadOnlyList<string> Build(
        SpotlightSnapshot snapshot,
        TaskReport tasks,
        IrisProbeResult iris,
        DateTime utcNow,
        bool cbsPresent = true)
    {
        var advice = new List<string>();
        if (!cbsPresent)
            advice.Add("没有找到系统包目录 MicrosoftWindows.Client.CBS，桌面聚焦后台任务无法注册。");
        if (snapshot.BackgroundType != 3)
            advice.Add("先在「设置 → 个性化 → 背景」选择 Windows 聚焦。");
        if (snapshot.PolicyHits.Count > 0)
            advice.Add("发现 CloudContent 策略值为 1，聚焦可能被策略关掉。本工具不会修改组策略。");
        if (tasks.Access.Contains("Denied", StringComparison.OrdinalIgnoreCase))
            advice.Add("后台执行权限是拒绝状态。本工具不会改系统的后台执行策略。");

        if (!tasks.EnteredDesktopPackage)
            advice.Add("没能进入 MicrosoftWindows.Client.CBS 的 Global.DesktopSpotlight 身份，无法确认或注册后台任务。"
                + (string.IsNullOrWhiteSpace(tasks.DesktopError) ? "" : " " + tasks.DesktopError));
        else if (!tasks.HasUpdateTimer)
            advice.Add("缺少每日拉图任务。请使用「一键修复」，或命令行：DesktopSpotlightFix.exe --repair");
        else if (!HealthRules.ShouldSkipOneShot(snapshot, true))
        {
            advice.Add("任务在，但现有证据还不能说明官方每日拉图是健康的。历史上的 success、单独一份缓存，或手工注入的图，都不能单独算数。请使用「强制刷新」，或命令行：DesktopSpotlightFix.exe --force");
            var gate = GateCooldown.GetOpensUtc(snapshot.WallpaperRefresh, snapshot.Rotation, utcNow);
            if (gate.HasValue)
                advice.Add("若不想等冷却结束，可加上 --open-gate。窗口里的「强制刷新」会直接清掉这段等待。冷却大约到 " + gate.Value.ToLocalTime().ToString("HH:mm") + "。");
        }

        if (!iris.Reachable)
            advice.Add("当前访问不了微软图库接口 fd.api.iris.microsoft.com。请检查代理、TUN 或防火墙。本工具不会关闭它们。");
        else if (!iris.HasImages)
            advice.Add("接口能通，但没有返回壁纸内容。可能是地区或内容源暂时没有桌面图。可用「放入备用图」先垫一组图；这不能代替官方每日拉图。");

        if (advice.Count == 0)
            advice.Add(HealthyMessage);
        return advice;
    }

    public static bool IsHealthy(IReadOnlyList<string> advice) =>
        advice.Count == 1 && advice[0] == HealthyMessage;
}
