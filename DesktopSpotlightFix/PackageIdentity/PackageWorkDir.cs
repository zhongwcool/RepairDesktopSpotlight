using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace DesktopSpotlightFix.PackageIdentity;

public sealed class WorkDirMarker
{
    public string Tool { get; set; } = "DesktopSpotlightFix";
    public string Marker { get; set; } = "dsfix-owned-v1";
    public string UserSid { get; set; } = "";
    public int SessionId { get; set; }
    public string CreatedUtc { get; set; } = "";
    public int? HelperPid { get; set; }
    public string? HelperStartUtc { get; set; }
    public string Nonce { get; set; } = "";
}

public sealed class PackageWorkDir : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly Logging.FileLogger? _log;
    private bool _deleted;

    private PackageWorkDir(string path, Logging.FileLogger? log)
    {
        Path = path;
        _log = log;
    }

    public string Path { get; }
    public static PackageWorkDir? Active { get; private set; }

    public static string CurrentUserSid()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static PackageWorkDir Create(string nonce, Logging.FileLogger? log)
    {
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        var dir = System.IO.Path.Combine(temp, "dsfix-" + Guid.NewGuid().ToString("N"));
        if (!Core.PathSafety.IsStrictSubPath(temp, dir))
            throw new InvalidOperationException("临时目录没有落在 TEMP 下面，已拒绝创建。");
        Directory.CreateDirectory(dir);
        if (Core.PathSafety.IsReparsePoint(dir))
            throw new InvalidOperationException("临时目录是重解析点，已拒绝使用。");

        var marker = new WorkDirMarker
        {
            UserSid = CurrentUserSid(),
            SessionId = Process.GetCurrentProcess().SessionId,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            Nonce = nonce
        };
        var work = new PackageWorkDir(dir, log);
        work.WriteMarker(marker);
        Active = work;
        return work;
    }

    public void MarkHelper(Process process)
    {
        var marker = ReadMarker(Path) ?? new WorkDirMarker();
        marker.HelperPid = process.Id;
        try
        {
            marker.HelperStartUtc = process.StartTime.ToUniversalTime().ToString("o");
        }
        catch (Exception)
        {
            marker.HelperStartUtc = DateTime.UtcNow.ToString("o");
        }

        WriteMarker(marker);
    }

    public void Abandon()
    {
        if (Active == this)
            Active = null;
        _deleted = true;
        _log?.Error("无法确认 helper 已退出，保留临时目录，下次启动再清理：" + Path);
    }

    public async Task ReleaseAsync()
    {
        if (_deleted)
            return;
        for (var i = 0; i < 10; i++)
        {
            if (TryDelete(Path, out _))
            {
                _deleted = true;
                if (Active == this)
                    Active = null;
                return;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        Abandon();
    }

    public void Dispose()
    {
        if (Active == this)
            Active = null;
        if (_deleted)
            return;
        if (!TryDelete(Path, out var error))
            _log?.Warn("临时目录未能删除：" + Path + " " + error);
        else
            _deleted = true;
    }

    public static void CleanupStale(Logging.FileLogger? log)
    {
        var sid = CurrentUserSid();
        if (string.IsNullOrEmpty(sid))
        {
            log?.Warn("读不到当前用户 SID，跳过临时目录清理。");
            return;
        }

        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(temp, "dsfix-*");
        }
        catch (Exception ex)
        {
            log?.Warn("无法枚举临时目录：" + ex.Message);
            return;
        }

        foreach (var dir in dirs)
        {
            try
            {
                if (Active != null && string.Equals(Core.PathSafety.Normalize(Active.Path), Core.PathSafety.Normalize(dir), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Core.PathSafety.IsReparsePoint(dir))
                {
                    log?.Info("跳过重解析点：" + dir);
                    continue;
                }

                var full = System.IO.Path.GetFullPath(dir);
                if (!Core.PathSafety.IsStrictSubPath(temp, full))
                    continue;

                var markerPath = System.IO.Path.Combine(full, "owner.json");
                if (!File.Exists(markerPath) || Core.PathSafety.IsReparsePoint(markerPath))
                    continue;

                var marker = ReadMarker(full);
                if (marker == null || marker.Tool != "DesktopSpotlightFix" || marker.Marker != "dsfix-owned-v1")
                    continue;
                if (!string.Equals(marker.UserSid, sid, StringComparison.Ordinal))
                    continue;
                if (HelperStillRunning(marker))
                    continue;
                if (!TryDelete(full, out var error))
                    log?.Warn("残留目录未删掉：" + full + " " + error);
                else
                    log?.Info("已清理残留临时目录：" + full);
            }
            catch (Exception ex)
            {
                log?.Warn("检查临时目录时出错：" + ex.Message);
            }
        }
    }

    public static void CleanupActiveBestEffort()
    {
        try
        {
            Active?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void WriteMarker(WorkDirMarker marker)
    {
        var path = System.IO.Path.Combine(Path, "owner.json");
        File.WriteAllText(path, JsonSerializer.Serialize(marker, JsonOptions), new System.Text.UTF8Encoding(false));
    }

    private static WorkDirMarker? ReadMarker(string dir)
    {
        try
        {
            var path = System.IO.Path.Combine(dir, "owner.json");
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<WorkDirMarker>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HelperStillRunning(WorkDirMarker marker)
    {
        if (marker.HelperPid is not int pid || pid <= 0)
        {
            if (DateTime.TryParse(marker.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var created) &&
                DateTime.UtcNow - created.ToUniversalTime() < TimeSpan.FromMinutes(2))
                return true;
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;
            if (!string.IsNullOrEmpty(marker.HelperStartUtc) &&
                DateTime.TryParse(marker.HelperStartUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var started))
            {
                var actual = process.StartTime.ToUniversalTime();
                if (Math.Abs((actual - started.ToUniversalTime()).TotalSeconds) > 3)
                    return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryDelete(string dir, out string error)
    {
        error = "";
        try
        {
            var full = System.IO.Path.GetFullPath(dir);
            var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!Core.PathSafety.IsStrictSubPath(temp, full))
            {
                error = "不在 TEMP 内";
                return false;
            }

            if (Core.PathSafety.IsReparsePoint(full))
            {
                error = "重解析点";
                return false;
            }

            var markerPath = System.IO.Path.Combine(full, "owner.json");
            byte[]? markerBytes = null;
            if (File.Exists(markerPath) && !Core.PathSafety.IsReparsePoint(markerPath))
                markerBytes = File.ReadAllBytes(markerPath);
            if (markerBytes == null)
            {
                error = "没有归属标记";
                return false;
            }

            DeleteChildren(full);
            try
            {
                Directory.Delete(full, recursive: false);
                return true;
            }
            catch (Exception ex)
            {
                if (markerBytes != null && Directory.Exists(full))
                {
                    try
                    {
                        File.WriteAllBytes(markerPath, markerBytes);
                    }
                    catch (Exception)
                    {
                    }
                }

                error = ex.Message;
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void DeleteChildren(string dir)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            if (Core.PathSafety.IsReparsePoint(file))
                continue;
            var full = System.IO.Path.GetFullPath(file);
            if (!Core.PathSafety.IsStrictSubPath(dir, full))
                continue;
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }

        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (Core.PathSafety.IsReparsePoint(sub))
                continue;
            var full = System.IO.Path.GetFullPath(sub);
            if (!Core.PathSafety.IsStrictSubPath(dir, full))
                continue;
            DeleteChildren(full);
            Directory.Delete(full, recursive: false);
        }
    }
}
