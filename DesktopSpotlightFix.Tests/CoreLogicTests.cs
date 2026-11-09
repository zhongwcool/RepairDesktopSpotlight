using System.Text.Json.Nodes;
using DesktopSpotlightFix.Backup;
using DesktopSpotlightFix.Cli;
using DesktopSpotlightFix.Core;
using DesktopSpotlightFix.Logging;
using DesktopSpotlightFix.PackageIdentity;

namespace DesktopSpotlightFix.Tests;

public class GateCooldownTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Closed_when_refresh_is_inside_60_minutes()
    {
        var refresh = Now.AddMinutes(-10).ToString("o");
        var opens = GateCooldown.GetOpensUtc(refresh, null, Now);
        Assert.Equal(Now.AddMinutes(50), opens);
    }

    [Fact]
    public void Open_when_both_stamps_are_older_than_60_minutes()
    {
        var old = Now.AddMinutes(-90).ToString("o");
        Assert.Null(GateCooldown.GetOpensUtc(old, old, Now));
    }

    [Fact]
    public void Uses_the_later_stamp()
    {
        var older = Now.AddMinutes(-50).ToString("o");
        var newer = Now.AddMinutes(-5).ToString("o");
        var opens = GateCooldown.GetOpensUtc(older, newer, Now);
        Assert.Equal(Now.AddMinutes(55), opens);
    }

    [Fact]
    public void Empty_timestamp_formats_as_missing()
    {
        Assert.Equal("(无)", GateCooldown.FormatWhen(null, Now));
        Assert.Equal("(无)", GateCooldown.FormatWhen("   ", Now));
    }

    [Fact]
    public void Rewind_is_two_hours_and_utc()
    {
        var stamp = GateCooldown.FormatRewind(Now);
        Assert.Equal("2026-11-09T04:00:00Z", stamp);
        var parsed = GateCooldown.TryParseUtc(stamp);
        Assert.Equal(Now.AddHours(-2), parsed);
    }
}

