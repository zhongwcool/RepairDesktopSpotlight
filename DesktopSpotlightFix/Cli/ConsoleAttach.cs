using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopSpotlightFix.Cli;

public static class ConsoleAttach
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint FileTypeDisk = 1;
    private const uint FileTypeChar = 2;
    private const uint FileTypePipe = 3;

    public static void PrepareForCli()
    {
        var stdout = GetStdHandle(StdOutputHandle);
        var type = IsInvalid(stdout) ? 0 : GetFileType(stdout);
        if (type == 0)
        {
            if (!AttachConsole(AttachParentProcess))
                return;
            SetConsoleOutputCP(65001);
            SetConsoleCP(65001);
            stdout = GetStdHandle(StdOutputHandle);
            type = IsInvalid(stdout) ? 0 : GetFileType(stdout);
        }
        else if (type == FileTypeChar)
        {
            SetConsoleOutputCP(65001);
            SetConsoleCP(65001);
        }

        if (type is FileTypeDisk or FileTypePipe or FileTypeChar)
            Rebind(stdout, GetStdHandle(StdErrorHandle));
    }

    private static void Rebind(IntPtr stdout, IntPtr stderr)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!IsInvalid(stdout))
        {
            var stream = new FileStream(new SafeFileHandle(stdout, ownsHandle: false), FileAccess.Write);
            Console.SetOut(new StreamWriter(stream, utf8) { AutoFlush = true });
        }

        if (!IsInvalid(stderr))
        {
            var stream = new FileStream(new SafeFileHandle(stderr, ownsHandle: false), FileAccess.Write);
            Console.SetError(new StreamWriter(stream, utf8) { AutoFlush = true });
        }

        Console.OutputEncoding = utf8;
    }

    private static bool IsInvalid(IntPtr handle) =>
        handle == IntPtr.Zero || handle == new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageId);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageId);
}
