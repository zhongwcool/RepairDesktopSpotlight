using System.Diagnostics;
using System.Text;
using DesktopSpotlightFix.Core;

namespace DesktopSpotlightFix.PackageIdentity;

public sealed record PackageCallResult(bool Success, string Detail, IReadOnlyList<string> Lines);

public sealed record OneShotScheduleResult(bool Scheduled, bool AlreadyExisted, string Detail, IReadOnlyList<string> Lines);

public interface IPackageTaskClient
{
    Task<TaskReport> GetTaskReportAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RegisterMissingAsync(CancellationToken cancellationToken);
    Task<OneShotScheduleResult> ScheduleOneShotAsync(CancellationToken cancellationToken);
    Task<PackageCallResult> RemoveOneShotAsync(CancellationToken cancellationToken);
}

public sealed class InDesktopPackageInvoker : IPackageTaskClient
{
    private readonly Logging.FileLogger _log;

    public InDesktopPackageInvoker(Logging.FileLogger log)
    {
        _log = log;
    }

    public async Task<TaskReport> GetTaskReportAsync(CancellationToken cancellationToken)
    {
        var desktop = await InvokeAsync(TaskCatalog.DesktopAppId, EmbeddedScripts.EnumTasks, 45, cancellationToken).ConfigureAwait(false);
        PackageCallResult? iris = null;
        if (desktop.Success)
            iris = await InvokeAsync(TaskCatalog.IrisAppId, EmbeddedScripts.EnumTasks, 45, cancellationToken).ConfigureAwait(false);

        var tasks = new List<string>();
        var lines = new List<string>();
        lines.AddRange(desktop.Lines);
        CollectTasks(desktop.Lines, tasks);
        if (iris != null)
        {
            lines.AddRange(iris.Lines);
            CollectTasks(iris.Lines, tasks);
        }

        var distinct = tasks.Distinct(StringComparer.Ordinal).ToArray();
        return new TaskReport
        {
            Tasks = distinct,
            Aumid = FirstValue(desktop.Lines, "AUMID="),
            Access = FirstValue(desktop.Lines, "ACCESS="),
            EnteredDesktopPackage = desktop.Success,
            DesktopError = desktop.Success ? null : desktop.Detail,
            IrisError = iris == null ? "未进入 IrisService：DesktopSpotlight 身份调用没有成功。" : iris.Success ? null : iris.Detail,
            Lines = lines
        };
    }

    public async Task<IReadOnlyList<string>> RegisterMissingAsync(CancellationToken cancellationToken)
    {
        var desktop = await InvokeAsync(TaskCatalog.DesktopAppId, EmbeddedScripts.RegisterDesktop, 50, cancellationToken).ConfigureAwait(false);
        var iris = await InvokeAsync(TaskCatalog.IrisAppId, EmbeddedScripts.RegisterIris, 50, cancellationToken).ConfigureAwait(false);
        var lines = new List<string> { "DesktopSpotlight:" };
        lines.AddRange(desktop.Lines);
        if (!desktop.Success)
            lines.Add("调用失败：" + desktop.Detail);
        lines.Add("IrisService:");
        lines.AddRange(iris.Lines);
        if (!iris.Success)
            lines.Add("调用失败：" + iris.Detail);
        if (!desktop.Success && !iris.Success)
            throw new InvalidOperationException("两个包身份都没能完成任务注册。" + desktop.Detail);
        return lines;
    }

    public async Task<OneShotScheduleResult> ScheduleOneShotAsync(CancellationToken cancellationToken)
    {
        var call = await InvokeAsync(TaskCatalog.DesktopAppId, EmbeddedScripts.ScheduleOneShot, 50, cancellationToken).ConfigureAwait(false);
        if (!call.Success)
            return new OneShotScheduleResult(false, false, call.Detail, call.Lines);

        var scheduled = call.Lines.FirstOrDefault(l => l.StartsWith("SCHEDULED=", StringComparison.Ordinal));
        var failed = call.Lines.FirstOrDefault(l => l.StartsWith("FAIL=", StringComparison.Ordinal));
        if (scheduled == null)
            return new OneShotScheduleResult(false, false, failed ?? "没有收到 SCHEDULED 结果。", call.Lines);

        var value = scheduled["SCHEDULED=".Length..];
        var existing = value == "EXISTING";
        return new OneShotScheduleResult(true, existing, existing ? "EXISTING" : value, call.Lines);
    }

