namespace DesktopSpotlightFix.Core;

public static class PathSafety
{
    public static bool IsReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsStrictSubPath(string parent, string child)
    {
        var p = Normalize(parent) + Path.DirectorySeparatorChar;
        var c = Normalize(child);
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryCombineLeaf(string directory, string untrustedLeaf, out string fullPath)
    {
        fullPath = "";
        var leaf = Path.GetFileName(untrustedLeaf);
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
            return false;
        if (leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        var root = Normalize(directory);
        var combined = Normalize(Path.Combine(root, leaf));
        if (!IsStrictSubPath(root, combined))
            return false;
        fullPath = combined;
        return true;
    }
}

public static class ImageDestination
{
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".jfif", ".bmp"];

    public static bool TryCreate(string directory, string assetUrl, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "图片地址不是 http 或 https。";
            return false;
        }

        var leaf = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(leaf) || leaf.Contains("..", StringComparison.Ordinal))
        {
            error = "图片文件名无效。";
            return false;
        }

        if (string.IsNullOrEmpty(Path.GetExtension(leaf)))
            leaf += ".jpg";

        var ext = Path.GetExtension(leaf);
        if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            error = "图片扩展名不在允许范围。";
            return false;
        }

        if (!PathSafety.TryCombineLeaf(directory, leaf, out fullPath))
        {
            error = "图片路径落在目标目录之外，已拒绝。";
            return false;
        }

        return true;
    }
}
