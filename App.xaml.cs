using System.Windows;
using System.Windows.Markup;
using HexoBlogStudio.Services;

namespace HexoBlogStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF 的 CJK 回退表（GlobalMonospace/GlobalUserInterface.CompositeFont）按元素的
        // Language 选字体：zh-Hans 对应 Microsoft YaHei，匹配不上则落到「Other」分支，
        // 而那一支的第一顺位是日文的 Meiryo。控件默认 Language 是 en-US，于是汉字会
        // 显示成日文字形。把框架级默认语言固定成中文，回退就落在中文字体上。
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("zh-CN")));

        // 无界面自检：HexoBlogStudio.exe --selftest <报告文件> [站点目录]
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            var reportPath = e.Args[1];
            var siteRoot = e.Args.Length >= 3 ? e.Args[2] : Settings.Load().SitePath;
            Shutdown(SelfTest.Run(siteRoot, reportPath));
            return;
        }

        // 启动本地预览、探测能否访问：--serve-test <报告文件> [站点目录]
        if (e.Args.Length >= 2 && e.Args[0] == "--serve-test")
        {
            var reportPath = e.Args[1];
            var siteRoot = e.Args.Length >= 3 ? e.Args[2] : Settings.Load().SitePath;
            Shutdown(RuntimeTest.RunServeTest(siteRoot, 4000, reportPath));
            return;
        }

        // 三条推送通道连通性（--dry-run，不改远端）：--push-test <报告文件> [站点目录]
        if (e.Args.Length >= 2 && e.Args[0] == "--push-test")
        {
            var reportPath = e.Args[1];
            var settings = Settings.Load();
            var siteRoot = e.Args.Length >= 3 ? e.Args[2] : settings.SitePath;
            // 必须在没有 UI 上下文的线程上跑：在 UI 线程同步等异步，续体会被派发回
            // 已被阻塞的 UI 线程，形成死锁（表现为一直卡在第一步）
            var exitCode = Task.Run(() => RuntimeTest.RunPushTestAsync(siteRoot, settings.ProxyUrl, reportPath))
                .GetAwaiter().GetResult();
            Shutdown(exitCode);
            return;
        }

        // 只跑发布前的同步（fetch + 快进）：--sync-test <报告文件> [站点目录]
        if (e.Args.Length >= 2 && e.Args[0] == "--sync-test")
        {
            var reportPath = e.Args[1];
            var settings = Settings.Load();
            var siteRoot = e.Args.Length >= 3 ? e.Args[2] : settings.SitePath;
            var code = Task.Run(() => RuntimeTest.RunSyncTestAsync(siteRoot, settings.ProxyUrl, reportPath))
                .GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
