using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRT.Interop;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Automation;
using PalmierPro.Core.Media;
using PalmierPro.Core.Models;
using PalmierPro.Core.Persistence;
using PalmierPro.WinUI.AI;

namespace PalmierPro.WinUI;

public sealed partial class MainWindow : Window
{
    private EditorDocument _document = new(ProjectFile.CreateDefault());
    private string? _projectPath;
    private readonly FfmpegMediaProbe _probe = new();
    private readonly FfmpegFrameRenderer _frameRenderer = new();
    private readonly WindowsCredentialStore _credentials = new();
    private McpLoopbackServer? _mcp;
    private PalmierPro.Core.AI.CodexAppServerClient? _codex;

    public MainWindow()
    {
        InitializeComponent();
        RefreshFromDocument();
        Closed += MainWindow_Closed;
        _ = RestartMcpAsync();
    }

    public ObservableCollection<MediaItem> MediaItems { get; } = [];

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowHandle());
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".m4v");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        var files = await picker.PickMultipleFilesAsync();
        if (files is not null) await ImportFilesAsync(files);
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowHandle());
        picker.ViewMode = PickerViewMode.List;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        try
        {
            var loaded = await ProjectPackageStore.OpenAsync(folder.Path);
            _document = new EditorDocument(loaded.Project, loaded.Manifest);
            _projectPath = folder.Path;
            await EnsureDefaultTracksAsync();
            await _document.MarkSavedAsync();
            await RestartMcpAsync();
            RefreshFromDocument();
        }
        catch (Exception error)
        {
            await ShowErrorAsync("Could not open project", error.Message);
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        _projectPath ??= await PickProjectPathAsync();
        if (_projectPath is null) return;
        try
        {
            await ProjectPackageStore.SaveAsync(_projectPath, await _document.SnapshotAsync());
            await _document.MarkSavedAsync();
            ProjectStatus.Text = "Saved";
        }
        catch (Exception error)
        {
            await ShowErrorAsync("Could not save project", error.Message);
        }
    }

    private async void RemoveClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MediaItem item) return;
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        try
        {
            await _document.ExecuteAsync(new RemoveMediaCommand(timeline.Id, item.ClipId, item.AssetId));
            RefreshFromDocument();
        }
        catch (Exception error)
        {
            await ShowErrorAsync("Could not remove clip", error.Message);
        }
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaItem item) return;
        PreviewStatus.Text = $"{item.Name}  ·  {item.DurationLabel}";
        EmptyState.Visibility = Visibility.Collapsed;
        _ = RenderPreviewAsync(item);
    }

    private void Canvas_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        e.DragUIOverride.Caption = "Import media";
    }

    private async void Canvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await ImportFilesAsync(items.OfType<StorageFile>());
    }

    private async Task ImportFilesAsync(IEnumerable<StorageFile> files)
    {
        await EnsureDefaultTracksAsync();
        var timeline = _document.Project.Timelines.First();
        foreach (var file in files)
        {
            if (MediaItems.Any(item => string.Equals(item.Path, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
            var type = ClipTypeFromExtension(file.FileType);
            if (type is null) continue;
            var track = timeline.Tracks.FirstOrDefault(candidate => candidate.Type == type.Value);
            if (track is null)
            {
                await _document.ExecuteAsync(new AddTrackCommand(timeline.Id, type.Value, $"{TrackPrefix(type.Value)}{timeline.Tracks.Count(candidate => candidate.Type == type.Value) + 1}"));
                track = timeline.Tracks.Last(candidate => candidate.Type == type.Value);
            }
            MediaMetadata? metadata = null;
            try { metadata = await _probe.ProbeAsync(file.Path); }
            catch { /* Import remains available offline; the asset is marked as needing a probe. */ }
            var duration = metadata?.DurationSeconds > 0 ? metadata.DurationSeconds : type == ClipType.Image ? 5 : 10;
            var clip = new Clip
            {
                Id = Guid.NewGuid().ToString(),
                MediaRef = Guid.NewGuid().ToString(),
                MediaType = type.Value,
                SourceClipType = type.Value,
                StartFrame = track.EndFrame,
                DurationFrames = Math.Max(1, (int)Math.Round(duration * timeline.Fps, MidpointRounding.ToEven))
            };
            await _document.ExecuteAsync(new ImportMediaCommand(timeline.Id, track.Id, clip, new MediaManifestEntry
            {
                Id = clip.MediaRef,
                Name = file.Name,
                Type = type.Value,
                Duration = duration,
                SourceWidth = metadata?.Width,
                SourceHeight = metadata?.Height,
                SourceFps = metadata?.FrameRate,
                HasAudio = metadata?.HasAudio,
                Source = new MediaSource.External(file.Path)
            }));
        }
        RefreshFromDocument();
    }

    private void RefreshFromDocument()
    {
        MediaItems.Clear();
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        foreach (var entry in _document.Manifest.Entries)
        {
            var clip = timeline.Tracks.SelectMany(track => track.Clips).FirstOrDefault(item => item.MediaRef == entry.Id);
            if (clip is null) continue;
            var path = entry.Source switch
            {
                MediaSource.External external => external.AbsolutePath,
                MediaSource.Project project => Path.GetFullPath(Path.Combine(_projectPath ?? string.Empty, project.RelativePath)),
                _ => string.Empty
            };
            MediaItems.Add(new MediaItem(entry.Id, clip.Id, entry.Name, path, entry.Duration, entry.SourceWidth, entry.SourceHeight));
        }
        ClipSummary.Text = MediaItems.Count == 0 ? "No clips" : $"{MediaItems.Count} clip{(MediaItems.Count == 1 ? "" : "s")}";
    }

    private async Task<string?> PickProjectPathAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowHandle());
        picker.ViewMode = PickerViewMode.List;
        picker.FileTypeFilter.Add("*");
        var parent = await picker.PickSingleFolderAsync();
        if (parent is null) return null;
        var projectPath = Path.Combine(parent.Path, "Untitled.palmier");
        if (!Directory.Exists(projectPath)) Directory.CreateDirectory(projectPath);
        return projectPath;
    }

    private async Task RenderPreviewAsync(MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            PreviewFrame.Source = null;
            return;
        }
        try
        {
            var bytes = await _frameRenderer.CapturePngAsync(item.Path, TimeSpan.Zero);
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            PreviewFrame.Source = bitmap;
            PreviewStatus.Text = $"{item.Name}  ·  {item.DurationLabel}";
        }
        catch (Exception error)
        {
            PreviewStatus.Text = $"{item.Name}  ·  preview unavailable";
            ProjectStatus.Text = error.Message;
        }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        await _document.UndoAsync();
        RefreshFromDocument();
        ProjectStatus.Text = "Undid last change";
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        await _document.RedoAsync();
        RefreshFromDocument();
        ProjectStatus.Text = "Redid last change";
    }

    private async void ConnectAgent_Click(object sender, RoutedEventArgs e)
    {
        var provider = new ComboBox { ItemsSource = new[] { "OpenAI API key", "Anthropic API key", "Codex account (browser)", "Codex account (device code)" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var key = new PasswordBox { PlaceholderText = "API key (kept in Windows Credential Manager)", HorizontalAlignment = HorizontalAlignment.Stretch };
        provider.SelectionChanged += (_, _) => key.Visibility = provider.SelectedIndex >= 2 ? Visibility.Collapsed : Visibility.Visible;
        var dialog = new ContentDialog
        {
            Title = "AI connections",
            Content = new StackPanel { Spacing = 12, Children = { provider, key, new TextBlock { Text = "Account sign-in is kept separate from media-generation providers.", TextWrapping = TextWrapping.Wrap } } },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (provider.SelectedIndex >= 2)
            {
                _codex ??= new PalmierPro.Core.AI.CodexAppServerClient();
                await _codex.StartAsync();
                var login = await _codex.LoginAsync(provider.SelectedIndex == 2 ? "chatgpt" : "chatgptDeviceCode");
                if (login.TryGetProperty("authUrl", out var authUrl) && Uri.TryCreate(authUrl.GetString(), UriKind.Absolute, out var browserUrl))
                    await Windows.System.Launcher.LaunchUriAsync(browserUrl);
                var userCode = login.TryGetProperty("userCode", out var code) ? code.GetString() : null;
                ProjectStatus.Text = userCode is null ? "Codex login started" : $"Codex device code: {userCode}";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(key.Password)) throw new InvalidOperationException("Enter an API key.");
                _credentials.Save(provider.SelectedIndex == 0 ? "openai" : "anthropic", key.Password);
                ProjectStatus.Text = $"{provider.SelectedItem} connected";
            }
        }
        catch (Exception error) { await ShowErrorAsync("Could not connect agent", error.Message); }
    }

    private async Task RestartMcpAsync()
    {
        if (_mcp is not null) await _mcp.DisposeAsync();
        _mcp = new McpLoopbackServer(_document);
        try
        {
            await _mcp.StartAsync();
            ProjectStatus.Text = "MCP READY · 127.0.0.1:19789";
        }
        catch (Exception error)
        {
            ProjectStatus.Text = "MCP OFF · " + error.Message;
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_mcp is not null) await _mcp.DisposeAsync();
        if (_codex is not null) await _codex.DisposeAsync();
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private IntPtr WindowHandle() => WindowNative.GetWindowHandle(this);

    private static ClipType? ClipTypeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".mov" or ".m4v" or ".mkv" or ".webm" => ClipType.Video,
        ".mp3" or ".wav" or ".m4a" => ClipType.Audio,
        ".png" or ".jpg" or ".jpeg" => ClipType.Image,
        _ => null
    };

    private async Task EnsureDefaultTracksAsync()
    {
        var project = _document.Project;
        var timeline = project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        if (!timeline.Tracks.Any(track => track.Type == ClipType.Video)) await _document.ExecuteAsync(new AddTrackCommand(timeline.Id, ClipType.Video, "V1"));
        if (!timeline.Tracks.Any(track => track.Type == ClipType.Audio)) await _document.ExecuteAsync(new AddTrackCommand(timeline.Id, ClipType.Audio, "A1"));
    }

    private static string TrackPrefix(ClipType type) => type switch
    {
        ClipType.Audio => "A",
        ClipType.Subtitle => "S",
        _ => "V"
    };
}

public sealed class MediaItem
{
    public MediaItem(string assetId, string clipId, string name, string path, double duration, int? width, int? height)
    {
        AssetId = assetId;
        ClipId = clipId;
        Name = name;
        Path = path;
        Duration = duration;
        Width = width;
        Height = height;
    }

    public string AssetId { get; set; }
    public string ClipId { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public double Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string DurationLabel => Duration <= 0 ? "duration unknown" : TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
