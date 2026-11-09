using System.Diagnostics;
using System.Windows;
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

    public MainWindow(FileLogger log, RepairOrchestrator orchestrator)
    {
        InitializeComponent();
        DpiTextRenderingHelper.Attach(this);
        _log = log;
        _orchestrator = orchestrator;
        LogPathText.Text = "日志：" + log.FilePath + "    备份：" + AppPaths.BackupRoot;
        AppendLog("日志文件：" + log.FilePath);
        AppendLog("备份目录：" + AppPaths.BackupRoot);
        Loaded += async (_, _) => await RunAsync(new ToolRequest { Diagnose = true }, "正在体检…");
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(new ToolRequest { Diagnose = true }, "正在体检…");

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(RepairMessage(), "修复缺失任务"))
            return;
        await RunAsync(new ToolRequest
        {
            Repair = true,
            OpenGate = OpenGateCheck.IsChecked == true,
            Wait = WaitCheck.IsChecked == true
        }, "正在修复…");
    }

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(ForceMessage(), "强制安排拉图"))
            return;
        await RunAsync(new ToolRequest
        {
            Force = true,
            OpenGate = OpenGateCheck.IsChecked == true,
            Wait = WaitCheck.IsChecked == true
        }, "正在安排…");
    }

    private async void Gate_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("将把 WallpaperRefresh 回拨至少 2 小时。若 Rotation 仍关着冷却，也会一并回拨。这只影响当前用户的聚焦时间戳。是否继续？", "打开冷却门限"))
            return;
        await RunAsync(new ToolRequest { OpenGate = true }, "正在打开冷却门限…");
    }

    private async void Inject_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("将从微软接口下载一组图片到「图片\\SpotlightDesktop」，并写入聚焦清单。这是补图，不能代替官方每日拉图。背景会保持 Windows 聚焦。是否继续？", "注入备用图"))
            return;
        await RunAsync(new ToolRequest { InjectFallback = true }, "正在注入备用图…");
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("只恢复本工具最近一次备份写下的注册表项。若发现之后又被改过，会中止并且不覆盖。是否继续？", "回滚"))
            return;
        await RunAsync(new ToolRequest { Rollback = true }, "正在回滚…");
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("只会注销名字完全等于 DesktopSpotlight.BackgroundTask.UpdateTimer.oneshot 的单次任务，不会删除正式的每日 UpdateTimer。是否继续？", "清理单次任务"))
            return;
        await RunAsync(new ToolRequest { ClearOneShot = true }, "正在清理单次任务…");
    }

    private async Task RunAsync(ToolRequest request, string status)
    {
        if (_runTask is { IsCompleted: false })
            return;

        SetBusy(true, status);
        var progress = new Progress<string>(AppendLog);
        var task = _orchestrator.ExecuteAsync(request, progress, _lifetime.Token);
        _runTask = task;
        try
        {
            var result = await task;
            ShowResult(result);
            AppendLog("结果：" + result.Summary);
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            AppendLog("出错：" + ex.Message);
            StatusText.Text = "出错";
        }
        finally
        {
            SetBusy(false, StatusText.Text == "出错" ? "出错" : "就绪");
        }
    }

    private void ShowResult(ToolResult result)
    {
        StatusText.Text = result.Summary;
        if (result.Report == null)
            return;
        _report = result.Report;
        FindingsList.ItemsSource = result.Report.Findings;
        AdviceBox.Text = string.Join(Environment.NewLine, result.Report.Advice.Select(a => "• " + a));
    }

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
        DiagnoseButton.IsEnabled = !busy;
        RepairButton.IsEnabled = !busy;
        ForceButton.IsEnabled = !busy;
        GateButton.IsEnabled = !busy;
        InjectButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        OpenGateCheck.IsEnabled = !busy;
        WaitCheck.IsEnabled = !busy;
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private string RepairMessage()
    {
        var text = "将补齐缺失的 DesktopSpotlight / IrisService 后台任务，并把聚焦开关设为启用。若已有 Wallpapers 项，背景类型会写成 Windows 聚焦（3）。不会把壁纸改成「图片」模式。";
        return text + ExtraOptions() + "是否继续？";
    }

    private string ForceMessage()
    {
        var text = "将安排系统单次任务 DesktopSpotlight.BackgroundTask.UpdateTimer.oneshot。注册成功只表示已安排，大约 15–30 分钟后系统才可能去拉取，不表示壁纸已经换好。";
        return text + ExtraOptions() + "是否继续？";
    }

    private string ExtraOptions()
    {
        var extra = "";
        if (OpenGateCheck.IsChecked == true)
            extra += "同时会把冷却时间戳回拨至少 2 小时。";
        if (WaitCheck.IsChecked == true)
            extra += "安排之后，这个窗口最多再观察约 35 分钟。";
        return extra;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = _report?.Text;
        if (string.IsNullOrWhiteSpace(text))
            text = LogBox.Text;
        Clipboard.SetText(text);
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
