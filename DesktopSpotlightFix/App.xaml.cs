using System.Windows;
using DesktopSpotlightFix.Cli;
using DesktopSpotlightFix.Core;
using DesktopSpotlightFix.Logging;
using DesktopSpotlightFix.PackageIdentity;

namespace DesktopSpotlightFix;

public partial class App : Application
{
    private FileLogger? _logger;
    private IrisProbe? _iris;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => PackageWorkDir.CleanupActiveBestEffort();

        var args = ProgramArgs.Parse(e.Args);
        if (args.Error != null)
        {
            ConsoleAttach.PrepareForCli();
            Console.Error.WriteLine(args.Error);
            Console.Error.WriteLine(ProgramArgs.HelpText);
            Shutdown(2);
            return;
        }

        if (args.Help)
        {
            ConsoleAttach.PrepareForCli();
            Console.WriteLine(ProgramArgs.HelpText);
            Shutdown(0);
            return;
        }

        _logger = new FileLogger(args.LogPath, args.Redact);
        PackageWorkDir.CleanupStale(_logger);
        _iris = new IrisProbe();
        var snapshots = new SnapshotReader();
        var orchestrator = new RepairOrchestrator(
            _logger,
            new InDesktopPackageInvoker(_logger),
            snapshots,
            _iris,
            new FallbackInjector(_iris, snapshots));

        if (!args.LaunchGui)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ConsoleAttach.PrepareForCli();
            _ = RunCliAsync(args, orchestrator);
            return;
        }

        var window = new MainWindow(_logger, orchestrator);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _iris?.Dispose();
        _logger?.Dispose();
        base.OnExit(e);
    }

    private async Task RunCliAsync(ProgramArgs args, RepairOrchestrator orchestrator)
    {
        using var cts = new CancellationTokenSource();
        try
        {
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            var code = await CliRunner.RunAsync(args, _logger!, orchestrator, cts.Token).ConfigureAwait(true);
            Shutdown(code);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex.ToString());
            try
            {
                Console.Error.WriteLine("出错：" + ex.Message);
            }
            catch (IOException)
            {
            }

            Shutdown(2);
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error(e.Exception.ToString());
        if (MainWindow != null)
            MessageBox.Show(MainWindow, e.Exception.Message, "桌面聚焦修复", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(2);
    }
}
