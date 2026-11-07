using System.Diagnostics;
using System.IO;

namespace HexoBlogStudio.Services;

/// <summary>
/// 本地预览用的 hexo server。直接调 node 跑 hexo 自己的入口，
/// 避免经过 hexo.cmd 再套一层 cmd.exe。
/// </summary>
public sealed class HexoServer : IDisposable
{
    private Process? _process;

    public int Port { get; set; } = 4000;

    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? Output;

    /// <summary>进程退出时触发（不管是手动停还是自己崩了），界面据此复原按钮。</summary>
    public event Action? Exited;

    public void Start(string siteRoot)
    {
        if (IsRunning) return;

        var hexoEntry = Path.Combine(siteRoot, "node_modules", "hexo", "bin", "hexo");
        if (!File.Exists(hexoEntry))
            throw new FileNotFoundException($"没找到 Hexo：{hexoEntry}，站点目录可能不对，或在里面执行过 pnpm install / npm install 吗？");

        var startInfo = new ProcessStartInfo("node", $"\"{hexoEntry}\" server -p {Port}")
        {
            WorkingDirectory = siteRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(ProcessRunner.StripAnsi(e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(ProcessRunner.StripAnsi(e.Data)); };
        process.Exited += (_, _) =>
        {
            Output?.Invoke($"本地预览已退出（退出码 {SafeExitCode(process)}）");
            Exited?.Invoke();
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    public void Stop()
    {
        if (_process is null) return;
        ProcessRunner.TryKill(_process);
        _process.Dispose();
        _process = null;
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); }
        catch { return "未知"; }
    }

    public void Dispose() => Stop();
}
