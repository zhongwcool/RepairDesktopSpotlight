using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopSpotlightFix.Core;

public static class IrisUriBuilder
{
    private static readonly Regex AsidPattern = new(@"asid=[0-9A-Fa-f]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ContainsImageFields(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return text.Contains("landscapeImage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("portraitImage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("onecdn", StringComparison.OrdinalIgnoreCase);
    }

    public static string WithNewAsid(string cachedUri)
    {
        var asid = Guid.NewGuid().ToString("N").ToUpperInvariant();
        if (AsidPattern.IsMatch(cachedUri))
            return AsidPattern.Replace(cachedUri, "asid=" + asid, 1);
        var sep = cachedUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return cachedUri + sep + "asid=" + asid;
    }

    public static string BuildFresh(string? country, string? locale, string? osVersion)
    {
        var c = string.IsNullOrWhiteSpace(country) ? "US" : country.Trim();
        var loc = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
        var ver = string.IsNullOrWhiteSpace(osVersion) ? "10.0.0.0" : osVersion.Trim();
        var asid = Guid.NewGuid().ToString("N").ToUpperInvariant();
        return "https://fd.api.iris.microsoft.com/v4/api/selection"
            + "?asid=" + asid
            + "&placement=" + PackageIdentity.TaskCatalog.Placement
            + "&bcnt=4"
            + "&country=" + Uri.EscapeDataString(c)
            + "&locale=" + Uri.EscapeDataString(loc)
            + "&lc=" + Uri.EscapeDataString(loc)
            + "&pl=" + Uri.EscapeDataString(loc)
            + "&fmt=json&clr=cdmlite&devfam=Windows.Desktop"
            + "&devosver=" + Uri.EscapeDataString(ver);
    }

    public static string LocalOsVersion()
    {
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var cv = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = cv?.GetValue("CurrentBuild") as string ?? "";
            var ubr = cv?.GetValue("UBR");
            var ubrText = ubr == null ? "0" : Convert.ToInt64(ubr, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(build))
                return "10.0." + build + "." + ubrText;
        }
        catch
        {
            // 读不到版本时退回环境变量，探测 URL 仍可发出。
        }

        var v = Environment.OSVersion.Version;
        return v.Major + "." + v.Minor + "." + v.Build + ".0";
    }

    public static string CurrentCountry()
    {
        try
        {
            var name = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return string.IsNullOrWhiteSpace(name) ? "US" : name;
        }
        catch
        {
            return "US";
        }
    }

    public static string CurrentLocale()
    {
        var name = CultureInfo.CurrentCulture.Name;
        return string.IsNullOrWhiteSpace(name) ? "en-US" : name;
    }
}
