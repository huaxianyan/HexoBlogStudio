using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HexoBlogStudio.Services;

/// <summary>启动外部命令并把输出逐行回调出去，供日志面板显示。</summary>
public static class ProcessRunner
{
    /// <summary>终端颜色转义序列，写进日志面板之前先去掉。</summary>
    private static readonly Regex AnsiEscape = new(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b[()][A-Za-z0-9]", RegexOptions.Compiled);

    public static string StripAnsi(string line) => AnsiEscape.Replace(line, string.Empty);

    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string> onLine,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(StripAnsi(e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(StripAnsi(e.Data)); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        return process.ExitCode;
    }

    public static async Task<(int ExitCode, string Output)> CaptureAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var buffer = new StringBuilder();
        var code = await RunAsync(fileName, arguments, workingDirectory, line => buffer.AppendLine(line), null, cancellationToken);
        return (code, buffer.ToString());
    }

    public static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* 进程已经退出，忽略 */ }
    }

    public static bool PortInUse(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);
}
