using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace HexoBlogStudio.Models;

/// <summary>
/// 图片工作区里的一张图。缩略图在后台解码，解好之前显示转圈，所以这里要
/// 属性通知：先占位，后填图，界面不用等。
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _loading;

    public ImageItem(string path, bool canPreview)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        CanPreview = canPreview;
        Placeholder = canPreview ? string.Empty : System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        _loading = canPreview;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string Name { get; }
    public bool CanPreview { get; }
    public string Placeholder { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            _loading = false;
            Raise(nameof(Thumbnail));
            Raise(nameof(LoadingVisibility));
            Raise(nameof(PlaceholderVisibility));
        }
    }

    /// <summary>解码失败时调用：停掉转圈，露出格式名。</summary>
    public void MarkFailed()
    {
        _loading = false;
        Raise(nameof(LoadingVisibility));
        Raise(nameof(PlaceholderVisibility));
    }

    public Visibility LoadingVisibility => _loading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlaceholderVisibility =>
        !_loading && _thumbnail is null && Placeholder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>正文里的引用写法，图片放在站点 source/images 下。</summary>
    public string Markdown(string imageKey) => $"![](/images/{imageKey}/{Name})";

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
