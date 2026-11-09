using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DesktopSpotlightFix.Core;

public static class OldImageCleaner
{
    public const int MaxAgeDays = 30;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".jfif", ".bmp"];

    private static readonly Regex SystemFallback = new(
        @"\\SystemApps\\.*\\DesktopSpotlight\\Assets\\Images\\image_\d+\.jpg$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryAcceptRoot(string root, string requiredParent, out string error)
    {
        error = "";
        string target;
        string parent;
        try
        {
            target = Path.GetFullPath(root);
            parent = Path.GetFullPath(requiredParent);
        }
        catch (Exception)
        {
            error = "目录无效，已拒绝删除。";
            return false;
        }

        if (!PathSafety.IsStrictSubPath(parent, target))
        {
            error = "目录不在允许的位置，已拒绝删除。";
            return false;
        }

        if (Directory.Exists(target) && PathSafety.IsReparsePoint(target))
        {
            error = "目录是重解析点，已拒绝删除。";
            return false;
        }

        var cursor = target;
        while (!string.IsNullOrEmpty(cursor) &&
               cursor.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(cursor) && PathSafety.IsReparsePoint(cursor))
            {
                error = "路径上有重解析点，已拒绝删除。";
                return false;
            }

            var next = Path.GetDirectoryName(cursor);
            if (string.IsNullOrEmpty(next) || next.Equals(cursor, StringComparison.OrdinalIgnoreCase))
                break;
            cursor = next;
        }

        return true;
    }

    public static bool TryReadCreativePaths(string? json, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (string.IsNullOrWhiteSpace(json))
            return true;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (Exception)
        {
            return false;
        }

        if (root is not JsonArray items)
            return false;

        var list = new List<string>();
        foreach (var item in items)
        {
            if (item is not JsonObject obj)
                continue;
            AddLocalAsset(obj["ad"]?["landscapeImage"]?["asset"], list);
            AddLocalAsset(obj["ad"]?["portraitImage"]?["asset"], list);
        }

        paths = list;
        return true;
    }

    public static IReadOnlyList<string> ListDeletable(string root, IEnumerable<string>? keepPaths, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || PathSafety.IsReparsePoint(root))
            return [];

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keepPaths != null)
        {
            foreach (var path in keepPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                try
                {
                    keep.Add(PathSafety.Normalize(path));
                }
                catch (Exception)
                {
                }
            }
        }

        var rootFull = PathSafety.Normalize(root);
        var cutoff = (utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime()).AddDays(-MaxAgeDays);
        var list = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        var seen = 0;
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (PathSafety.IsReparsePoint(dir))
                continue;
            string dirFull;
            try
            {
                dirFull = PathSafety.Normalize(dir);
            }
            catch (Exception)
            {
                continue;
            }

            if (!dirFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                !PathSafety.IsStrictSubPath(rootFull, dirFull))
                continue;

            IEnumerable<string> files;
            IEnumerable<string> subs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                subs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                seen++;
                if (seen > 20000)
                    return list;
                if (PathSafety.IsReparsePoint(file) || !IsImage(file) || SystemFallback.IsMatch(file))
                    continue;
                string full;
                try
                {
                    full = PathSafety.Normalize(file);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!PathSafety.IsStrictSubPath(rootFull, full) || keep.Contains(full))
                    continue;
                DateTime written;
                try
                {
                    written = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception)
                {
                    continue;
                }

                if (written >= cutoff)
                    continue;
                list.Add(full);
            }

            foreach (var sub in subs)
                stack.Push(sub);
        }

        return list;
    }

    public static int Delete(IEnumerable<string> files, ICollection<string> notes)
    {
        var deleted = 0;
        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file) || PathSafety.IsReparsePoint(file))
                    continue;
                File.Delete(file);
                deleted++;
                notes.Add("已删除 " + Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                notes.Add("没能删除 " + Path.GetFileName(file) + "：" + ex.Message);
            }
        }

        return deleted;
    }

    private static void AddLocalAsset(JsonNode? node, List<string> paths)
    {
        if (node is not JsonValue)
            return;
        string? text;
        try
        {
            text = node.GetValue<string>();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text) || !Path.IsPathRooted(text))
            return;
        try
        {
            paths.Add(PathSafety.Normalize(text));
        }
        catch (Exception)
        {
        }
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}
