namespace DesktopSpotlightFix.Backup;

public sealed class BackupEntry
{
    public string SubKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalKind { get; set; } = RollbackPlanner.Missing;
    public string? OriginalValue { get; set; }
    public string? WrittenKind { get; set; }
    public string? WrittenValue { get; set; }
}

public sealed class BackupManifest
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string Operation { get; set; } = "";
    public List<BackupEntry> Entries { get; set; } = [];
}

public enum RollbackDecision
{
    Restore,
    SkipNotWritten,
    Conflict
}

public static class RollbackPlanner
{
    public const string Missing = "Missing";

    public static RollbackDecision Judge(BackupEntry entry, string currentKind, string? currentValue)
    {
        if (string.IsNullOrEmpty(entry.WrittenKind))
            return RollbackDecision.SkipNotWritten;

        var writtenValue = entry.WrittenValue ?? "";
        var current = currentValue ?? "";
        if (!string.Equals(entry.WrittenKind, currentKind, StringComparison.Ordinal) ||
            !string.Equals(writtenValue, current, StringComparison.Ordinal))
            return RollbackDecision.Conflict;

        return RollbackDecision.Restore;
    }
}
