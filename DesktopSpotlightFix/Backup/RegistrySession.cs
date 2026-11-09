using System.Globalization;
using Microsoft.Win32;

namespace DesktopSpotlightFix.Backup;

public static class RegistryPaths
{
    public const string Desktop = @"Software\Microsoft\Windows\CurrentVersion\DesktopSpotlight";
    public const string Settings = Desktop + @"\Settings";
    public const string Creatives = Desktop + @"\Creatives";
    public const string Iris = @"Software\Microsoft\Windows\CurrentVersion\IrisService";
    public const string IrisCache = Iris + @"\Cache";
    public const string Wallpapers = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers";
    public const string ControlPanelDesktop = @"Control Panel\Desktop";
    public const string CloudContent = @"Software\Policies\Microsoft\Windows\CloudContent";
    public const string WindowsVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
}

public static class RegistryRead
{
    public static (string Kind, string? Value) ReadHkcu(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return ReadKey(key, name);
    }

    public static (string Kind, string? Value) ReadKey(RegistryKey? key, string name)
    {
        if (key == null)
            return (RollbackPlanner.Missing, null);
        var names = key.GetValueNames();
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (RollbackPlanner.Missing, null);

        var kind = key.GetValueKind(name);
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return (kind.ToString(), Box(kind, raw));
    }

