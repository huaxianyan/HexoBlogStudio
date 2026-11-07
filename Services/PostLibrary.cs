using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HexoBlogStudio.Models;

namespace HexoBlogStudio.Services;

/// <summary>扫描 source/_posts 并把文章与 source/images 下的配图目录对上。</summary>
public static class PostLibrary
{
    /// <summary>文章文件名的日期前缀，允许 20251228.01 这种同一天多篇的后缀。</summary>
    private static readonly Regex DatePrefix = new(@"^(\d{8}(?:\.\d{1,2})?)", RegexOptions.Compiled);

    /// <summary>正文里引用的图片路径，取第一段目录名。</summary>
    private static readonly Regex BodyImageDir = new(@"/images/([^/""')\s]+)/", RegexOptions.Compiled);

    /// <summary>能否在应用内显示缩略图；其余格式（webp、avif）交给系统看图软件。</summary>
    private static readonly HashSet<string> PreviewableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff" };

    public static IReadOnlyList<Post> Scan(string siteRoot)
    {
        var postsDir = Path.Combine(siteRoot, "source", "_posts");
        if (!Directory.Exists(postsDir))
            throw new DirectoryNotFoundException($"找不到文章目录：{postsDir}");

        var imagesRoot = Path.Combine(siteRoot, "source", "images");
        var posts = new List<Post>();

        foreach (var file in Directory.EnumerateFiles(postsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var text = ReadText(file);
            var front = FrontMatter.Parse(text);
            var name = Path.GetFileNameWithoutExtension(file);
            var key = ResolveImageKey(name, front.Date, text, imagesRoot);

            posts.Add(new Post
            {
                FilePath = file,
                Title = string.IsNullOrWhiteSpace(front.Title) ? name : front.Title!,
                Date = ParseDate(front.Date) ?? ParseDateFromName(name),
                ImageKey = key,
                ImageDir = key is null ? null : Path.Combine(imagesRoot, key),
                Tags = front.Tags,
            });
        }

        return posts
            .OrderByDescending(p => p.Date ?? DateTime.MinValue)
            .ThenByDescending(p => p.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>配图目录的判定顺序：文件名日期 → 正文里引用最多的目录 → front-matter 日期。</summary>
    public static string? ResolveImageKey(string nameWithoutExtension, string? frontMatterDate, string body, string imagesRoot)
    {
        var byName = DatePrefix.Match(nameWithoutExtension);
        if (byName.Success && DirectoryExists(imagesRoot, byName.Groups[1].Value))
            return byName.Groups[1].Value;

        var referenced = BodyImageDir.Matches(body)
            .Select(m => m.Groups[1].Value)
            .Where(k => DirectoryExists(imagesRoot, k))
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (referenced is not null) return referenced.Key;

        var byFrontMatter = ParseDate(frontMatterDate);
        if (byFrontMatter is not null)
        {
            var key = byFrontMatter.Value.ToString("yyyyMMdd");
            if (DirectoryExists(imagesRoot, key)) return key;
        }
        return null;
    }

    /// <summary>配图目录里的文件，不含 proc/ 占位图目录。</summary>
    public static IReadOnlyList<ImageFile> ListImages(string? imageDir)
    {
        if (imageDir is null || !Directory.Exists(imageDir)) return [];
        return Directory.EnumerateFiles(imageDir, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new ImageFile(path, PreviewableExtensions.Contains(Path.GetExtension(path))))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static DateTime? ParseDateFromName(string nameWithoutExtension)
    {
        var m = DatePrefix.Match(nameWithoutExtension);
        if (!m.Success) return null;
        var digits = m.Groups[1].Value.Split('.')[0];
        return DateTime.TryParseExact(digits, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    private static bool DirectoryExists(string imagesRoot, string key)
    {
        if (key.Length == 0 || key.Contains("..") || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return Directory.Exists(Path.Combine(imagesRoot, key));
    }

    private static string ReadText(string path)
    {
        // 文章可能被老编辑器写成 UTF-8 with BOM 或 GBK，先按 UTF-8 读，失败再回落系统编码
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
    }
}

/// <summary>配图目录里的一张图片。</summary>
public sealed record ImageFile(string Path, bool CanPreview)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
