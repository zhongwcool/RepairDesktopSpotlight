namespace DesktopSpotlightFix.Core;

public sealed record SpotlightSnapshot
{
    public int? BackgroundType { get; init; }
    public string Wallpaper { get; init; } = "";
    public bool IsSystemFallbackWallpaper { get; init; }
    public bool IsInjectedWallpaper { get; init; }
    public int? EnabledState { get; init; }
    public int? DisabledReason { get; init; }
    public string UpdateTimer { get; init; } = "";
    public string WallpaperRefresh { get; init; } = "";
    public string Rotation { get; init; } = "";
    public int? ImagesUsed { get; init; }
    public int? ImageIndex { get; init; }
    public bool? StateSuccess { get; init; }
    public string StateSuccessDate { get; init; } = "";
    public string LastAttempt { get; init; } = "";
    public string StatusCode { get; init; } = "";
    public string LastTaskRun { get; init; } = "";
    public bool StateJsonUnreadable { get; init; }
    public bool IrisFolderExists { get; init; }
    public string IrisFolder { get; init; } = "";
    public int IrisFileCount { get; init; }
    public int IrisFullSizeCount { get; init; }
    public bool IrisCountTruncated { get; init; }
    public bool HasCache88000820 { get; init; }
    public string CacheStatusCode { get; init; } = "";
    public bool CacheJsonHasImage { get; init; }
    public string CacheRequestUri { get; init; } = "";
    public string CacheRefresh { get; init; } = "";
    public string CacheExpiry { get; init; } = "";
    public IReadOnlyList<string> PolicyHits { get; init; } = [];
}

public sealed record OsInfo(
    string ProductName,
    string DisplayVersion,
    string CurrentBuild,
    string Ubr,
    string CbsVersion,
    bool CbsFolderExists);

public enum FindingLevel
{
    Info,
    Ok,
    Warn,
    Fail
}

public sealed record Finding(FindingLevel Level, string Label, string Detail, string? Section = null)
{
    public string LevelText => Level switch
    {
        FindingLevel.Ok => "正常",
        FindingLevel.Warn => "注意",
        FindingLevel.Fail => "问题",
        _ => "信息"
    };
}

public sealed record IrisProbeResult(bool Reachable, bool HasImages, string Detail);

public sealed record TaskReport
{
    public IReadOnlyList<string> Tasks { get; init; } = [];
    public string Aumid { get; init; } = "";
    public string Access { get; init; } = "";
    public bool EnteredDesktopPackage { get; init; }
    public string? DesktopError { get; init; }
    public string? IrisError { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];

    public bool HasUpdateTimer => Tasks.Contains(PackageIdentity.TaskCatalog.UpdateTimer, StringComparer.Ordinal);

    public bool HasOneShot => Tasks.Contains(PackageIdentity.TaskCatalog.OneShotName, StringComparer.Ordinal);
}

public enum RunOutcome
{
    DiagnoseOk,
    DiagnoseNeedsAttention,
    Completed,
    WaitTimeout,
    Cancelled,
    Error
}

public static class ExitCodes
{
    public static int For(RunOutcome outcome) => outcome switch
    {
        RunOutcome.DiagnoseOk => 0,
        RunOutcome.DiagnoseNeedsAttention => 1,
        RunOutcome.Completed => 0,
        RunOutcome.WaitTimeout => 4,
        RunOutcome.Cancelled => 3,
        RunOutcome.Error => 2,
        _ => 2
    };
}

public sealed class ToolResult
{
    public required RunOutcome Outcome { get; init; }
    public required string Summary { get; init; }
    public DiagnosisReport? Report { get; init; }
    public int ExitCode => ExitCodes.For(Outcome);
}

public sealed class DiagnosisReport
{
    public required OsInfo Os { get; init; }
    public required SpotlightSnapshot Snapshot { get; init; }
    public required TaskReport Tasks { get; init; }
    public required IrisProbeResult Iris { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<string> Advice { get; init; }
    public required string Text { get; init; }
    public bool NeedsAttention { get; init; }
}

public sealed class ToolRequest
{
    public bool Diagnose { get; init; }
    public bool Repair { get; init; }
    public bool Force { get; init; }
    public bool OpenGate { get; init; }
    public bool InjectFallback { get; init; }
    public bool Wait { get; init; }
    public bool Rollback { get; init; }
    public bool ClearOneShot { get; init; }
    public bool ClearOldImages { get; init; }

    public string Describe()
    {
        var parts = new List<string>();
        if (Diagnose) parts.Add("diagnose");
        if (Repair) parts.Add("repair");
        if (Force) parts.Add("force");
        if (OpenGate) parts.Add("open-gate");
        if (InjectFallback) parts.Add("inject-fallback");
        if (Wait) parts.Add("wait");
        if (Rollback) parts.Add("rollback");
        if (ClearOneShot) parts.Add("clear-oneshot");
        if (ClearOldImages) parts.Add("clear-old-images");
        return parts.Count == 0 ? "diagnose" : string.Join(" ", parts);
    }
}
