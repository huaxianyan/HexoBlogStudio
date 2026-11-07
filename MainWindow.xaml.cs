using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using HexoBlogStudio.Models;
using HexoBlogStudio.Services;
using Wpf.Ui.Controls;
// WPF-UI 也导出同名的 MessageBox 系列，这里固定用系统自带的
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MenuItem = System.Windows.Controls.MenuItem;

namespace HexoBlogStudio;

public partial class MainWindow : FluentWindow
{
    private readonly Settings _settings;
    private readonly HexoServer _server = new();
    private readonly List<Post> _allPosts = [];
    private List<Post> _visiblePosts = [];

    private Post? _current;
    private string _savedText = string.Empty;
    private string _newline = "\r\n";
    private bool _loadingEditor;
    private bool _busy;
    private CancellationTokenSource? _imageLoadCts;

    private const int MaxLogLines = 3000;
    private const int ThumbnailParallelism = 3;

    public MainWindow()
    {
        InitializeComponent();
        ClampToScreen();
        _settings = Settings.Load();
        _server.Output += AppendLog;
        _server.Exited += OnServerExited;
        _server.Port = 4000;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private bool IsDirty => _current is not null && !_loadingEditor && Editor.Text != _savedText;

    /// <summary>默认尺寸按屏幕可见区域收一收，避免小屏上右边一栏被切掉。</summary>
    private void ClampToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(area.Width - 40, MinWidth));
        Height = Math.Min(Height, Math.Max(area.Height - 60, MinHeight));
    }

    // ---------- 启动 ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdatePreviewUi();
        AppendLog("工作台已启动。");
        UpdateSitePath();
        if (!Settings.LooksLikeSite(_settings.SitePath))
        {
            AppendLog("还没有设置站点目录，选一个含有 source/_posts 的目录。");
            SetStatus("先选择站点目录");
            if (!PromptSiteFolder()) return;
        }
        await ReloadPostsAsync();
    }

    private void UpdateSitePath()
    {
        SitePathText.Text = string.IsNullOrEmpty(_settings.SitePath) ? "未设置站点目录" : _settings.SitePath;
        Title = string.IsNullOrEmpty(_settings.SitePath) ? "Hexo 博客工作台" : $"Hexo 博客工作台 — {_settings.SitePath}";
    }

    private bool PromptSiteFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Hexo 站点目录（里面有 source/_posts）" };
        if (dialog.ShowDialog() != true) return false;
        if (!Settings.LooksLikeSite(dialog.FolderName))
        {
            MessageBox.Show(this, "这个目录里没有 source/_posts，应该选站点根目录。", "目录不对",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _settings.SitePath = dialog.FolderName;
        _settings.Save();
        UpdateSitePath();
        return true;
    }

    private void OnChooseSiteClick(object sender, RoutedEventArgs e)
    {
        if (IsDirty && !ConfirmDiscard()) return;
        if (PromptSiteFolder()) _ = ReloadPostsAsync();
    }

    private async Task ReloadPostsAsync()
    {
        try
        {
            _allPosts.Clear();
            _allPosts.AddRange(PostLibrary.Scan(_settings.SitePath));
            ApplyFilter();
            AppendLog($"读到 {_allPosts.Count} 篇文章，其中 {_allPosts.Count(p => p.ImageDir is not null)} 篇有配图目录。");
            SetStatus($"共 {_allPosts.Count} 篇文章");
        }
        catch (Exception ex)
        {
            AppendLog($"读取文章失败：{ex.Message}");
            SetStatus("读取文章失败");
        }
        await Task.CompletedTask;
    }

    // ---------- 文章列表 ----------

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        _visiblePosts = keyword.Length == 0
            ? [.. _allPosts]
            : _allPosts.Where(p => p.Matches(keyword)).ToList();

        var previous = _current;
        PostList.ItemsSource = _visiblePosts;
        PostCountText.Text = keyword.Length == 0
            ? $"共 {_allPosts.Count} 篇"
            : $"筛出 {_visiblePosts.Count} / {_allPosts.Count} 篇";

        if (previous is not null && _visiblePosts.Contains(previous))
            PostList.SelectedItem = previous;
        else if (_visiblePosts.Count > 0 && _current is null)
            PostList.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnPostSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PostList.SelectedItem is not Post selected || ReferenceEquals(selected, _current)) return;
        if (IsDirty)
        {
            var choice = MessageBox.Show(this,
                $"「{_current!.Title}」还有未保存的改动，要先保存吗？",
                "切换文章", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel)
            {
                _ = Dispatcher.BeginInvoke(() => PostList.SelectedItem = _current);
                return;
            }
            if (choice == MessageBoxResult.Yes && !SaveCurrent()) return;
        }

        await LoadPostAsync(selected);
    }

    /// <summary>切换文章要快：正文异步读，配图异步加载，界面不等任何一边。</summary>
    private async Task LoadPostAsync(Post post)
    {
        var stopwatch = Stopwatch.StartNew();
        _current = post;
        try
        {
            var text = await File.ReadAllTextAsync(post.FilePath, Encoding.UTF8);
            _newline = text.Contains("\r\n") ? "\r\n" : "\n";
            _loadingEditor = true;
            Editor.Text = text.Replace("\r\n", "\n");
            Editor.IsEnabled = true;
            _savedText = Editor.Text;
        }
        catch (Exception ex)
        {
            AppendLog($"打开「{post.Title}」失败：{ex.Message}");
            Editor.Text = string.Empty;
            Editor.IsEnabled = false;
            _savedText = string.Empty;
        }
        finally
        {
            _loadingEditor = false;
        }

        stopwatch.Stop();
        // 正常切换应当在几十毫秒内完成，慢到能感觉出来才值得记一笔
        if (stopwatch.ElapsedMilliseconds > 150)
            AppendLog($"[切换] {post.Title} 用时 {stopwatch.ElapsedMilliseconds} ms");

        UpdateEditorState();
        SetStatus(post.ImageDir is null ? $"{post.Title}（没有配图目录）" : post.Title);

        // 不 await：配图在后台按顺序解码，文章本身已经可以编辑了
        _ = LoadImagesAsync(post);
    }

    /// <summary>先把格子摆出来（立即返回），再并发解码缩略图；切换文章时取消上一批。</summary>
    private async Task LoadImagesAsync(Post post)
    {
        _imageLoadCts?.Cancel();
        _imageLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _imageLoadCts = cts;

        var items = PostLibrary.ListImages(post.ImageDir)
            .Select(file => new ImageItem(file.Path, file.CanPreview))
            .ToList();

        if (cts.IsCancellationRequested) return;
        ImageList.ItemsSource = items;
        ImagesHeaderText.Text = post.ImageKey is null ? "图片工作区" : $"图片工作区 · {post.ImageKey}";
        OpenImageFolderButton.IsEnabled = post.ImageDir is not null;
        ImageEmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (items.Count == 0)
        {
            AppendLog($"{post.Title}：没有配图。");
            return;
        }

        using var slots = new SemaphoreSlim(ThumbnailParallelism);
        var loads = items.Where(item => item.CanPreview).Select(async item =>
        {
            try
            {
                await slots.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var thumbnail = await Task.Run(() => LoadThumbnail(item.Path), cts.Token);
                if (cts.IsCancellationRequested) return;
                if (thumbnail is null)
                {
                    await Dispatcher.InvokeAsync(item.MarkFailed);
                    return;
                }
                await Dispatcher.InvokeAsync(() => item.Thumbnail = thumbnail);
            }
            catch (OperationCanceledException)
            {
                // 切换文章了，丢掉这一批
            }
            catch (Exception ex)
            {
                AppendLog($"缩略图读取失败：{Path.GetFileName(item.Path)} — {ex.Message}");
                await Dispatcher.InvokeAsync(item.MarkFailed);
            }
            finally
            {
                slots.Release();
            }
        });

        await Task.WhenAll(loads);
        if (!cts.IsCancellationRequested)
            AppendLog($"{post.Title}：配图 {items.Count} 张。");
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 300;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 编辑与保存 ----------

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e) => UpdateEditorState();

    private void UpdateEditorState()
    {
        var dirty = IsDirty;
        SaveButton.IsEnabled = _current is not null && dirty;
        EditorStateText.Text = _current is null ? string.Empty : dirty ? "有未保存的改动" : "已保存";
        EditorStateText.Foreground = dirty
            ? System.Windows.Media.Brushes.OrangeRed
            : (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveCurrent();
            e.Handled = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveCurrent();

    private bool SaveCurrent()
    {
        if (_current is null) return false;
        try
        {
            var text = Editor.Text.Replace("\r\n", "\n").Replace("\n", _newline);
            File.WriteAllText(_current.FilePath, text, new UTF8Encoding(false));
            _savedText = Editor.Text;
            UpdateEditorState();
            AppendLog($"已保存 {Path.GetFileName(_current.FilePath)}");
            SetStatus("已保存");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"保存失败：{ex.Message}");
            MessageBox.Show(this, $"保存失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private bool ConfirmDiscard()
    {
        if (!IsDirty) return true;
        var choice = MessageBox.Show(this, "当前文章有未保存的改动，要放弃吗？", "放弃改动",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return choice == MessageBoxResult.Yes;
    }

    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (IsDirty && !SaveCurrent()) return;
        Launch(_current.FilePath);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        Launch("explorer.exe", $"/select,\"{_current.FilePath}\"");
    }

    // ---------- 图片工作区 ----------

    private static ImageItem? ItemFromMenu(object sender) =>
        (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: FrameworkElement target }
            ? target.DataContext as ImageItem
            : null;

    private void OnImageThumbClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageItem item }) Launch(item.Path);
    }

    private void OnOpenImageClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item) Launch(item.Path);
    }

    private void OnRevealImageClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item) Launch("explorer.exe", $"/select,\"{item.Path}\"");
    }

    private void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is not { } item || _current?.ImageKey is not { } key) return;
        var snippet = item.Markdown(key);
        var caret = Editor.CaretIndex;
        Editor.Text = Editor.Text.Insert(caret, snippet);
        Editor.CaretIndex = caret + snippet.Length;
        Editor.Focus();
    }

    private void OnOpenImageFolderClick(object sender, RoutedEventArgs e)
    {
        if (_current?.ImageDir is { } dir) Launch(dir);
    }

    // ---------- 本地预览（一个按钮，来回切） ----------

    private void OnPreviewToggleClick(object sender, RoutedEventArgs e)
    {
        if (_server.IsRunning)
        {
            StopPreview();
            return;
        }
        StartPreview();
    }

    private void StartPreview()
    {
        if (ProcessRunner.PortInUse(_server.Port))
        {
            AppendLog($"端口 {_server.Port} 已被占用：可能有别处的 hexo server 在跑，先停掉再启动。");
            SetStatus($"端口 {_server.Port} 被占用");
            return;
        }

        try
        {
            _server.Start(_settings.SitePath);
            AppendLog($"正在启动本地预览：http://localhost:{_server.Port}（首次启动要等几秒）");
            UpdatePreviewUi();
        }
        catch (Exception ex)
        {
            AppendLog($"启动失败：{ex.Message}");
            SetStatus("本地预览启动失败");
            UpdatePreviewUi();
        }
    }

    private void StopPreview()
    {
        _server.Stop();
        AppendLog("本地预览已停止。");
        SetStatus("本地预览已停止");
        UpdatePreviewUi();
    }

    private void OnServerExited()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(OnServerExited);
            return;
        }
        UpdatePreviewUi();
    }

    private void UpdatePreviewUi()
    {
        var running = _server.IsRunning;
        PreviewButton.Content = running ? "停止本地预览" : "启动本地预览";
        PreviewButton.Icon = new SymbolIcon { Symbol = running ? SymbolRegular.Stop24 : SymbolRegular.Play24 };
        PreviewButton.Appearance = running ? ControlAppearance.Secondary : ControlAppearance.Primary;
        OpenSiteButton.IsEnabled = running;

        if (running) SetStatus($"本地预览运行中 · http://localhost:{_server.Port}");
    }

    private void OnOpenSiteClick(object sender, RoutedEventArgs e) => Launch("http://localhost:" + _server.Port);

    // ---------- 构建与发布 ----------

    private async void OnBuildClick(object sender, RoutedEventArgs e)
    {
        var entry = Path.Combine(_settings.SitePath, "node_modules", "hexo", "bin", "hexo");
        if (!File.Exists(entry))
        {
            AppendLog($"没找到 Hexo：{entry}");
            return;
        }

        await RunBusyAsync("构建", async () =>
        {
            AppendLog("[构建] hexo generate");
            var code = await ProcessRunner.RunAsync("node", $"\"{entry}\" generate", _settings.SitePath, AppendLog);
            AppendLog(code == 0 ? "[构建] 完成，产物在 public 目录。" : $"[构建] 失败，退出码 {code}。");
            SetStatus(code == 0 ? "构建完成" : "构建失败");
        });
    }

    private async void OnPublishClick(object sender, RoutedEventArgs e)
    {
        if (IsDirty && !SaveCurrent()) return;

        var dialog = new CommitMessageDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        await RunBusyAsync("发布", async () =>
        {
            var result = await GitPublisher.PublishAsync(
                _settings.SitePath, dialog.Message, _settings.ProxyUrl, AppendLog);

            AppendLog(result.Message);
            SetStatus(result.Message);
            if (result.Outcome == PublishOutcome.Failed)
                MessageBox.Show(this, result.Message, "发布没有完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private async Task RunBusyAsync(string label, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetBusyUi(true);
        SetStatus($"{label}中…");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog($"{label}出错：{ex.Message}");
            SetStatus($"{label}出错");
        }
        finally
        {
            _busy = false;
            SetBusyUi(false);
        }
    }

    private void SetBusyUi(bool busy)
    {
        PreviewButton.IsEnabled = !busy;
        BuildButton.IsEnabled = !busy;
        PublishButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && IsDirty;
    }

    // ---------- 日志与状态 ----------

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.LineCount > MaxLogLines)
        {
            var lines = LogBox.Text.Split('\n');
            LogBox.Text = string.Join("\n", lines.Skip(lines.Length - MaxLogLines));
        }
        LogBox.ScrollToEnd();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void SetStatus(string text) => StatusText.Text = text;

    private static void Launch(string target, string? arguments = null)
    {
        try
        {
            var startInfo = arguments is null
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo(target, arguments) { UseShellExecute = true };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开：{ex.Message}", "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 关闭 ----------

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (IsDirty)
        {
            var choice = MessageBox.Show(this, "还有未保存的改动，要保存后再退出吗？", "退出",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (choice == MessageBoxResult.Yes && !SaveCurrent()) { e.Cancel = true; return; }
        }
        _imageLoadCts?.Cancel();
        _server.Dispose();
        _settings.Save();
    }
}
