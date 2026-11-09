using System.Text.RegularExpressions;

namespace DesktopSpotlightFix.Logging;

public static partial class Redactor
{
    [GeneratedRegex(@"asid=([0-9A-Fa-f]{8})[0-9A-Fa-f]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AsidRegex();

    public static string Apply(string message, bool redactIdentity)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var text = AsidRegex().Replace(message, "asid=$1…");
        if (redactIdentity)
        {
            var user = Environment.UserName;
            var machine = Environment.MachineName;
            if (!string.IsNullOrEmpty(user))
                text = ReplaceIgnoreCase(text, user, "<user>");
            if (!string.IsNullOrEmpty(machine))
                text = ReplaceIgnoreCase(text, machine, "<machine>");
        }

        if (text.Length > 2000)
            text = text[..2000] + "…";
        return text;
    }

    private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
    {
        if (oldValue.Length < 2)
            return text;
        return text.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public FileLogger(string? path, bool redactIdentity)
    {
        RedactIdentity = redactIdentity;
        FilePath = string.IsNullOrWhiteSpace(path) ? AppPaths.DefaultLogFile() : path;
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        Info("---- 启动 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  v"
            + (typeof(FileLogger).Assembly.GetName().Version?.ToString() ?? "?")
            + (redactIdentity ? "  已脱敏机器名和用户名" : "") + " ----");
    }

    public string FilePath { get; }
    public bool RedactIdentity { get; }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + " " + level + " " + Redactor.Apply(message, RedactIdentity);
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // 日志写失败不能中断修复流程。
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _writer.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
