using System.Globalization;

namespace DesktopSpotlightFix.Core;

public static class GateCooldown
{
    public static DateTime? TryParseUtc(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
            return null;

        if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ||
            DateTime.TryParse(iso, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt))
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Local);
            return dt.ToUniversalTime();
        }

        return null;
    }

    /// <summary>
    /// 最近一次 WallpaperRefresh / Rotation 若在 60 分钟内，返回冷却结束的 UTC 时间；否则返回 null（门限开着）。
    /// </summary>
    public static DateTime? GetOpensUtc(string? wallpaperRefresh, string? rotation, DateTime utcNow)
    {
        var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        DateTime? latest = null;
        foreach (var iso in new[] { wallpaperRefresh, rotation })
        {
            var t = TryParseUtc(iso);
            if (t.HasValue && (latest == null || t.Value > latest.Value))
                latest = t;
        }

        if (latest.HasValue && (now - latest.Value).TotalMinutes < 60)
            return latest.Value.AddMinutes(60);
        return null;
    }

    public static string FormatWhen(string? iso, DateTime utcNow)
    {
        var t = TryParseUtc(iso);
        if (t == null)
            return "(无)";

        var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        var local = t.Value.ToLocalTime();
        var delta = now - t.Value;
        var seconds = delta.TotalSeconds;
        string ago;
        if (seconds < 0)
        {
            var s = Math.Abs(seconds);
            ago = s < 5400
                ? $"（{(s / 60).ToString("n0", CultureInfo.CurrentCulture)} 分钟后）"
                : s < 129600
                    ? $"（{(s / 3600).ToString("n0", CultureInfo.CurrentCulture)} 小时后）"
                    : $"（{(s / 86400).ToString("n0", CultureInfo.CurrentCulture)} 天后）";
        }
        else
        {
            ago = seconds < 5400
                ? $"（{(seconds / 60).ToString("n0", CultureInfo.CurrentCulture)} 分钟前）"
                : seconds < 129600
                    ? $"（{(seconds / 3600).ToString("n0", CultureInfo.CurrentCulture)} 小时前）"
                    : $"（{(seconds / 86400).ToString("n0", CultureInfo.CurrentCulture)} 天前）";
        }

        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) + " " + ago;
    }

    public static string FormatRewind(DateTime utcNow)
    {
        var utc = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        return utc.AddHours(-2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
