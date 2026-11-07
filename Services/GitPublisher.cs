using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace HexoBlogStudio.Services;

public enum PublishOutcome
{
    Pushed,
    Failed,
}

public sealed record PublishResult(PublishOutcome Outcome, string Message);

/// <summary>
/// 一条连接通道：同样的操作换一条网络路径再试。
/// 注意这里只描述「怎么连」，具体是 push 还是 fetch 由调用方给。
/// </summary>
public sealed record GitChannel(string Label, string RemoteArgument, string ConfigArguments, bool NeedsConnectTool);

/// <summary>
/// 发布流程：确认分支 → 与远端对齐 → 暂存 → 提交 → 依次尝试三条通道推送。
///
/// 通道顺序沿用手工脚本的结论：22 端口直连最快，被挡时走 ssh.github.com:443，
/// 最后兜底 HTTPS 加代理。本站实际网络下三条都能连，所以顺序只影响快慢。
///
/// 与旧脚本的两处不同：
/// 1. 推送前先看远端有没有新提交。CI 会把主题同步提交推到 main，本地落后时直接
///    推送会被拒（non-fast-forward），所以先快进合并，本地有独立提交则明确报错。
/// 2. 所有 git 调用都禁掉交互（GIT_TERMINAL_PROMPT=0、SSH BatchMode），
///    没有终端可读时等待输入会一直挂着，界面上表现为「发布中…」永远不动。
/// </summary>
public static class GitPublisher
{
    public const string Branch = "main";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);

    private static readonly Regex CsiEscape = new(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b[()][A-Za-z0-9]", RegexOptions.Compiled);
    private static readonly Regex RemotePattern = new(
        @"^(?:git@github\.com:|https://github\.com/|ssh://git@(?:ssh\.)?github\.com(?::\d+)?/)(?<slug>[^/]+/[^/]+?)(?:\.git)?/?$",
        RegexOptions.Compiled);

    /// <summary>提交说明只留可打印内容：编辑器复制来的转义序列、控制字符会把 git 弄乱。</summary>
    public static string SanitizeCommitMessage(string raw)
    {
        var text = CsiEscape.Replace(raw, string.Empty);
        var cleaned = new string(text.Where(c => !char.IsControl(c) || c == '\n').ToArray());
        return cleaned.Trim();
    }

    public static bool TryParseSlug(string originUrl, out string slug)
    {
        var match = RemotePattern.Match(originUrl.Trim());
        slug = match.Success ? match.Groups["slug"].Value : string.Empty;
        return match.Success;
    }

    public static IReadOnlyList<GitChannel> BuildChannels(string slug, string proxyUrl)
    {
        var hostPort = HostPort(proxyUrl);
        return
        [
            new("通道 1/3：github.com:22", "origin", "", false),
            new(
                "通道 2/3：ssh.github.com:443 经代理",
                $"ssh://git@ssh.github.com:443/{slug}.git",
                $"-c \"core.sshCommand=ssh -o BatchMode=yes -o ProxyCommand='connect -H {hostPort} %h %p'\"",
                true),
            new(
                "通道 3/3：HTTPS 经代理",
                $"https://github.com/{slug}.git",
                $"-c http.proxy={proxyUrl} -c https.proxy={proxyUrl}",
                false),
        ];
    }

    /// <summary>connect.exe 跟着 Git for Windows 装在 mingw64/bin，cmd 的 PATH 里通常没有。</summary>
    public static string? FindGitMingwBin()
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "--exec-path")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var execPath = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (execPath.Length == 0) return null;

            var bin = Path.Combine(Path.GetFullPath(Path.Combine(execPath, "..", "..")), "bin");
            return Directory.Exists(bin) ? bin : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>通道 1 也要禁掉交互：没有终端时 ssh 的密码/口令提问不会返回。</summary>
    private static string ConfigFor(GitChannel channel) =>
        channel.Label.StartsWith("通道 1") ? "-c \"core.sshCommand=ssh -o BatchMode=yes\"" : channel.ConfigArguments;

    private static IReadOnlyDictionary<string, string> EnvironmentFor(GitChannel channel, string? connectBin)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
        };
        if (channel.NeedsConnectTool && connectBin is not null)
            env["PATH"] = connectBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return env;
    }

    public static async Task<int> RunGitAsync(
        string siteRoot, GitChannel channel, string? connectBin, string arguments, Action<string> onLine)
    {
        using var timeout = new CancellationTokenSource(NetworkTimeout);
        try
        {
            return await ProcessRunner.RunAsync(
                "git", arguments, siteRoot, onLine, EnvironmentFor(channel, connectBin), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            onLine($"  {channel.Label} 超时（{NetworkTimeout.TotalSeconds:F0}s 没有响应）");
            return -1;
        }
    }

    /// <summary>给自检用：拼出一条完整的 git 命令，保证测的就是发布时真正会跑的写法。</summary>
    public static string BuildCommand(GitChannel channel, string verb, string refspec)
    {
        var head = ConfigFor(channel);
        var command = $"{verb} {channel.RemoteArgument} {refspec}";
        return head.Length == 0 ? command : head + " " + command;
    }

    /// <summary>给自检用：与发布时一致的环境（禁交互、补 connect 所在目录）。</summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(GitChannel channel) =>
        EnvironmentFor(channel, FindGitMingwBin());

    private static string WithConfig(GitChannel channel, string command)
    {
        var config = ConfigFor(channel);
        return config.Length == 0 ? command : config + " " + command;
    }

    public static async Task<PublishResult> PublishAsync(
        string siteRoot,
        string? commitMessage,
        string proxyUrl,
        Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        var (branchCode, branch) = await ProcessRunner.CaptureAsync("git", "rev-parse --abbrev-ref HEAD", siteRoot, cancellationToken);
        if (branchCode != 0) return new PublishResult(PublishOutcome.Failed, "读不到当前分支，站点目录是不是 Git 仓库？");
        if (branch.Trim() != Branch)
            return new PublishResult(PublishOutcome.Failed, $"当前在 {branch.Trim()} 分支，发布只在 {Branch} 上进行。");

        var (originCode, originUrl) = await ProcessRunner.CaptureAsync("git", "remote get-url origin", siteRoot, cancellationToken);
        if (originCode != 0 || !TryParseSlug(originUrl, out var slug))
            return new PublishResult(PublishOutcome.Failed, "读不到 origin 地址，发布通道拼不出来。");

        var channels = BuildChannels(slug, proxyUrl);
        var connectBin = FindGitMingwBin();

        // 1. 先和远端对齐：CI 会往 main 推主题同步提交，落后时直接推会被拒
        var sync = await AlignWithRemoteAsync(siteRoot, channels, connectBin, onLine);
        if (sync is not null) return sync;

        // 2. 暂存并提交
        var addCode = await ProcessRunner.RunAsync("git", "add --all", siteRoot, onLine, null, cancellationToken);
        if (addCode != 0) return new PublishResult(PublishOutcome.Failed, "暂存改动失败。");

        var (statusCode, status) = await ProcessRunner.CaptureAsync("git", "status --porcelain", siteRoot, cancellationToken);
        if (statusCode != 0) return new PublishResult(PublishOutcome.Failed, "取不到改动列表。");

        if (status.Trim().Length > 0)
        {
            var message = SanitizeCommitMessage(commitMessage ?? string.Empty);
            if (message.Length == 0)
                return new PublishResult(PublishOutcome.Failed, "有改动待提交，但提交说明是空的。");
            onLine($"[提交] {message}");
            var commitCode = await ProcessRunner.RunAsync(
                "git", $"commit -m \"{message.Replace("\"", "'")}\"", siteRoot, onLine, null, cancellationToken);
            if (commitCode != 0) return new PublishResult(PublishOutcome.Failed, "提交失败，看日志里的 git 输出。");
        }
        else
        {
            onLine("[提交] 没有新的改动，直接推送已有提交。");
        }

        // 3. 依次尝试三条通道
        foreach (var channel in channels)
        {
            onLine($"[推送] {channel.Label}");
            var code = await RunGitAsync(siteRoot, channel, connectBin, WithConfig(channel, $"push {channel.RemoteArgument} {Branch}:{Branch}"), onLine);
            if (code == 0) return new PublishResult(PublishOutcome.Pushed, $"{channel.Label} 推送成功。");
            onLine($"  这条通道没成（退出码 {code}），换下一条。");
        }

        return new PublishResult(PublishOutcome.Failed, "三条通道都没推上去，检查网络与代理设置。");
    }

    /// <summary>远端比本地新时快进合并；本地有独立提交时不做任何改动并报错。返回 null 表示可以继续。</summary>
    public static async Task<PublishResult?> AlignWithRemoteAsync(
        string siteRoot, IReadOnlyList<GitChannel> channels, string? connectBin, Action<string> onLine)
    {
        var (headCode, localHead) = await ProcessRunner.CaptureAsync("git", "rev-parse HEAD", siteRoot);
        if (headCode != 0) return new PublishResult(PublishOutcome.Failed, "读不到本地提交。");

        foreach (var channel in channels)
        {
            var code = await RunGitAsync(siteRoot, channel, connectBin,
                WithConfig(channel, $"fetch {channel.RemoteArgument} {Branch}"), onLine);
            if (code != 0) continue;

            var (shaCode, remoteHead) = await ProcessRunner.CaptureAsync("git", "rev-parse FETCH_HEAD", siteRoot);
            if (shaCode != 0) continue;

            if (remoteHead.Trim() == localHead.Trim())
            {
                onLine("[同步] 本地与远端一致。");
                return null;
            }

            var (ancestorCode, _) = await ProcessRunner.CaptureAsync("git", $"merge-base --is-ancestor {remoteHead.Trim()} HEAD", siteRoot);
            if (ancestorCode == 0)
            {
                onLine("[同步] 本地比远端新，直接推送。");
                return null;
            }

            onLine("[同步] 远端有本地没有的提交，先快进合并。");
            var mergeCode = await ProcessRunner.RunAsync("git", "merge --ff-only FETCH_HEAD", siteRoot, onLine);
            if (mergeCode == 0)
            {
                onLine("[同步] 已快进到远端最新。");
                return null;
            }

            return new PublishResult(PublishOutcome.Failed,
                "远端有新提交，本地也有自己的提交，自动快进不了。先在站点目录里手动合并（git pull --rebase），再发布。");
        }

        return new PublishResult(PublishOutcome.Failed, "三条通道都连不上远端，没法确认是否落后。检查网络与代理。");
    }

    private static string HostPort(string proxyUrl)
    {
        var uri = new Uri(proxyUrl);
        return $"{uri.Host}:{uri.Port}";
    }
}
