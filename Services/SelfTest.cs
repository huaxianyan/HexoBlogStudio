using System.IO;
using System.Text;

namespace HexoBlogStudio.Services;

/// <summary>
/// 无界面自检：dotnet HexoBlogStudio.dll --selftest &lt;报告文件&gt;
/// 用真实站点数据和纯逻辑跑一遍，退出码 0 表示全部通过。
/// </summary>
public static class SelfTest
{
    public static int Run(string siteRoot, string reportPath)
    {
        var lines = new List<string>();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            lines.Add($"{(ok ? "[OK]" : "[NG]")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        try
        {
            var posts = PostLibrary.Scan(siteRoot);
            Check("扫描文章", posts.Count > 0, $"{posts.Count} 篇");
            Check("标题已解析", posts.All(p => p.Title.Length > 0));
            Check("日期已解析", posts.Count(p => p.Date is not null) == posts.Count,
                $"{posts.Count(p => p.Date is not null)}/{posts.Count}");

            var variant = posts.FirstOrDefault(p => Path.GetFileName(p.FilePath).StartsWith("20251228.01"));
            Check("同日后缀文章对上配图", variant?.ImageKey == "20251228.01", variant?.ImageKey ?? "没找到 20251228.01");

            var withImages = posts.Count(p => p.ImageDir is not null);
            Check("配图目录识别", withImages > 0, $"{withImages}/{posts.Count} 篇有配图目录");

            var sample = posts.First(p => p.ImageDir is not null);
            var images = PostLibrary.ListImages(sample.ImageDir);
            Check("配图文件列表", images.Count > 0, $"{sample.ImageKey} 下 {images.Count} 个文件");

            var firstFile = File.ReadAllText(posts[0].FilePath);
            var front = FrontMatter.Parse(firstFile);
            Check("front-matter 解析", front.Title is not null && front.Tags.Count > 0,
                $"标题={front.Title}，标签={string.Join("/", front.Tags)}");

            Check("front-matter 处理行内列表",
                FrontMatter.Parse("---\ntitle: t\ndate: 2020-01-01 10:00:00\ntags: [甲, 乙]\n---\n").Tags.Count == 2);

            Check("配图目录非法名被拒", PostLibrary.ResolveImageKey("..", null, "", Path.Combine(siteRoot, "source", "images")) is null);

            Check("提交说明去掉转义序列",
                GitPublisher.SanitizeCommitMessage("\u001b[33m修复标题\u001b[0m\n") == "修复标题",
                GitPublisher.SanitizeCommitMessage("\u001b[33m修复标题\u001b[0m\n"));

            var slugOk = GitPublisher.TryParseSlug("git@github.com:huaxianyan/Hexo.git", out var slug);
            Check("解析 origin 地址", slugOk && slug == "huaxianyan/Hexo", slug);

            var channels = GitPublisher.BuildChannels(slug, "http://127.0.0.1:37777");
            Check("三条推送通道", channels.Count == 3);
            Check("通道 1 走 origin", channels[0].RemoteArgument == "origin", channels[0].RemoteArgument);
            Check("通道 1 禁掉交互", GitPublisher.BuildCommand(channels[0], "push", "main:main").Contains("BatchMode=yes"));
            Check("通道 2 走 443", channels[1].RemoteArgument.Contains("ssh.github.com:443"), channels[1].Label);
            Check("通道 2 带代理命令行", channels[1].ConfigArguments.Contains("ProxyCommand='connect -H 127.0.0.1:37777 %h %p'"));
            Check("通道 3 走 HTTPS", channels[2].RemoteArgument.Contains("https://github.com/huaxianyan/Hexo.git"), channels[2].Label);
            Check("所有通道禁掉凭据提问",
                channels.All(c => GitPublisher.BuildEnvironment(c)["GIT_TERMINAL_PROMPT"] == "0"));

            var mingw = GitPublisher.FindGitMingwBin();
            Check("找到 connect.exe 所在目录", mingw is not null && File.Exists(Path.Combine(mingw, "connect.exe")), mingw ?? "未找到");

            var hexoEntry = Path.Combine(siteRoot, "node_modules", "hexo", "bin", "hexo");
            Check("找到 Hexo 入口", File.Exists(hexoEntry), hexoEntry);
        }
        catch (Exception ex)
        {
            failures++;
            lines.Add($"[NG] 自检抛异常 — {ex.GetType().Name}: {ex.Message}");
        }

        lines.Add(failures == 0 ? "结论：全部通过" : $"结论：{failures} 项未通过");
        File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }
}
