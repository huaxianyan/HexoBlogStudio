using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Text;

namespace HexoBlogStudio.Services;

/// <summary>
/// 需要外部进程的自检：本地预览能不能起来、三条推送通道通不通。
/// 推送一律用 --dry-run，只探测连通性，不改远端。
/// </summary>
public static class RuntimeTest
{
    public static int RunServeTest(string siteRoot, int port, string reportPath)
    {
        var lines = new List<string>();
        var ok = false;

        using var server = new HexoServer { Port = port };
        var output = new StringBuilder();
        server.Output += line => output.AppendLine(line);

        // 端口被占时先看清楚是谁：本机常有上次没关掉的实例
        if (ProcessRunner.PortInUse(port))
        {
            lines.Add($"[NG] 端口 {port} 已被占用，测不了启动流程。先停掉占用者。");
            return Write(lines, reportPath);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            server.Start(siteRoot);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var response = client.GetAsync($"http://localhost:{port}/").GetAwaiter().GetResult();
                    if ((int)response.StatusCode == 200)
                    {
                        ok = true;
                        break;
                    }
                }
                catch
                {
                    // 还没起来，继续等
                }
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[NG] 启动抛异常 — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            server.Stop();
        }

        stopwatch.Stop();
        if (ok)
            lines.Add($"[OK] 本地预览可访问 — http://localhost:{port} 用时 {stopwatch.Elapsed.TotalSeconds:F1}s");
        else
            lines.Add($"[NG] 本地预览没起来 — 等了 {stopwatch.Elapsed.TotalSeconds:F1}s");

        if (!ok)
        {
            lines.Add("--- Hexo 输出（末尾 20 行）---");
            lines.AddRange(output.ToString().Split('\n').TakeLast(20));
        }
        return Write(lines, reportPath);
    }

    public static async Task<int> RunPushTestAsync(string siteRoot, string proxyUrl, string reportPath)
    {
        var lines = new List<string>();
        // 每步立刻落盘：卡住时能看到卡在哪条通道
        void Note(string line)
        {
            lines.Add(line);
            File.AppendAllText(reportPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        File.WriteAllText(reportPath, "[步骤] 开始" + Environment.NewLine, new UTF8Encoding(false));

        try
        {
            return await RunPushStepsAsync(siteRoot, proxyUrl, Note, lines);
        }
        catch (Exception ex)
        {
            Note($"[NG] 异常 — {ex.GetType().Name}: {ex.Message}");
            Note(ex.StackTrace ?? string.Empty);
            return 1;
        }
    }

    private static async Task<int> RunPushStepsAsync(
        string siteRoot, string proxyUrl, Action<string> Note, List<string> lines)
    {
        var failures = 0;

        Note("[步骤] 读取 origin 地址");
        var (code, originUrl) = await ProcessRunner.CaptureAsync("git", "remote get-url origin", siteRoot);
        Note($"[步骤] origin = {originUrl.Trim()}（退出码 {code}）");
        if (code != 0 || !GitPublisher.TryParseSlug(originUrl, out var slug))
        {
            Note("[NG] 读不到 origin 地址");
            return 1;
        }

        Note("[步骤] 查找 connect.exe");
        var connectBin = GitPublisher.FindGitMingwBin();
        Note($"[步骤] connect 目录 = {connectBin ?? "未找到"}");
        if (connectBin is null)
            Note("[NG] 找不到 Git 自带的 connect.exe，443 通道会用不了");

        foreach (var channel in GitPublisher.BuildChannels(slug, proxyUrl))
        {
            Note($"[测试] {channel.Label} …");
            var stopwatch = Stopwatch.StartNew();
            var command = GitPublisher.BuildCommand(channel, "push", "main:main") + " --dry-run";
            Note($"[命令] git {command}");
            var output = new System.Text.StringBuilder();
            var exitCode = await ProcessRunner.RunAsync(
                "git", command, siteRoot, line => output.AppendLine(line), GitPublisher.BuildEnvironment(channel));
            stopwatch.Stop();
            var rejected = output.ToString().Contains("non-fast-forward")
                || output.ToString().Contains("rejected")
                || output.ToString().Contains("fetch first");

            if (exitCode == 0)
                Note($"[OK] {channel.Label} — 连通，{stopwatch.Elapsed.TotalSeconds:F1}s");
            else if (rejected)
                Note($"[OK] {channel.Label} — 连通，远端有新提交所以推送被拒（发布时会先同步再推），{stopwatch.Elapsed.TotalSeconds:F1}s");
            else
            {
                failures++;
                Note($"[NG] {channel.Label} — 退出码 {exitCode}");
                var tail = output.ToString().Split(Environment.NewLine.ToCharArray()).TakeLast(6);
                foreach (var line in tail) Note($"  | {line.TrimEnd()}");
            }
        }

        Note(failures == 0 ? "结论：三条通道都通" : $"结论：{failures} 条不通（发布时会按顺序换通道，只要有一条通就够）");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>只跑「和远端对齐」这一步：fetch + 快进合并，用来验证发布前的同步逻辑。</summary>
    public static async Task<int> RunSyncTestAsync(string siteRoot, string proxyUrl, string reportPath)
    {
        var lines = new List<string>();
        void Note(string line)
        {
            lines.Add(line);
            File.AppendAllText(reportPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        File.WriteAllText(reportPath, string.Empty, new UTF8Encoding(false));

        try
        {
            var (code, originUrl) = await ProcessRunner.CaptureAsync("git", "remote get-url origin", siteRoot);
            if (code != 0 || !GitPublisher.TryParseSlug(originUrl, out var slug))
            {
                Note("[NG] 读不到 origin 地址");
                return 1;
            }

            var channels = GitPublisher.BuildChannels(slug, proxyUrl);
            var result = await GitPublisher.AlignWithRemoteAsync(siteRoot, channels, GitPublisher.FindGitMingwBin(), Note);
            if (result is null)
            {
                Note("[OK] 同步完成，可以发布。");
                return 0;
            }
            Note($"[NG] {result.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Note($"[NG] 异常 — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Write(List<string> lines, string reportPath)
    {
        File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
        return lines.Any(l => l.StartsWith("[NG]")) ? 1 : 0;
    }
}