    public static string Box(RegistryValueKind kind, object? raw)
    {
        try
        {
            return kind switch
            {
                RegistryValueKind.DWord => Convert.ToInt32(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => Convert.ToInt64(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.String or RegistryValueKind.ExpandString => raw as string ?? "",
                RegistryValueKind.MultiString => raw is string[] arr ? string.Join("\u001f", arr) : "",
                _ => raw?.ToString() ?? ""
            };
        }
        catch (Exception)
        {
            return raw?.ToString() ?? "";
        }
    }

    public static string? GetString(RegistryKey key, string name)
    {
        var raw = key.GetValue(name);
        return raw as string ?? (raw == null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture));
    }

    public static int? GetInt(RegistryKey? key, string name)
    {
        if (key == null)
            return null;
        var raw = key.GetValue(name);
        if (raw == null)
            return null;
        try
        {
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// 修复前备份、写入、以及把「我们写下的值」记进清单，供回滚比对。
/// </summary>
public sealed class RegistrySession : IDisposable
{
    private readonly BackupManifest _manifest;
    private readonly string _directory;
    private bool _committed;

    private RegistrySession(BackupManifest manifest, string directory)
    {
        _manifest = manifest;
        _directory = directory;
    }

    public string DirectoryPath => _directory;
    public BackupManifest Manifest => _manifest;

    public static RegistrySession Begin(string operation)
    {
        var root = AppPaths.BackupRoot;
        System.IO.Directory.CreateDirectory(root);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(root, id);
        var n = 0;
        while (System.IO.Directory.Exists(dir))
        {
            n++;
            dir = Path.Combine(root, id + "-" + n.ToString(CultureInfo.InvariantCulture));
        }

        System.IO.Directory.CreateDirectory(dir);
        var manifest = new BackupManifest
        {
            Schema = 1,
            Id = Path.GetFileName(dir),
            CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Operation = operation
        };
        return new RegistrySession(manifest, dir);
    }

    public void SetString(string subKey, string name, string value)
    {
        EnsureCaptured(subKey, name);
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException("无法写入注册表项 " + subKey);
        key.SetValue(name, value, RegistryValueKind.String);
        RecordWritten(subKey, name, "String", value);
    }

    public void SetDWord(string subKey, string name, int value)
    {
        EnsureCaptured(subKey, name);
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException("无法写入注册表项 " + subKey);
        key.SetValue(name, value, RegistryValueKind.DWord);
        RecordWritten(subKey, name, "DWord", value.ToString(CultureInfo.InvariantCulture));
    }

    public void Commit()
    {
        if (_committed)
            return;
        var path = Path.Combine(_directory, "manifest.json");
        var json = System.Text.Json.JsonSerializer.Serialize(_manifest, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        _committed = true;
    }

    public void Dispose()
    {
        if (!_committed)
            Commit();
    }

    private void EnsureCaptured(string subKey, string name)
    {
        if (_manifest.Entries.Any(e =>
                e.SubKey.Equals(subKey, StringComparison.OrdinalIgnoreCase) &&
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        var (kind, value) = RegistryRead.ReadHkcu(subKey, name);
        _manifest.Entries.Add(new BackupEntry
        {
            SubKey = subKey,
            Name = name,
            OriginalKind = kind,
            OriginalValue = value
        });
    }

    private void RecordWritten(string subKey, string name, string kind, string value)
    {
        var entry = _manifest.Entries.First(e =>
            e.SubKey.Equals(subKey, StringComparison.OrdinalIgnoreCase) &&
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        entry.WrittenKind = kind;
        entry.WrittenValue = value;
    }
}

public static class BackupStore
{
    public static BackupManifest? LoadLatest()
    {
        if (!System.IO.Directory.Exists(AppPaths.BackupRoot))
            return null;

        BackupManifest? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(AppPaths.BackupRoot))
        {
            if (Core.PathSafety.IsReparsePoint(dir))
                continue;
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath) || Core.PathSafety.IsReparsePoint(manifestPath))
                continue;
            BackupManifest? manifest;
            try
            {
                var json = File.ReadAllText(manifestPath);
                manifest = System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(json, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception)
            {
                continue;
            }

            if (manifest == null || manifest.Schema != 1)
                continue;
            if (!DateTime.TryParse(manifest.CreatedUtc, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var created))
                created = System.IO.Directory.GetCreationTimeUtc(dir);
            if (created >= bestTime)
            {
                bestTime = created;
                best = manifest;
            }
        }

        return best;
    }

    public static string RollbackLatest()
    {
        var manifest = LoadLatest();
        if (manifest == null)
            return "没有找到本工具的备份，未改任何注册表。";

        var plan = new List<(BackupEntry Entry, RollbackDecision Decision)>();
        foreach (var entry in manifest.Entries)
        {
            var (kind, value) = RegistryRead.ReadHkcu(entry.SubKey, entry.Name);
            var decision = RollbackPlanner.Judge(entry, kind, value);
            if (decision == RollbackDecision.Conflict)
            {
                return "回滚已中止。" + entry.SubKey + "\\" + entry.Name
                    + " 的当前值与本工具写下的值不一致，可能之后又被改过。没有写回任何项。备份 "
                    + manifest.Id + "。";
            }

            plan.Add((entry, decision));
        }

        if (plan.All(p => p.Decision == RollbackDecision.SkipNotWritten))
            return "最近一次备份（" + manifest.Id + "）里没有已写入的项，无需回滚。";

        foreach (var (entry, decision) in plan)
        {
            if (decision != RollbackDecision.Restore)
                continue;
            Restore(entry);
        }

        return "已按备份 " + manifest.Id + " 回滚本工具写下的项。";
    }

    private static void Restore(BackupEntry entry)
    {
        if (entry.OriginalKind == RollbackPlanner.Missing)
        {
            using var key = Registry.CurrentUser.OpenSubKey(entry.SubKey, writable: true);
            key?.DeleteValue(entry.Name, throwOnMissingValue: false);
            return;
        }

        using var writable = Registry.CurrentUser.CreateSubKey(entry.SubKey, writable: true)
            ?? throw new InvalidOperationException("无法写回 " + entry.SubKey);
        switch (entry.OriginalKind)
        {
            case "String":
                writable.SetValue(entry.Name, entry.OriginalValue ?? "", RegistryValueKind.String);
                break;
            case "ExpandString":
                writable.SetValue(entry.Name, entry.OriginalValue ?? "", RegistryValueKind.ExpandString);
                break;
            case "DWord":
                if (!int.TryParse(entry.OriginalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dword))
                    throw new InvalidOperationException("无法回滚 DWORD " + entry.Name);
                writable.SetValue(entry.Name, dword, RegistryValueKind.DWord);
                break;
            case "QWord":
                if (!long.TryParse(entry.OriginalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qword))
                    throw new InvalidOperationException("无法回滚 QWORD " + entry.Name);
                writable.SetValue(entry.Name, qword, RegistryValueKind.QWord);
                break;
            default:
                throw new InvalidOperationException("不回滚未支持的注册表类型 " + entry.OriginalKind + "：" + entry.Name);
        }
    }
}
