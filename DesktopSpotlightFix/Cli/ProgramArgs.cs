using System.Text;

namespace DesktopSpotlightFix.Cli;

public sealed class ProgramArgs
{
    public bool Diagnose { get; private set; }
    public bool Repair { get; private set; }
    public bool Force { get; private set; }
    public bool OpenGate { get; private set; }
    public bool InjectFallback { get; private set; }
    public bool Wait { get; private set; }
    public bool Rollback { get; private set; }
    public bool ClearOneShot { get; private set; }
    public bool Redact { get; private set; }
    public bool Help { get; private set; }
    public string? LogPath { get; private set; }
    public string? Error { get; private set; }

    public bool LaunchGui =>
        Error == null &&
        !Help &&
        !Diagnose &&
        !Repair &&
        !Force &&
        !OpenGate &&
        !InjectFallback &&
        !Wait &&
        !Rollback &&
        !ClearOneShot &&
        LogPath == null &&
        !Redact;

    public Core.ToolRequest ToRequest() => new()
    {
        Diagnose = Diagnose || (!Repair && !Force && !InjectFallback && !OpenGate && !Rollback && !ClearOneShot),
        Repair = Repair,
        Force = Force,
        OpenGate = OpenGate,
        InjectFallback = InjectFallback,
        Wait = Wait,
        Rollback = Rollback,
        ClearOneShot = ClearOneShot
    };

    public static ProgramArgs Parse(string[] args)
    {
        var result = new ProgramArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i].Trim();
            switch (token.ToLowerInvariant())
            {
                case "--diagnose":
                case "-diagnose":
                    result.Diagnose = true;
                    break;
                case "--repair":
                    result.Repair = true;
                    break;
                case "--force":
                    result.Force = true;
                    break;
                case "--open-gate":
                    result.OpenGate = true;
                    break;
                case "--inject-fallback":
                    result.InjectFallback = true;
                    break;
                case "--wait":
                    result.Wait = true;
                    break;
                case "--rollback":
                    result.Rollback = true;
                    break;
                case "--clear-oneshot":
                    result.ClearOneShot = true;
                    break;
                case "--redact":
                    result.Redact = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    result.Help = true;
                    break;
                case "--log":
                    if (i + 1 >= args.Length)
                    {
                        result.Error = "缺少 --log 后面的路径。";
                        return result;
                    }

                    result.LogPath = args[++i];
                    break;
                default:
                    result.Error = "无法识别的参数：" + token;
                    return result;
            }
        }

        return result;
    }

    public const string HelpText =
        """
        桌面聚焦修复  DesktopSpotlightFix.exe

        无参数              打开窗口，并做一次只读体检
        --diagnose          只读体检，不改注册表、文件或任务
        --repair            补齐缺失的系统后台任务；已有官方拉图证据时不安排单次拉取
        --force             即使任务都在，也安排一次单次拉取
        --open-gate         把冷却时间戳回拨至少 2 小时
        --inject-fallback   下载一组图写入「下一个背景」清单（不能代替官方每日拉图）
        --wait              安排后最多观察约 35 分钟
        --rollback          只回滚本工具最近一次备份写下的项；发现冲突则中止
        --clear-oneshot     只注销 DesktopSpotlight.BackgroundTask.UpdateTimer.oneshot
        --log <文件>        指定日志文件
        --redact            日志里隐藏机器名和用户名
        --help              显示本说明

        退出码：0 完成且无需处理，或修复流程按设计完成；1 体检发现需要处理的问题；
        2 运行错误；3 用户取消；4 等待观察超时。
        安排成功只表示系统任务已登记，大约 15–30 分钟后才可能拉图。
        面向 Windows 11，依赖本机的 MicrosoftWindows.Client.CBS 和 Invoke-CommandInDesktopPackage。
        """;
}

public static class CliRunner
{
    public static async Task<int> RunAsync(ProgramArgs args, Logging.FileLogger log, Core.RepairOrchestrator orchestrator, CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(line =>
        {
            try
            {
                Console.WriteLine(line);
            }
            catch (IOException)
            {
            }
        });

        var result = await orchestrator.ExecuteAsync(args.ToRequest(), progress, cancellationToken).ConfigureAwait(false);
        Write("结果：" + result.Summary);
        Write("退出码 " + result.ExitCode);
        Write("日志：" + log.FilePath);
        return result.ExitCode;
    }

    private static void Write(string line)
    {
        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
        }
    }
}
