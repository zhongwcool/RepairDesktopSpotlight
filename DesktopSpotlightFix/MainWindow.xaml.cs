using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using DesktopSpotlightFix.Core;
using DesktopSpotlightFix.Helpers;
using DesktopSpotlightFix.Logging;

namespace DesktopSpotlightFix;

public partial class MainWindow : Window
{
    private readonly FileLogger _log;
    private readonly RepairOrchestrator _orchestrator;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _runTask;
    private bool _allowClose;
    private DiagnosisReport? _report;
    private Guidance? _guidance;

    public MainWindow(FileLogger log, RepairOrchestrator orchestrator)
    {
        InitializeComponent();
        DpiTextRenderingHelper.Attach(this);
        _log = log;
        _orchestrator = orchestrator;
        LogPathText.Text = "日志：" + log.FilePath + "    备份：" + AppPaths.BackupRoot;
        AppendLog("日志文件：" + log.FilePath);
        AppendLog("备份目录：" + AppPaths.BackupRoot);
        SetBusy(true, "正在检查…");
        Loaded += async (_, _) => await RunAsync(new ToolRequest { Diagnose = true }, "正在检查…");
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(new ToolRequest { Diagnose = true }, "正在检查…");

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_guidance?.Action != GuidanceAction.Repair)
            return;
        if (!Confirm(RepairMessage(), "一键修复"))
            return;
        await RunAsync(new ToolRequest { Repair = true }, "正在修复…");
    }

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(ForceMessage(), "强制刷新"))
            return;
        await RunAsync(new ToolRequest
        {
            Force = true,
            OpenGate = GateIsClosed()
        }, "正在刷新…");
    }

    private async void Inject_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("将从微软下载一组图片到「图片\\SpotlightDesktop」，放进聚焦的换图清单。这只是临时垫图，不能代替每天自动换图。背景仍保持 Windows 聚焦。是否继续？", "放入备用图"))
            return;
        await RunAsync(new ToolRequest { InjectFallback = true }, "正在放入备用图…");
    }

    private async void ClearOld_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("会删除超过 " + OldImageCleaner.MaxAgeDays + " 天、而且「下一个背景」已经用不到的聚焦图片和备用图。最近的图、正在显示的图、换图清单里的图、Windows 自带底图都会留下。删掉的文件不能恢复。是否继续？", "移除旧图"))
            return;
        await RunAsync(new ToolRequest { ClearOldImages = true }, "正在移除旧图…");
    }

    private async Task RunAsync(ToolRequest request, string status)
    {
        if (_runTask is { IsCompleted: false })
            return;

        if (request.Wait && ShowLogCheck.IsChecked != true)
            ShowLogCheck.IsChecked = true;

        SetBusy(true, status);
        ShowBusyVerdict(status);
        var progress = new Progress<string>(AppendLog);
        var task = _orchestrator.ExecuteAsync(request, progress, _lifetime.Token);
        _runTask = task;
        string done = "就绪";
        try
        {
            var result = await task;
            ShowResult(result);
            done = ShortStatus(result);
            AppendLog("结果：" + result.Summary);
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            AppendLog("出错：" + ex.Message);
            ApplyGuidance(new Guidance("没有完成", ex.Message, GuidanceAction.None, GuidanceTone.Blocked));
            done = "出错";
        }
        finally
        {
            SetBusy(false, done);
        }
    }

    private void ShowResult(ToolResult result)
    {
        if (result.Report != null)
        {
            _report = result.Report;
            FindingsList.ItemsSource = result.Report.Findings;
            FindingsPlaceholder.Visibility = Visibility.Collapsed;
            FindingsScroller.Visibility = Visibility.Visible;
        }

        ApplyGuidance(UserGuidance.ForResult(result, DateTime.UtcNow));
    }

    private void ApplyGuidance(Guidance guidance)
    {
        _guidance = guidance;
        VerdictTitle.Text = guidance.Title;
        VerdictBody.Text = guidance.Message;
        ApplyTone(guidance.Tone);
        PrimaryButton.Visibility = guidance.Action == GuidanceAction.Repair ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Content = "一键修复";
    }

    private void ShowBusyVerdict(string status)
    {
        VerdictTitle.Text = status.TrimEnd('…', '.');
        VerdictBody.Text = "请稍等。";
        ApplyTone(GuidanceTone.Neutral);
        PrimaryButton.Visibility = Visibility.Collapsed;
    }

    private void ApplyTone(GuidanceTone tone)
    {
        var (background, border, title) = tone switch
        {
            GuidanceTone.Healthy => ("#ECFDF3", "#6EE7B7", "#166534"),
            GuidanceTone.Repair => ("#FEF2F2", "#FECACA", "#B42318"),
            GuidanceTone.Refresh => ("#FFFBEB", "#FDE68A", "#A15C07"),
            GuidanceTone.Blocked => ("#FEF2F2", "#FECACA", "#B42318"),
            GuidanceTone.External => ("#FFFBEB", "#FDE68A", "#A15C07"),
            _ => ("#F8FAFC", "#E2E8F0", "#1E293B")
        };
        VerdictCard.Background = Freeze(background);
        VerdictCard.BorderBrush = Freeze(border);
        VerdictTitle.Foreground = Freeze(title);
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static string ShortStatus(ToolResult result) => result.Outcome switch
    {
        RunOutcome.DiagnoseOk or RunOutcome.DiagnoseNeedsAttention => "检查完成",
        RunOutcome.Completed => "操作完成",
        RunOutcome.WaitTimeout => "等待结束",
        RunOutcome.Cancelled => "已取消",
        RunOutcome.Error => "出错",
        _ => "就绪"
    };

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(line));
            return;
        }

        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        PrimaryButton.IsEnabled = !busy;
        RecheckButton.IsEnabled = !busy;
        ForceButton.IsEnabled = !busy;
        InjectButton.IsEnabled = !busy;
        ClearOldButton.IsEnabled = !busy;
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private string RepairMessage() =>
        "将补上缺失的换图任务，并把背景保持为 Windows 聚焦。不会改成普通图片。如果最近没有拉到新图的记录，冷却结束后还会安排一次刷新，大约 15–30 分钟后才可能看到新壁纸。是否继续？";

    private string ForceMessage()
    {
        var text = "将安排系统再拉一次新图。大约 15–30 分钟后才可能换上，不是马上生效。";
        if (GateIsClosed())
            text += "系统如果还在等大约 1 小时才允许再拉，会把这段等待清掉。";
        return text + "是否继续？";
    }

    private bool GateIsClosed()
    {
        var snapshot = _report?.Snapshot;
        if (snapshot == null)
            return true;
        return GateCooldown.GetOpensUtc(snapshot.WallpaperRefresh, snapshot.Rotation, DateTime.UtcNow).HasValue;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = _report?.Text;
        if (string.IsNullOrWhiteSpace(text))
            text = LogBox.Text;
        Clipboard.SetText(text);
        StatusText.Text = "已复制摘要";
        AppendLog("已复制摘要。");
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "/select,\"" + _log.FilePath + "\"",
            UseShellExecute = true
        });
    }

    private void ShowLog_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        LogPanel.Visibility = ShowLogCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (_runTask is { IsCompleted: false })
        {
            e.Cancel = true;
            _ = CloseAfterCancelAsync();
            return;
        }

        _lifetime.Cancel();
        base.OnClosing(e);
    }

    private async Task CloseAfterCancelAsync()
    {
        StatusText.Text = "正在停止…";
        _lifetime.Cancel();
        try
        {
            if (_runTask != null)
                await _runTask;
        }
        catch (Exception)
        {
        }

        _allowClose = true;
        Close();
    }
}