public class IrisAndCreativeTests
{
    [Fact]
    public void Replaces_only_asid_and_keeps_placement()
    {
        var uri = "https://fd.api.iris.microsoft.com/v4/api/selection?asid=AABBCCDD11223344&placement=88000820&locale=zh-CN";
        var updated = IrisUriBuilder.WithNewAsid(uri);
        Assert.Contains("placement=88000820", updated, StringComparison.Ordinal);
        Assert.Contains("locale=zh-CN", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("AABBCCDD11223344", updated, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("asid=[0-9A-F]{32}", updated);
    }

    [Fact]
    public void Fresh_uri_uses_machine_inputs()
    {
        var uri = IrisUriBuilder.BuildFresh("CN", "zh-CN", "10.0.26200.1");
        Assert.Contains("placement=88000820", uri, StringComparison.Ordinal);
        Assert.Contains("country=CN", uri, StringComparison.Ordinal);
        Assert.Contains("locale=zh-CN", uri, StringComparison.Ordinal);
        Assert.Contains("devosver=10.0.26200.1", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Detects_image_fields()
    {
        Assert.True(IrisUriBuilder.ContainsImageFields("{\"landscapeImage\":1}"));
        Assert.False(IrisUriBuilder.ContainsImageFields("{\"status\":204}"));
    }

    [Fact]
    public void Merger_drops_system_images_keeps_injected_and_dedupes()
    {
        var fresh = new JsonObject
        {
            ["ad"] = new JsonObject
            {
                ["landscapeImage"] = new JsonObject { ["asset"] = @"C:\Users\a\Pictures\SpotlightDesktop\a.jpg" }
            }
        };
        var injectedOld = new JsonObject
        {
            ["ad"] = new JsonObject
            {
                ["landscapeImage"] = new JsonObject { ["asset"] = @"D:\Pictures\SpotlightDesktop\b.jpg" }
            }
        };
        var duplicate = new JsonObject
        {
            ["ad"] = new JsonObject
            {
                ["landscapeImage"] = new JsonObject { ["asset"] = @"C:\Users\a\Pictures\SpotlightDesktop\a.jpg" }
            }
        };
        Assert.False(CreativeMerger.ShouldKeepOld(@"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\DesktopSpotlight\Assets\Images\image_0.jpg"));
        Assert.True(CreativeMerger.ShouldKeepOld(CreativeMerger.LandscapeAsset(injectedOld)));

        var merged = CreativeMerger.Merge([fresh], [injectedOld, duplicate]);
        Assert.Equal(2, merged.Count);
        var json = CreativeMerger.Serialize(merged);
        Assert.StartsWith("[", json, StringComparison.Ordinal);
        Assert.EndsWith("]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemApps", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parses_string_items()
    {
        const string payload = """
            {"batchrsp":{"items":[{"item":"{\"ad\":{\"landscapeImage\":{\"asset\":\"https://example/a.jpg\"}}}"}]}}
            """;
        var items = CreativeMerger.ParseBatchItems(payload);
        Assert.Single(items);
        Assert.Equal("https://example/a.jpg", CreativeMerger.LandscapeAsset(items[0]));
    }

    [Fact]
    public void Rejects_path_escape()
    {
        Assert.False(ImageDestination.TryCreate(@"C:\Users\a\Pictures\SpotlightDesktop", "https://cdn.example/../../Windows/notepad.exe", out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.True(ImageDestination.TryCreate(@"C:\Users\a\Pictures\SpotlightDesktop", "https://cdn.example/photos/a.jpg?x=1", out var path, out _));
        Assert.EndsWith("a.jpg", path, StringComparison.OrdinalIgnoreCase);
    }
}

public class HealthAndAdviceTests
{
    [Fact]
    public void Skip_requires_combined_evidence()
    {
        Assert.False(HealthRules.ShouldSkipOneShot(new SpotlightSnapshot { StateSuccess = true }, true));
        Assert.False(HealthRules.ShouldSkipOneShot(new SpotlightSnapshot
        {
            HasCache88000820 = true,
            CacheJsonHasImage = true
        }, true));
        Assert.False(HealthRules.ShouldSkipOneShot(new SpotlightSnapshot
        {
            StateSuccess = true,
            LastAttempt = "2026-11-09T00:00:00Z",
            IsInjectedWallpaper = true,
            Wallpaper = @"C:\Users\a\Pictures\SpotlightDesktop\a.jpg"
        }, true));
        Assert.True(HealthRules.ShouldSkipOneShot(new SpotlightSnapshot
        {
            StateSuccess = true,
            LastAttempt = "2026-11-09T00:00:00Z",
            HasCache88000820 = true,
            CacheJsonHasImage = true
        }, true));
        Assert.True(HealthRules.ShouldSkipOneShot(new SpotlightSnapshot
        {
            Wallpaper = @"C:\Users\a\AppData\Local\Packages\Iris\a.jpg",
            HasCache88000820 = true
        }, true));
        Assert.False(HealthRules.ShouldSkipOneShot(new SpotlightSnapshot { StateSuccess = true, LastAttempt = "2026-11-09T00:00:00Z", HasCache88000820 = true, CacheJsonHasImage = true }, false));
    }

    [Fact]
    public void Missing_timer_suggests_repair()
    {
        var advice = AdviceBuilder.Build(HealthySnapshot(), new TaskReport { EnteredDesktopPackage = true, Tasks = [] }, OkIris(), DateTime.UtcNow);
        Assert.Contains(advice, a => a.Contains("--repair", StringComparison.Ordinal));
    }

    [Fact]
    public void Healthy_pipeline_has_single_ok_message()
    {
        var advice = AdviceBuilder.Build(
            new SpotlightSnapshot
            {
                BackgroundType = 3,
                StateSuccess = true,
                LastAttempt = "2026-11-09T00:00:00Z",
                HasCache88000820 = true,
                CacheJsonHasImage = true,
                Wallpaper = @"C:\Users\a\AppData\Local\Packages\Iris\a.jpg"
            },
            new TaskReport
            {
                EnteredDesktopPackage = true,
                Aumid = "MicrosoftWindows.Client.CBS_cw5n1h2txyewy!Global.DesktopSpotlight",
                Access = "Allowed",
                Tasks = TaskCatalog.CoreTasks
            },
            OkIris(),
            DateTime.UtcNow);
        Assert.True(AdviceBuilder.IsHealthy(advice));
    }

    [Fact]
    public void Missing_cache_does_not_claim_it_never_succeeded()
    {
        var report = DiagnosisComposer.Create(
            new OsInfo("Windows 11", "25H2", "26200", "1", "1.0.0.0", true),
            new SpotlightSnapshot { BackgroundType = 3 },
            new TaskReport
            {
                EnteredDesktopPackage = true,
                Aumid = "pkg!Global.DesktopSpotlight",
                Access = "Allowed",
                Tasks = TaskCatalog.CoreTasks
            },
            OkIris(),
            DateTime.UtcNow,
            "TEST");
        var cache = Assert.Single(report.Findings, f => f.Label == "桌面内容缓存");
        Assert.Contains("不能据此断言", cache.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("从未成功请求过", report.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Change_detector_ignores_another_fallback_image()
    {
        var baseline = new SpotlightSnapshot
        {
            Wallpaper = @"C:\Windows\SystemApps\x\DesktopSpotlight\Assets\Images\image_0.jpg",
            IsSystemFallbackWallpaper = true,
            IrisFileCount = 1,
            IrisFullSizeCount = 0
        };
        var stillFallback = baseline with
        {
            Wallpaper = @"C:\Windows\SystemApps\x\DesktopSpotlight\Assets\Images\image_1.jpg",
            IsSystemFallbackWallpaper = true
        };
        Assert.False(ChangeDetector.HasObservedChange(baseline, stillFallback));
        Assert.True(ChangeDetector.HasObservedChange(baseline, baseline with
        {
            Wallpaper = @"C:\Users\a\Pictures\SpotlightDesktop\n.jpg",
            IsSystemFallbackWallpaper = false
        }));
        Assert.True(ChangeDetector.HasObservedChange(baseline, baseline with { LastAttempt = "2026-11-09T01:00:00Z" }));
        Assert.True(ChangeDetector.HasObservedChange(baseline, baseline with { IrisFullSizeCount = 2 }));
        Assert.False(ChangeDetector.HasObservedChange(baseline, baseline));
    }

    private static SpotlightSnapshot HealthySnapshot() => new() { BackgroundType = 3 };

    private static IrisProbeResult OkIris() => new(true, true, "可访问，且正在返回桌面壁纸内容。");
}

public class HandshakeAndArgsTests
{
    [Fact]
    public void Handshake_requires_nonce_and_end()
    {
        var wrapped = HelperHandshake.Wrap("W 'TASK=Demo'", @"C:\temp\o's\out.txt", "abc123");
        Assert.Contains("AppendAllText", wrapped, StringComparison.Ordinal);
        Assert.Contains("END=1", wrapped, StringComparison.Ordinal);
        Assert.Contains("C:\\temp\\o''s\\out.txt", wrapped, StringComparison.Ordinal);

        var good = HelperHandshake.Parse("NONCE=abc123\r\nTASK=Demo\r\nEND=1\r\n", "abc123");
        Assert.True(good.SawEnd);
        Assert.True(good.NonceOk);
        Assert.Equal(["TASK=Demo"], good.Lines);

        var wrong = HelperHandshake.Parse("NONCE=other\r\nEND=1\r\n", "abc123");
        Assert.True(wrong.SawEnd);
        Assert.False(wrong.NonceOk);
    }

    [Fact]
    public void One_shot_name_is_exact_and_scripts_do_not_use_application_trigger()
    {
        Assert.True(TaskCatalog.IsRemovableOneShotName(TaskCatalog.OneShotName));
        Assert.False(TaskCatalog.IsRemovableOneShotName(TaskCatalog.UpdateTimer));
        Assert.False(TaskCatalog.IsRemovableOneShotName(TaskCatalog.OneShotName + ".extra"));
        Assert.Contains("TaskEntryPoint = $p.n", EmbeddedScripts.RegisterDesktop, StringComparison.Ordinal);
        Assert.Contains("TaskEntryPoint = 'DesktopSpotlight.BackgroundTask.UpdateTimer'", EmbeddedScripts.ScheduleOneShot, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicationTrigger", EmbeddedScripts.ScheduleOneShot, StringComparison.Ordinal);
        Assert.Contains("$t.Name -ne $name", EmbeddedScripts.RemoveOneShot, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationHandler", EmbeddedScripts.RegisterIris, StringComparison.Ordinal);
    }

    [Fact]
    public void Args_open_gui_only_without_switches()
    {
        Assert.True(ProgramArgs.Parse([]).LaunchGui);
        var diag = ProgramArgs.Parse(["--diagnose", "--redact", "--log", @"D:\a.log"]);
        Assert.False(diag.LaunchGui);
        Assert.True(diag.Diagnose);
        Assert.True(diag.Redact);
        Assert.Equal(@"D:\a.log", diag.LogPath);
        Assert.Equal("无法识别的参数：--nope", ProgramArgs.Parse(["--nope"]).Error);
        var force = ProgramArgs.Parse(["--force", "--open-gate", "--wait"]);
        Assert.True(force.ToRequest().Force);
        Assert.True(force.ToRequest().OpenGate);
        Assert.True(force.ToRequest().Wait);
        Assert.False(force.ToRequest().Diagnose);
    }

    [Fact]
    public void Exit_codes_are_stable()
    {
        Assert.Equal(0, ExitCodes.For(RunOutcome.DiagnoseOk));
        Assert.Equal(1, ExitCodes.For(RunOutcome.DiagnoseNeedsAttention));
        Assert.Equal(0, ExitCodes.For(RunOutcome.Completed));
        Assert.Equal(2, ExitCodes.For(RunOutcome.Error));
        Assert.Equal(3, ExitCodes.For(RunOutcome.Cancelled));
        Assert.Equal(4, ExitCodes.For(RunOutcome.WaitTimeout));
    }

    [Fact]
    public void Rollback_conflicts_before_any_restore()
    {
        var entry = new BackupEntry
        {
            WrittenKind = "String",
            WrittenValue = "new",
            OriginalKind = "String",
            OriginalValue = "old"
        };
        Assert.Equal(RollbackDecision.Conflict, RollbackPlanner.Judge(entry, "String", "changed-again"));
        Assert.Equal(RollbackDecision.Restore, RollbackPlanner.Judge(entry, "String", "new"));
        Assert.Equal(RollbackDecision.SkipNotWritten, RollbackPlanner.Judge(new BackupEntry(), "String", "x"));
    }

    [Fact]
    public void Redactor_truncates_asid_and_can_hide_identity()
    {
        var user = Environment.UserName;
        var text = Redactor.Apply("asid=AABBCCDD11223344556677889900AABB user=" + user, redactIdentity: true);
        Assert.Contains("asid=AABBCCDD…", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<user>", text, StringComparison.Ordinal);
    }
}
