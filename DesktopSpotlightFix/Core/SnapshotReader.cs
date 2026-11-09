using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DesktopSpotlightFix.Core;

public sealed class SnapshotReader
{
    private static readonly Regex FallbackWallpaper = new(
        @"\\SystemApps\\.*\\DesktopSpotlight\\Assets\\Images\\image_\d+\.jpg$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public SpotlightSnapshot Read()
    {
        string wallpaper = "";
        string updateTimer = "";
        string wallpaperRefresh = "";
        string rotation = "";
        int? imagesUsed = null;
        string? stateRaw = null;

        using (var desktop = Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.Desktop, writable: false))
        {
            if (desktop != null)
            {
                stateRaw = desktop.GetValue("State") as string;
                updateTimer = Backup.RegistryRead.GetString(desktop, "UpdateTimer") ?? "";
                wallpaperRefresh = Backup.RegistryRead.GetString(desktop, "WallpaperRefresh") ?? "";
                rotation = Backup.RegistryRead.GetString(desktop, "Rotation") ?? "";
                imagesUsed = Backup.RegistryRead.GetInt(desktop, "ImagesUsed");
            }
        }

        using (var panel = Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.ControlPanelDesktop, writable: false))
        {
            if (panel != null)
                wallpaper = Backup.RegistryRead.GetString(panel, "Wallpaper") ?? "";
        }

        int? backgroundType = null;
        using (var wall = Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.Wallpapers, writable: false))
            backgroundType = Backup.RegistryRead.GetInt(wall, "BackgroundType");

        int? enabled = null;
        int? disabledReason = null;
        using (var settings = Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.Settings, writable: false))
        {
            enabled = Backup.RegistryRead.GetInt(settings, "EnabledState");
            disabledReason = Backup.RegistryRead.GetInt(settings, "SpotlightDisabledReason");
        }

        int? imageIndex = null;
        using (var creatives = Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.Creatives, writable: false))
            imageIndex = Backup.RegistryRead.GetInt(creatives, "ImageIndex");

        var state = ParseState(stateRaw);
        var irisFolder = AppPaths.IrisCacheDirectory;
        var (files, full, truncated, exists) = CountIrisFiles(irisFolder);
        var cache = ReadCache();

        return new SpotlightSnapshot
        {
            BackgroundType = backgroundType,
            Wallpaper = wallpaper,
            IsSystemFallbackWallpaper = !string.IsNullOrEmpty(wallpaper) && FallbackWallpaper.IsMatch(wallpaper),
            IsInjectedWallpaper = wallpaper.Contains("SpotlightDesktop", StringComparison.OrdinalIgnoreCase),
            EnabledState = enabled,
            DisabledReason = disabledReason,
            UpdateTimer = updateTimer,
            WallpaperRefresh = wallpaperRefresh,
            Rotation = rotation,
            ImagesUsed = imagesUsed,
            ImageIndex = imageIndex,
            StateSuccess = state.Success,
            StateSuccessDate = state.SuccessDate,
            LastAttempt = state.LastAttempt,
            StatusCode = state.StatusCode,
            LastTaskRun = state.LastTaskRun,
            StateJsonUnreadable = state.Unreadable,
            IrisFolderExists = exists,
            IrisFolder = irisFolder,
            IrisFileCount = files,
            IrisFullSizeCount = full,
            IrisCountTruncated = truncated,
            HasCache88000820 = cache.Found,
            CacheStatusCode = cache.Status,
            CacheJsonHasImage = cache.HasImage,
            CacheRequestUri = cache.Uri,
            CacheRefresh = cache.Refresh,
            CacheExpiry = cache.Expiry,
            PolicyHits = ReadPolicyHits()
        };
    }

    private static (bool? Success, string SuccessDate, string LastAttempt, string StatusCode, string LastTaskRun, bool Unreadable) ParseState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "", "", "", "", false);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "", "", "", "", true);
            var root = doc.RootElement;
            bool? success = null;
            if (root.TryGetProperty("RetrieveIrisContentSuccess", out var successEl))
            {
                if (successEl.ValueKind == JsonValueKind.True) success = true;
                else if (successEl.ValueKind == JsonValueKind.False) success = false;
            }

            return (
                success,
                ReadJsonString(root, "RetrieveIrisContentSuccessDate"),
                ReadJsonString(root, "RetrieveIrisContentLastAttemptDate"),
                ReadJsonString(root, "RetrieveIrisContentStatusCode"),
                ReadJsonString(root, "LastBackgroundTaskRunDate"),
                false);
        }
        catch (JsonException)
        {
            return (null, "", "", "", "", true);
        }
    }

    private static string ReadJsonString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => ""
        };
    }

    private static (int Files, int Full, bool Truncated, bool Exists) CountIrisFiles(string root)
    {
        if (!Directory.Exists(root))
            return (0, 0, false, false);
        if (PathSafety.IsReparsePoint(root))
            return (0, 0, false, true);

        var files = 0;
        var full = 0;
        var truncated = false;
        var rootFull = PathSafety.Normalize(root);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (PathSafety.IsReparsePoint(dir))
                continue;
            var dirFull = PathSafety.Normalize(dir);
            if (!dirFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                !PathSafety.IsStrictSubPath(rootFull, dirFull))
                continue;

            IEnumerable<string> children;
            IEnumerable<string> subs;
            try
            {
                children = Directory.EnumerateFiles(dir);
                subs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in children)
            {
                if (PathSafety.IsReparsePoint(file))
                    continue;
                files++;
                try
                {
                    if (new FileInfo(file).Length > 100 * 1024)
                        full++;
                }
                catch (IOException)
                {
                }

                if (files >= 20000)
                {
                    truncated = true;
                    return (files, full, truncated, true);
                }
            }

            foreach (var sub in subs)
                stack.Push(sub);
        }

        return (files, full, truncated, true);
    }

    private readonly record struct CacheHit(bool Found, string Status, bool HasImage, string Uri, string Refresh, string Expiry);

    private static CacheHit ReadCache()
    {
        using var cache = Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.IrisCache, writable: false);
        if (cache == null)
            return default;

        CacheHit hit = default;
        foreach (var name in cache.GetSubKeyNames())
        {
            using var sub = cache.OpenSubKey(name, writable: false);
            if (sub == null)
                continue;
            var uri = Backup.RegistryRead.GetString(sub, "RequestUri") ?? "";
            if (!uri.Contains("placement=" + PackageIdentity.TaskCatalog.Placement, StringComparison.OrdinalIgnoreCase))
                continue;
            var raw = sub.GetValue("RawJson") as string ?? "";
            var status = sub.GetValue("StatusCode");
            hit = new CacheHit(
                true,
                status == null ? "" : Convert.ToString(status, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                IrisUriBuilder.ContainsImageFields(raw),
                uri,
                Backup.RegistryRead.GetString(sub, "RefreshTime") ?? "",
                Backup.RegistryRead.GetString(sub, "LastExpiration") ?? "");
        }

        return hit;
    }

    private static IReadOnlyList<string> ReadPolicyHits()
    {
        var hits = new List<string>();
        CollectPolicy(Registry.CurrentUser, "HKCU", Backup.RegistryPaths.CloudContent, hits);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            CollectPolicy(hklm, "HKLM", Backup.RegistryPaths.CloudContent, hits);
        }
        catch (Exception)
        {
            // 读不到 HKLM 策略时不把它当成禁用。
        }

        return hits;
    }

    private static void CollectPolicy(RegistryKey hive, string hiveName, string subKey, List<string> hits)
    {
        using var key = hive.OpenSubKey(subKey, writable: false);
        if (key == null)
            return;
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name);
            if (Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) == "1")
                hits.Add(hiveName + "\\" + subKey + "\\" + name + "=1");
        }
    }
}

