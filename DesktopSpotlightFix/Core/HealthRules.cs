namespace DesktopSpotlightFix.Core;

public static class HealthRules
{
    /// <summary>
    /// --repair 且没有 --force 时，是否跳过 oneshot。
    /// 单一信号不够：只有 success、只有缓存、或只有注入图，都不算官方管道健康。
    /// </summary>
    public static bool ShouldSkipOneShot(SpotlightSnapshot snapshot, bool hasUpdateTimer)
    {
        if (!hasUpdateTimer)
            return false;

        var hasTimestamp =
            !string.IsNullOrWhiteSpace(snapshot.LastAttempt) ||
            !string.IsNullOrWhiteSpace(snapshot.StateSuccessDate) ||
            !string.IsNullOrWhiteSpace(snapshot.LastTaskRun);

        var officialEvidence =
            snapshot.StateSuccess == true &&
            hasTimestamp &&
            snapshot.HasCache88000820 &&
            snapshot.CacheJsonHasImage;

        var realWallpaper =
            !snapshot.IsSystemFallbackWallpaper &&
            !snapshot.IsInjectedWallpaper &&
            !string.IsNullOrWhiteSpace(snapshot.Wallpaper);
        var wallpaperAndCache = realWallpaper && snapshot.HasCache88000820;

        return officialEvidence || wallpaperAndCache;
    }
}

public static class ChangeDetector
{
    public static bool HasObservedChange(SpotlightSnapshot baseline, SpotlightSnapshot current)
    {
        if (!string.Equals(current.Wallpaper, baseline.Wallpaper, StringComparison.OrdinalIgnoreCase) &&
            !current.IsSystemFallbackWallpaper &&
            !string.IsNullOrWhiteSpace(current.Wallpaper))
            return true;

        if (!string.IsNullOrWhiteSpace(current.LastAttempt) &&
            !string.Equals(current.LastAttempt, baseline.LastAttempt, StringComparison.Ordinal))
            return true;

        if (current.StateSuccess == true && baseline.StateSuccess != true)
            return true;

        if (current.IrisFullSizeCount > baseline.IrisFullSizeCount)
            return true;

        if (current.IrisFileCount > baseline.IrisFileCount)
            return true;

        if (current.HasCache88000820 && !baseline.HasCache88000820)
            return true;

        if (current.HasCache88000820 &&
            !string.IsNullOrWhiteSpace(current.CacheRefresh) &&
            !string.Equals(current.CacheRefresh, baseline.CacheRefresh, StringComparison.Ordinal))
            return true;

        return false;
    }
}
