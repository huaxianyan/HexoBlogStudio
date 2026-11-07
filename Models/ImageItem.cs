using System.Windows.Media;

namespace HexoBlogStudio.Models;

/// <summary>图片工作区里的一张图。Thumbnail 为 null 表示当前格式不能在应用内显示。</summary>
public sealed class ImageItem(string path, ImageSource? thumbnail)
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public ImageSource? Thumbnail { get; } = thumbnail;

    /// <summary>缩略图位置显示的说明，只有不能预览的格式才有。</summary>
    public string Placeholder { get; } = thumbnail is null
        ? System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
        : string.Empty;

    /// <summary>正文里的引用写法，图片放在站点 source/images 下。</summary>
    public string Markdown(string imageKey) => $"![](/images/{imageKey}/{Name})";
}
