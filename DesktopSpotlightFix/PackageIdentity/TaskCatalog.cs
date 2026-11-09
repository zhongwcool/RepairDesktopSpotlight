namespace DesktopSpotlightFix.PackageIdentity;

public static class TaskCatalog
{
    public const string PackageFamilyName = "MicrosoftWindows.Client.CBS_cw5n1h2txyewy";
    public const string DesktopAppId = "Global.DesktopSpotlight";
    public const string IrisAppId = "Global.IrisService";
    public const string UpdateTimer = "DesktopSpotlight.BackgroundTask.UpdateTimer";
    public const string OneShotName = "DesktopSpotlight.BackgroundTask.UpdateTimer.oneshot";
    public const string Placement = "88000820";

    public static readonly string[] CoreTasks =
    [
        "DesktopSpotlight.BackgroundTask.UpdateTimer",
        "DesktopSpotlight.BackgroundTask.Maintenance",
        "DesktopSpotlight.BackgroundTask.RegistrationStatusCheck",
        "DesktopSpotlight.BackgroundTask.OnlineIdChange",
        "IrisService.BackgroundTask.UpdateTimer",
        "IrisService.BackgroundTask.Maintenance",
        "IrisService.BackgroundTask.OnlineIdChange",
        "IrisService.BackgroundTask.NotificationHandler"
    ];

    public static bool IsAllowedAppId(string appId) =>
        appId == DesktopAppId || appId == IrisAppId;

    /// <summary>
    /// 只允许注销这个精确名字。正式每日任务 UpdateTimer 以及其他核心任务一律拒绝。
    /// </summary>
    public static bool IsRemovableOneShotName(string? name) =>
        string.Equals(name, OneShotName, StringComparison.Ordinal);

    public static IReadOnlyList<string> Missing(IEnumerable<string> present)
    {
        var set = new HashSet<string>(present, StringComparer.Ordinal);
        return CoreTasks.Where(t => !set.Contains(t)).ToArray();
    }
}
