using DesktopSpotlightFix.Core;

namespace DesktopSpotlightFix.Tests;

public class OldImageCleanerTests
{
    [Fact]
    public void Selects_images_and_leaves_the_current_wallpaper()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsfix-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var old = Path.Combine(root, "old.jpg");
        var current = Path.Combine(root, "nested", "now.png");
        var note = Path.Combine(root, "readme.txt");
        var fallback = Path.Combine(root, "SystemApps", "SomePackage", "DesktopSpotlight", "Assets", "Images");
        Directory.CreateDirectory(fallback);
        var system = Path.Combine(fallback, "image_1.jpg");
        var recent = Path.Combine(root, "recent.jpg");
        var now = new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);
        File.WriteAllBytes(old, [1, 2, 3]);
        File.WriteAllBytes(current, [4, 5, 6]);
        File.WriteAllBytes(recent, [4, 5]);
        File.WriteAllText(note, "no");
        File.WriteAllBytes(system, [7, 8, 9]);
        File.SetLastWriteTimeUtc(old, now.AddDays(-40));
        File.SetLastWriteTimeUtc(current, now.AddDays(-40));
        File.SetLastWriteTimeUtc(recent, now.AddDays(-2));
        File.SetLastWriteTimeUtc(system, now.AddDays(-400));

        try
        {
            var selected = OldImageCleaner.ListDeletable(root, [current], now);
            Assert.Contains(Path.GetFullPath(old), selected, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(current), selected, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(recent), selected, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(note), selected, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(system), selected, StringComparer.OrdinalIgnoreCase);

            var notes = new List<string>();
            var deleted = OldImageCleaner.Delete(selected, notes);
            Assert.Equal(1, deleted);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(system));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Creative_list_keeps_local_pictures_and_ignores_urls()
    {
        var json = """
            [
              {"ad":{"landscapeImage":{"asset":"C:\\Pictures\\SpotlightDesktop\\keep.jpg"},"portraitImage":{"asset":"https://example/a.jpg"}}},
              {"ad":{"landscapeImage":{"asset":"relative.jpg"}}}
            ]
            """;
        Assert.True(OldImageCleaner.TryReadCreativePaths(json, out var paths));
        Assert.Equal([Path.GetFullPath(@"C:\Pictures\SpotlightDesktop\keep.jpg")], paths);
        Assert.False(OldImageCleaner.TryReadCreativePaths("{", out _));
        Assert.True(OldImageCleaner.TryReadCreativePaths("  ", out var empty));
        Assert.Empty(empty);
    }

    [Fact]
    public void Refuses_a_root_outside_the_allowed_parent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "dsfix-parent-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "dsfix-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        try
        {
            Assert.False(OldImageCleaner.TryAcceptRoot(outside, parent, out var error));
            Assert.Contains("拒绝", error, StringComparison.Ordinal);
            Assert.True(OldImageCleaner.TryAcceptRoot(Path.Combine(parent, "IrisService"), parent, out _));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
