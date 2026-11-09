namespace DesktopSpotlightFix.PackageIdentity;

public sealed record HelperPayload(
    bool SawEnd,
    bool NonceOk,
    string? Exception,
    IReadOnlyList<string> Lines);

public static class HelperHandshake
{
    public static string Wrap(string body, string outPath, string nonce)
    {
        var path = outPath.Replace("'", "''", StringComparison.Ordinal);
        var safeNonce = nonce.Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference = 'Continue'\r\n" +
            $"$out = '{path}'\r\n" +
            $"$nonce = '{safeNonce}'\r\n" +
            "function W($m){\r\n" +
            "  $utf8 = New-Object System.Text.UTF8Encoding $false\r\n" +
            "  [System.IO.File]::AppendAllText($out, ([string]$m) + [Environment]::NewLine, $utf8)\r\n" +
            "}\r\n" +
            "try {\r\n" +
            "  W ('NONCE=' + $nonce)\r\n" +
            "  W ('PID=' + $PID)\r\n" +
            body.Trim() + "\r\n" +
            "} catch {\r\n" +
            "  W ('EXCEPTION=' + $_.Exception.Message)\r\n" +
            "} finally {\r\n" +
            "  W 'END=1'\r\n" +
            "}\r\n";
    }

    public static HelperPayload Parse(string? text, string expectedNonce)
    {
        var lines = Split(text);
        var sawEnd = lines.Any(l => Normalize(l) == "END=1");
        var nonceOk = lines.Any(l => Normalize(l) == "NONCE=" + expectedNonce);
        string? exception = null;
        foreach (var line in lines)
        {
            var n = Normalize(line);
            if (n.StartsWith("EXCEPTION=", StringComparison.Ordinal))
            {
                exception = n["EXCEPTION=".Length..];
                break;
            }
        }

        var payload = new List<string>();
        foreach (var line in lines)
        {
            var n = Normalize(line);
            if (n.Length == 0 || n == "END=1" || n.StartsWith("NONCE=", StringComparison.Ordinal))
                continue;
            payload.Add(n);
        }

        return new HelperPayload(sawEnd, nonceOk, exception, payload);
    }

    public static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        return text.Split(["\r\n", "\n"], StringSplitOptions.None);
    }

    public static string Normalize(string line) =>
        line.Trim().TrimStart('\uFEFF');

    public static bool LooksLikeExecutionPolicyBlock(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return text.Contains("running scripts is disabled", StringComparison.OrdinalIgnoreCase)
            || text.Contains("because running scripts is disabled", StringComparison.OrdinalIgnoreCase)
            || text.Contains("禁止运行脚本", StringComparison.Ordinal)
            || text.Contains("因为在此系统上禁止运行脚本", StringComparison.Ordinal);
    }
}
