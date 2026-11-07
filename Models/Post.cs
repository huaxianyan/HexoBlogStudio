using System.IO;

namespace HexoBlogStudio.Models;

/// <summary>一篇已发布的文章（source/_posts 下的 .md）。</summary>
public sealed class Post
{
    public required string FilePath { get; init; }
    public required string Title { get; init; }
    public DateTime? Date { get; init; }

    /// <summary>配图目录名（source/images 下的子目录），没有配图时为 null。</summary>
    public string? ImageKey { get; init; }

    /// <summary>配图目录的绝对路径，没有配图时为 null。</summary>
    public string? ImageDir { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string DateText => Date?.ToString("yyyy-MM-dd") ?? "未知日期";

    public string ListLabel => $"{DateText}  {Title}";

    /// <summary>列表项的无障碍名就是这个：不重写的话读屏器和自动化会读到类型名。</summary>
    public override string ToString() => ListLabel;

    /// <summary>列表搜索用：标题、标签、文件名都能匹配上。</summary>
    public bool Matches(string keyword) =>
        Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
        || Path.GetFileNameWithoutExtension(FilePath).Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
        || Tags.Any(t => t.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
}
