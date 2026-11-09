using DesktopSpotlightFix.Core;
using DesktopSpotlightFix.PackageIdentity;

namespace DesktopSpotlightFix.Tests;

public class UserGuidanceTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Healthy_pipeline_says_no_repair()
    {
        var guidance = UserGuidance.Decide(Make(Official(), ReadyTasks()), Now);

        Assert.Equal("不需要修复", guidance.Title);
        Assert.Equal(GuidanceAction.None, guidance.Action);
        Assert.Equal(GuidanceTone.Healthy, guidance.Tone);
        Assert.Contains("不用修复", guidance.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("注意", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Healthy_with_a_warning_row_says_it_can_be_ignored()
    {
        var snapshot = Official() with
        {
            IsSystemFallbackWallpaper = true,
            Wallpaper = @"C:\Windows\SystemApps\x\image_0.jpg"
        };

        var guidance = UserGuidance.Decide(Make(snapshot, ReadyTasks()), Now);

        Assert.Equal(GuidanceAction.None, guidance.Action);
        Assert.Contains("不用处理", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_timer_is_one_click_repair()
    {
        var tasks = ReadyTasks() with
        {
            Tasks = TaskCatalog.CoreTasks.Where(t => t != TaskCatalog.UpdateTimer).ToArray()
        };

        var guidance = UserGuidance.Decide(Make(Official(), tasks), Now);

        Assert.Equal("需要修复", guidance.Title);
        Assert.Equal(GuidanceAction.Repair, guidance.Action);
        Assert.Contains("一键修复", guidance.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("强制刷新", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Weak_evidence_asks_for_refresh()
    {
        var guidance = UserGuidance.Decide(Make(new SpotlightSnapshot { BackgroundType = 3 }, ReadyTasks()), Now);

        Assert.Equal("建议刷新", guidance.Title);
        Assert.Equal(GuidanceAction.Refresh, guidance.Action);
        Assert.Contains("强制刷新", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_cooldown_tells_the_user_where_to_skip_it()
    {
        var snapshot = new SpotlightSnapshot
        {
            BackgroundType = 3,
            WallpaperRefresh = Now.AddMinutes(-10).ToString("o")
        };

        var guidance = UserGuidance.Decide(Make(snapshot, ReadyTasks()), Now);

        Assert.Equal(GuidanceAction.Refresh, guidance.Action);
        Assert.Contains("还在冷却", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("强制刷新", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_background_with_healthy_evidence_repairs_without_promising_a_download()
    {
        var guidance = UserGuidance.Decide(Make(Official() with { BackgroundType = 0 }, ReadyTasks()), Now);

        Assert.Equal(GuidanceAction.Repair, guidance.Action);
        Assert.Contains("Windows 聚焦", guidance.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("15–30", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_is_not_offered_as_a_repair()
    {
        var snapshot = Official() with { PolicyHits = ["DisableWindowsSpotlightFeatures=1"] };

        var guidance = UserGuidance.Decide(Make(snapshot, ReadyTasks()), Now);

        Assert.Equal("这里修不了", guidance.Title);
        Assert.Equal(GuidanceAction.None, guidance.Action);
        Assert.Contains("组策略", guidance.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("一键修复", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreachable_gallery_does_not_ask_for_repair()
    {
        var guidance = UserGuidance.Decide(
            Make(Official(), ReadyTasks(), new IrisProbeResult(false, false, "超时")),
            Now);

        Assert.Equal("先不用修复", guidance.Title);
        Assert.Equal(GuidanceAction.None, guidance.Action);
        Assert.Equal(GuidanceTone.External, guidance.Tone);
        Assert.Contains("图库", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_gallery_points_at_fallback_images()
    {
        var guidance = UserGuidance.Decide(
            Make(Official(), ReadyTasks(), new IrisProbeResult(true, false, "空")),
            Now);

        Assert.Equal(GuidanceAction.None, guidance.Action);
        Assert.Contains("放入备用图", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduled_refresh_hides_the_button_until_it_is_time()
    {
        var report = Make(new SpotlightSnapshot { BackgroundType = 3 }, ReadyTasks());
        var result = new ToolResult
        {
            Outcome = RunOutcome.Completed,
            Summary = "刷新已安排。大约 15–30 分钟后才可能看到新图，不是马上换好。",
            Report = report
        };

        var guidance = UserGuidance.ForResult(result, Now);

        Assert.Equal("已经安排好", guidance.Title);
        Assert.Equal(GuidanceAction.None, guidance.Action);
        Assert.Contains("不用再点", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cooldown_block_keeps_the_refresh_button()
    {
        var report = Make(new SpotlightSnapshot
        {
            BackgroundType = 3,
            WallpaperRefresh = Now.AddMinutes(-10).ToString("o")
        }, ReadyTasks());
        var result = new ToolResult
        {
            Outcome = RunOutcome.Completed,
            Summary = "任务检查过了。现在还在冷却，所以没有安排刷新。",
            Report = report
        };

        var guidance = UserGuidance.ForResult(result, Now);

        Assert.Equal(GuidanceAction.Refresh, guidance.Action);
        Assert.Contains("没有安排刷新", guidance.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_without_a_new_report_does_not_claim_the_pipeline_is_healthy()
    {
        var guidance = UserGuidance.ForResult(new ToolResult
        {
            Outcome = RunOutcome.Completed,
            Summary = "已按备份回滚本工具写下的项。",
            Report = null
        }, Now);

        Assert.Equal("已完成", guidance.Title);
        Assert.Equal(GuidanceTone.Neutral, guidance.Tone);
        Assert.NotEqual("不需要修复", guidance.Title);
    }

    private static SpotlightSnapshot Official() => new()
    {
        BackgroundType = 3,
        StateSuccess = true,
        LastAttempt = "2026-11-09T00:00:00Z",
        HasCache88000820 = true,
        CacheJsonHasImage = true,
        Wallpaper = @"C:\Users\a\AppData\Local\Packages\Iris\a.jpg",
        IrisFolderExists = true,
        IrisFullSizeCount = 2,
        IrisFileCount = 2
    };

    private static TaskReport ReadyTasks() => new()
    {
        EnteredDesktopPackage = true,
        Aumid = "MicrosoftWindows.Client.CBS_cw5n1h2txyewy!Global.DesktopSpotlight",
        Access = "Allowed",
        Tasks = TaskCatalog.CoreTasks
    };

    private static DiagnosisReport Make(SpotlightSnapshot snapshot, TaskReport tasks, IrisProbeResult? iris = null) =>
        DiagnosisComposer.Create(
            new OsInfo("Windows 11", "25H2", "26200", "1", "1.0.0.0", true),
            snapshot,
            tasks,
            iris ?? new IrisProbeResult(true, true, "可访问，且正在返回桌面壁纸内容。"),
            Now,
            "TEST");
}
