using System.IO;
using System.Text.Json;

namespace HexoBlogStudio.Services;

/// <summary>用户设置（站点目录、代理地址），存在 %APPDATA%\HexoBlogStudio\settings.json。</summary>
public sealed class Settings
{
    public string SitePath { get; set; } = string.Empty;
    public string ProxyUrl { get; set; } = "http://127.0.0.1:37777";

    private const string DefaultProxy = "http://127.0.0.1:37777";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HexoBlogStudio", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.ProxyUrl)) loaded.ProxyUrl = DefaultProxy;
                    return loaded;
                }
            }
        }
        catch
        {
            // 设置文件损坏时按默认值重新开始，不拦住启动
        }

        return new Settings { SitePath = DetectSitePath(), ProxyUrl = DefaultProxy };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>站点目录的常见位置。找不到就留空，由用户在选择框里指定。</summary>
    public static string DetectSitePath()
    {
        var candidates = new[]
        {
            @"C:\Neko7ina\Syncthing\Hexo\Hexo",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Syncthing", "Hexo", "Hexo"),
        };
        return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "_config.yml"))) ?? string.Empty;
    }

    public static bool LooksLikeSite(string path) =>
        Directory.Exists(Path.Combine(path, "source", "_posts")) || File.Exists(Path.Combine(path, "_config.yml"));
}