    public async Task<PackageCallResult> RemoveOneShotAsync(CancellationToken cancellationToken)
    {
        if (!TaskCatalog.IsRemovableOneShotName(TaskCatalog.OneShotName))
            return new PackageCallResult(false, "单次任务名字未通过精确匹配，已拒绝注销。", []);

        var call = await InvokeAsync(TaskCatalog.DesktopAppId, EmbeddedScripts.RemoveOneShot, 45, cancellationToken).ConfigureAwait(false);
        if (!call.Success)
            return call;
        if (call.Lines.Any(l => l.StartsWith("REFUSED=", StringComparison.Ordinal) || l.StartsWith("FAIL=", StringComparison.Ordinal)))
            return new PackageCallResult(false, "注销单次任务失败。正式 UpdateTimer 不会被这个操作删除。", call.Lines);
        return call;
    }

    private async Task<PackageCallResult> InvokeAsync(string appId, string body, int timeoutSec, CancellationToken cancellationToken)
    {
        if (!TaskCatalog.IsAllowedAppId(appId))
            throw new InvalidOperationException("拒绝未允许的 AppId。");

        var nonce = Guid.NewGuid().ToString("N");
        var work = PackageWorkDir.Create(nonce, _log);
        try
        {
        var outPath = Path.Combine(work.Path, "out.txt");
        var launcherOut = Path.Combine(work.Path, "launcher.txt");
        var scriptPath = Path.Combine(work.Path, "inpkg.ps1");
        var launcherPath = Path.Combine(work.Path, "launcher.ps1");
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(scriptPath, HelperHandshake.Wrap(body, outPath, nonce), utf8Bom, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(launcherPath, BuildLauncher(appId, scriptPath, launcherOut, nonce), utf8Bom, cancellationToken).ConfigureAwait(false);

        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
            return new PackageCallResult(false, "找不到 Windows PowerShell 5.1：" + powershell, []);

        var psi = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + launcherPath + "\"",
            WorkingDirectory = work.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => AppendCapped(stderr, e.Data);

        if (!process.Start())
            return new PackageCallResult(false, "无法启动 powershell.exe。", []);

        work.MarkHelper(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var launcherText = "";
        var innerText = "";
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                launcherText = await ReadSharedAsync(launcherOut).ConfigureAwait(false);
                innerText = await ReadSharedAsync(outPath).ConfigureAwait(false);
                var launcherNow = HelperHandshake.Parse(launcherText, nonce);
                var innerNow = HelperHandshake.Parse(innerText, nonce);
                if (launcherNow.Lines.Any(l => l.StartsWith("LAUNCHER_ERROR=", StringComparison.Ordinal)) && launcherNow.SawEnd)
                    break;
                if (innerNow.SawEnd && innerNow.NonceOk && launcherNow.SawEnd && launcherNow.NonceOk)
                    break;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warn("操作已取消，正在结束本次 helper。");
            await StopKnownHelpersAsync(process, innerText).ConfigureAwait(false);
            throw;
        }

        if (!SafeHasExited(process) && DateTime.UtcNow >= deadline)
        {
            _log.Warn("包身份调用超时，正在结束本次 helper。");
            await StopKnownHelpersAsync(process, innerText).ConfigureAwait(false);
        }

        if (SafeHasExited(process))
        {
            try
            {
                process.WaitForExit();
            }
            catch (Exception)
            {
            }
        }

        launcherText = await ReadSharedAsync(launcherOut).ConfigureAwait(false);
        innerText = await ReadSharedAsync(outPath).ConfigureAwait(false);
        var hostText = stdout + Environment.NewLine + stderr;
        _log.Info("包身份 " + appId + " 退出码 " + SafeExitCode(process));
        if (!string.IsNullOrWhiteSpace(hostText.Trim()))
            _log.Info("helper 输出：" + hostText.Trim());

        if (HelperHandshake.LooksLikeExecutionPolicyBlock(launcherText + hostText))
        {
            return new PackageCallResult(
                false,
                "本机执行策略阻止了临时脚本。本工具没有修改系统的执行策略。若策略由组织锁定，需要允许当前用户运行本地脚本后再试。",
                []);
        }

        var launcher = HelperHandshake.Parse(launcherText, nonce);
        var launcherError = launcher.Lines.FirstOrDefault(l => l.StartsWith("LAUNCHER_ERROR=", StringComparison.Ordinal));
        if (!launcher.SawEnd || launcherError != null || !launcher.NonceOk)
        {
            _log.Warn("启动脚本原文：" + launcherText);
            var detail = launcherError == null
                ? "进入包身份的启动脚本没有完成握手。"
                : launcherError["LAUNCHER_ERROR=".Length..];
            if (string.IsNullOrWhiteSpace(detail))
                detail = "无法进入包身份 " + appId + "。";
            return new PackageCallResult(false, detail, []);
        }

        var inner = HelperHandshake.Parse(innerText, nonce);
        foreach (var line in inner.Lines)
            _log.Info("  " + line);

        if (!inner.SawEnd || !inner.NonceOk)
        {
            _log.Warn("包内输出原文：" + innerText);
            return new PackageCallResult(false, "包内脚本没有完成握手（END=1 只表示结束，这里连结束标记都不可信）。", inner.Lines);
        }

        if (!string.IsNullOrWhiteSpace(inner.Exception))
            return new PackageCallResult(false, inner.Exception!, inner.Lines);
        return new PackageCallResult(true, "ok", inner.Lines);
        }
        finally
        {
            await WaitForInnerExitAsync(work.Path).ConfigureAwait(false);
            await work.ReleaseAsync().ConfigureAwait(false);
        }
    }

    private static async Task WaitForInnerExitAsync(string workDir)
    {
        var text = await ReadSharedAsync(Path.Combine(workDir, "out.txt")).ConfigureAwait(false);
        var pidLine = HelperHandshake.Parse(text, "ignored").Lines
            .FirstOrDefault(l => l.StartsWith("PID=", StringComparison.Ordinal));
        if (pidLine == null || !int.TryParse(pidLine["PID=".Length..], out var pid))
            return;

        try
        {
            using var inner = Process.GetProcessById(pid);
            if (!inner.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                return;
            await inner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static async Task StopKnownHelpersAsync(Process? launcherProcess, string innerText)
    {
        if (launcherProcess != null)
            await StopHelperAsync(launcherProcess).ConfigureAwait(false);

        var pidLine = HelperHandshake.Parse(innerText, "ignored").Lines
            .FirstOrDefault(l => l.StartsWith("PID=", StringComparison.Ordinal));
        if (pidLine == null || !int.TryParse(pidLine["PID=".Length..], out var pid))
            return;

        try
        {
            using var inner = Process.GetProcessById(pid);
            if (!inner.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                return;
            if ((DateTime.UtcNow - inner.StartTime.ToUniversalTime()).TotalMinutes > 10)
                return;
            inner.Kill(entireProcessTree: true);
            await inner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static string BuildLauncher(string appId, string scriptPath, string launcherOut, string nonce)
    {
        var script = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var launcher = launcherOut.Replace("'", "''", StringComparison.Ordinal);
        var safeNonce = nonce.Replace("'", "''", StringComparison.Ordinal);
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")
            .Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference = 'Continue'\r\n" +
            $"$launcherOut = '{launcher}'\r\n" +
            $"$nonce = '{safeNonce}'\r\n" +
            "function L($m){\r\n" +
            "  $utf8 = New-Object System.Text.UTF8Encoding $false\r\n" +
            "  [System.IO.File]::AppendAllText($launcherOut, ([string]$m) + [Environment]::NewLine, $utf8)\r\n" +
            "}\r\n" +
            "try {\r\n" +
            "  L ('NONCE=' + $nonce)\r\n" +
            "  Import-Module Appx -ErrorAction SilentlyContinue | Out-Null\r\n" +
            "  if (-not (Get-Command Invoke-CommandInDesktopPackage -ErrorAction SilentlyContinue)) {\r\n" +
            "    L 'LAUNCHER_ERROR=当前 Windows PowerShell 没有 Invoke-CommandInDesktopPackage。它通常由系统 Appx 模块提供，这不是管理员权限问题。'\r\n" +
            "    L 'END=1'\r\n" +
            "    exit 2\r\n" +
            "  }\r\n" +
            "  Invoke-CommandInDesktopPackage -PackageFamilyName '" + TaskCatalog.PackageFamilyName + "' -AppId '" + appId + "' -Command '" + powershell + "' -Args ('-NoProfile -ExecutionPolicy Bypass -File \"" + script.Replace("\"", "", StringComparison.Ordinal) + "\"')\r\n" +
            "  L 'LAUNCHER_OK=1'\r\n" +
            "  L 'END=1'\r\n" +
            "} catch {\r\n" +
            "  L ('LAUNCHER_ERROR=' + $_.Exception.Message)\r\n" +
            "  L 'END=1'\r\n" +
            "  exit 2\r\n" +
            "}\r\n";
    }

    private static async Task StopHelperAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        if (!File.Exists(path))
            return "";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static void AppendCapped(StringBuilder builder, string? line)
    {
        if (line == null)
            return;
        lock (builder)
        {
            if (builder.Length > 8000)
                return;
            builder.AppendLine(line);
        }
    }

    private static void CollectTasks(IReadOnlyList<string> lines, List<string> tasks)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("TASK=", StringComparison.Ordinal))
                tasks.Add(line["TASK=".Length..]);
        }
    }

    private static string FirstValue(IReadOnlyList<string> lines, string prefix)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..];
        }

        return "";
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString() : "未退出";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
