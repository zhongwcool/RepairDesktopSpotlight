namespace DesktopSpotlightFix.Core;

public static class SpotlightMutator
{
    public static IReadOnlyList<string> EnableSpotlight(Backup.RegistrySession session)
    {
        var notes = new List<string>();
        session.SetDWord(Backup.RegistryPaths.Settings, "EnabledState", 1);
        using (var wall = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.Wallpapers, writable: false))
        {
            if (wall != null)
                session.SetDWord(Backup.RegistryPaths.Wallpapers, "BackgroundType", 3);
            else
                notes.Add("没有找到 Wallpapers 注册表项，没有改 BackgroundType。请在设置里选一次 Windows 聚焦。");
        }

        if (TryEnsureIrisCache(out var createdMessage) && !string.IsNullOrWhiteSpace(createdMessage))
            notes.Add(createdMessage);
        return notes;
    }

    public static IReadOnlyList<string> OpenGate(Backup.RegistrySession session, SpotlightSnapshot snapshot, DateTime utcNow)
    {
        var notes = new List<string>();
        var stamp = GateCooldown.FormatRewind(utcNow);
        var rotation = GateCooldown.TryParseUtc(snapshot.Rotation);
        var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        var rotationBlocks = rotation.HasValue && (now - rotation.Value).TotalMinutes < 60;
        session.SetString(Backup.RegistryPaths.Desktop, "WallpaperRefresh", stamp);
        notes.Add("已把 WallpaperRefresh 回拨到 " + stamp + "（至少 2 小时前），用来打开约 60 分钟冷却。");
        if (rotationBlocks)
        {
            session.SetString(Backup.RegistryPaths.Desktop, "Rotation", stamp);
            notes.Add("Rotation 仍在 60 分钟内，只改 WallpaperRefresh 门限还会关着，因此一并回拨了 Rotation。");
        }

        return notes;
    }

    public static bool TryEnsureIrisCache(out string message)
    {
        message = "";
        var target = Path.GetFullPath(AppPaths.IrisCacheDirectory);
        var packageRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            PackageIdentity.TaskCatalog.PackageFamilyName));
        if (!PathSafety.IsStrictSubPath(packageRoot, target))
        {
            message = "缓存目录解析后不在 CBS 包目录内，已拒绝创建。";
            return false;
        }

        var cursor = target;
        var localRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        while (!string.IsNullOrEmpty(cursor) &&
               cursor.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(cursor) && PathSafety.IsReparsePoint(cursor))
            {
                message = "缓存路径上有重解析点，已拒绝使用：" + cursor;
                return false;
            }

            var parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrEmpty(parent) || parent.Equals(cursor, StringComparison.OrdinalIgnoreCase))
                break;
            cursor = parent;
        }

        if (Directory.Exists(target))
            return true;
        Directory.CreateDirectory(target);
        message = "已创建缓存目录：" + target;
        return true;
    }
}