public static class OsInfoReader
{
    public static OsInfo Read()
    {
        var product = "";
        var display = "";
        var build = "";
        var ubr = "";
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cv = hklm.OpenSubKey(Backup.RegistryPaths.WindowsVersion, writable: false);
            if (cv != null)
            {
                product = cv.GetValue("ProductName") as string ?? "";
                display = cv.GetValue("DisplayVersion") as string ?? "";
                build = cv.GetValue("CurrentBuild") as string ?? "";
                var ubrRaw = cv.GetValue("UBR");
                ubr = ubrRaw == null
                    ? ""
                    : Convert.ToInt64(ubrRaw, System.Globalization.CultureInfo.InvariantCulture)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
        }

        var cbsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SystemApps",
            PackageIdentity.TaskCatalog.PackageFamilyName);
        var exists = Directory.Exists(cbsDir);
        var version = exists ? ReadCbsVersion(cbsDir) : "";
        if (string.IsNullOrWhiteSpace(version))
            version = exists ? "(未能读取版本)" : "";
        return new OsInfo(product, display, build, ubr, version, exists);
    }

    private static string ReadCbsVersion(string cbsDir)
    {
        var manifest = Path.Combine(cbsDir, "AppxManifest.xml");
        if (!File.Exists(manifest))
            return "";
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(manifest);
            var identity = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
            return identity?.Attribute("Version")?.Value ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
