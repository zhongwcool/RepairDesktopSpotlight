using System.Text.Json.Nodes;

namespace DesktopSpotlightFix.Core;

public static class CreativeMerger
{
    public static string? LandscapeAsset(JsonObject obj)
    {
        try
        {
            return obj["ad"]?["landscapeImage"]?["asset"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TrySetLocalAssets(JsonObject obj, string localPath)
    {
        if (obj["ad"] is not JsonObject ad)
            return false;
        if (ad["landscapeImage"] is not JsonObject landscape)
            return false;
        landscape["asset"] = localPath;
        if (ad["portraitImage"] is JsonObject portrait)
            portrait["asset"] = localPath;
        return true;
    }

    public static bool ShouldKeepOld(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
            return false;
        return asset.Contains("SpotlightDesktop", StringComparison.OrdinalIgnoreCase)
            && !asset.Contains("SystemApps", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<JsonObject> Merge(IEnumerable<JsonObject> fresh, IEnumerable<JsonObject> keptOld)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<JsonObject>();
        foreach (var obj in fresh.Concat(keptOld))
        {
            var path = LandscapeAsset(obj);
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (!seen.Add(path))
                continue;
            list.Add(obj);
        }

        return list;
    }

    public static string Serialize(IReadOnlyList<JsonObject> items)
    {
        var parts = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
            parts[i] = items[i].ToJsonString();
        return "[" + string.Join(",", parts) + "]";
    }

    public static IReadOnlyList<JsonObject> ParseBatchItems(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (Exception)
        {
            return [];
        }

        if (root?["batchrsp"]?["items"] is not JsonArray items)
            return [];

        var list = new List<JsonObject>();
        foreach (var it in items)
        {
            JsonObject? obj = null;
            try
            {
                var itemNode = it?["item"];
                if (itemNode is JsonValue)
                {
                    var text = itemNode.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                        obj = JsonNode.Parse(text) as JsonObject;
                }
                else if (itemNode is JsonObject jo)
                {
                    obj = JsonNode.Parse(jo.ToJsonString()) as JsonObject;
                }
            }
            catch (Exception)
            {
                obj = null;
            }

            if (obj != null)
                list.Add(obj);
        }

        return list;
    }
}
