using System.Text.Json.Nodes;

namespace DesktopSpotlightFix.Core;

public sealed class FallbackInjector
{
    private readonly IrisProbe _iris;
    private readonly SnapshotReader _snapshots;

    public FallbackInjector(IrisProbe iris, SnapshotReader snapshots)
    {
        _iris = iris;
        _snapshots = snapshots;
    }

    public async Task<IReadOnlyList<string>> InjectAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>
        {
            "开始注入备用图。这只把图片写入「下一个背景」清单，不能代替官方每日拉图。背景类型会保持为 Windows 聚焦。本工具不会调用设置壁纸接口。"
        };

        var country = IrisUriBuilder.CurrentCountry();
        var locale = IrisUriBuilder.CurrentLocale();
        var version = IrisUriBuilder.LocalOsVersion();
        var uri = IrisUriBuilder.BuildFresh(country, locale, version);
        string body;
        try
        {
            body = await _iris.DownloadTextAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("下载目录失败：" + ex.Message, ex);
        }

        var items = CreativeMerger.ParseBatchItems(body);
        if (items.Count == 0)
            throw new InvalidOperationException("接口返回里没有可用的 items。");

        var folder = AppPaths.FallbackPicturesDirectory;
        EnsurePicturesFolder(folder);

        var downloaded = new List<JsonObject>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = CreativeMerger.LandscapeAsset(item);
            if (string.IsNullOrWhiteSpace(asset))
                continue;
            if (!ImageDestination.TryCreate(folder, asset, out var local, out var error))
            {
                notes.Add("跳过一张图：" + error);
                continue;
            }

            if (!File.Exists(local))
            {
                notes.Add("下载 " + Path.GetFileName(local));
                await _iris.DownloadFileAsync(asset, local, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                notes.Add("已存在 " + Path.GetFileName(local));
            }

            if (!File.Exists(local))
                continue;
            if (!CreativeMerger.TrySetLocalAssets(item, local))
                continue;
            downloaded.Add(item);
        }

        if (downloaded.Count == 0)
            throw new InvalidOperationException("没有成功准备任何图片。");

        var oldKept = ReadKeptCreatives();
        var combined = CreativeMerger.Merge(downloaded, oldKept);
        var json = CreativeMerger.Serialize(combined);
        var first = CreativeMerger.LandscapeAsset(downloaded[0]) ?? "";

        using (var session = Backup.RegistrySession.Begin("inject-fallback"))
        {
            foreach (var note in SpotlightMutator.EnableSpotlight(session))
                notes.Add(note);
            session.SetString(Backup.RegistryPaths.Desktop, "DefaultCreatives", json);
            session.SetDWord(Backup.RegistryPaths.Desktop, "ImagesUsed", 0);
            session.SetDWord(Backup.RegistryPaths.Creatives, "ImageIndex", 0);
            session.SetString(Backup.RegistryPaths.ControlPanelDesktop, "Wallpaper", first);
            session.SetDWord(Backup.RegistryPaths.Wallpapers, "BackgroundType", 3);
            session.SetString(Backup.RegistryPaths.Wallpapers, "CurrentWallpaperPath", first);
            var rawCopy = Path.Combine(session.DirectoryPath, "DefaultCreatives.written.json");
            await File.WriteAllTextAsync(rawCopy, json, cancellationToken).ConfigureAwait(false);
            notes.Add("注册表备份目录：" + session.DirectoryPath);
            session.Commit();
        }

        notes.Add("已写入清单，共 " + combined.Count + " 张。");
        notes.Add("请保持「设置 → 个性化 → 背景」为 Windows 聚焦，然后在桌面右键点几次「下一个背景」。");
        notes.Add("不要用设置壁纸的系统接口去指定图片，那会把模式切成「图片」。");
        _ = _snapshots.Read();
        return notes;
    }

    private static void EnsurePicturesFolder(string folder)
    {
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var full = Path.GetFullPath(folder);
        if (!PathSafety.IsStrictSubPath(profile, full))
            throw new InvalidOperationException("备用图目录不在用户配置下，已拒绝创建。");
        if (Directory.Exists(full) && PathSafety.IsReparsePoint(full))
            throw new InvalidOperationException("备用图目录是重解析点，已拒绝使用。");
        Directory.CreateDirectory(full);
    }

    private static List<JsonObject> ReadKeptCreatives()
    {
        var kept = new List<JsonObject>();
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Backup.RegistryPaths.Desktop, writable: false);
        var raw = key?.GetValue("DefaultCreatives") as string;
        if (string.IsNullOrWhiteSpace(raw))
            return kept;
        try
        {
            var node = JsonNode.Parse(raw) as JsonArray;
            if (node == null)
                return kept;
            foreach (var item in node)
            {
                if (item is not JsonObject obj)
                    continue;
                var clone = JsonNode.Parse(obj.ToJsonString()) as JsonObject;
                if (clone == null)
                    continue;
                if (CreativeMerger.ShouldKeepOld(CreativeMerger.LandscapeAsset(clone)))
                    kept.Add(clone);
            }
        }
        catch (Exception)
        {
            return [];
        }

        return kept;
    }
}
