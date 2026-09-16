using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
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
using PalmierPro.WinUI.Audio;

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
    private WindowsAudioEngine? _audio;
    private const double PixelsPerFrame = 2;
    private const double TrackHeight = 58;
    private const double TrackLabelWidth = 66;
    private string? _selectedClipId;
    private ClipDragState? _drag;

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
        if (item.Type == ClipType.Audio)
        {
            try
            {
                _audio ??= new WindowsAudioEngine();
                _audio.PlayFile(item.Path);
                PreviewStatus.Text = $"{item.Name}  ·  playing via {_audio.SelectedDevice?.Name ?? "WASAPI"}";
            }
            catch (Exception error) { ProjectStatus.Text = error.Message; }
            return;
        }
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
            MediaItems.Add(new MediaItem(entry.Id, clip.Id, entry.Name, path, entry.Type, entry.Duration, entry.SourceWidth, entry.SourceHeight));
        }
        ClipSummary.Text = MediaItems.Count == 0 ? "No clips" : $"{MediaItems.Count} clip{(MediaItems.Count == 1 ? "" : "s")}";
        RefreshTimeline();
    }

    private void RefreshTimeline()
    {
        TimelineCanvas.Children.Clear();
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        var totalFrames = Math.Max(timeline.DisplayFrames, timeline.Fps * 10);
        var width = TrackLabelWidth + totalFrames * PixelsPerFrame + 120;
        TimelineCanvas.Width = width;
        TimelineCanvas.Height = Math.Max(TrackHeight, timeline.Tracks.Count * TrackHeight);
        TimelineFps.Text = $"  /  {timeline.Fps} FPS";
        TimelineReadout.Text = $"  /  {Timecode(0, timeline.Fps)}";

        for (var trackIndex = 0; trackIndex < timeline.Tracks.Count; trackIndex++)
        {
            var track = timeline.Tracks[trackIndex];
            var row = new Border
            {
                Width = width,
                Height = TrackHeight - 4,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(34, 253, 252, 248)),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(35, 21, 19, 20)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7)
            };
            Canvas.SetLeft(row, 0);
            Canvas.SetTop(row, trackIndex * TrackHeight + 2);
            TimelineCanvas.Children.Add(row);

            var trackLabel = new TextBlock
            {
                Text = track.Name ?? $"{track.Type} {trackIndex + 1}",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedBrush"],
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(trackLabel, 14);
            Canvas.SetTop(trackLabel, trackIndex * TrackHeight + 21);
            TimelineCanvas.Children.Add(trackLabel);

            foreach (var clip in track.Clips.OrderBy(item => item.StartFrame))
            {
                var assetName = _document.Manifest.Entries.FirstOrDefault(entry => entry.Id == clip.MediaRef)?.Name ?? clip.MediaRef;
                var block = new Button
                {
                    Tag = clip.Id,
                    Content = new TextBlock { Text = assetName, TextTrimming = TextTrimming.CharacterEllipsis },
                    Width = Math.Max(38, clip.DurationFrames * PixelsPerFrame),
                    Height = TrackHeight - 16,
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = ClipBrush(clip, clip.Id == _selectedClipId),
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InkBrush"],
                    BorderBrush = clip.Id == _selectedClipId
                        ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VioletBrush"]
                        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(45, 22, 21, 19)),
                    BorderThickness = new Thickness(1)
                };
                block.Click += ClipBlock_Click;
                block.PointerPressed += ClipBlock_PointerPressed;
                block.PointerMoved += ClipBlock_PointerMoved;
                block.PointerReleased += ClipBlock_PointerReleased;
                Canvas.SetLeft(block, TrackLabelWidth + clip.StartFrame * PixelsPerFrame);
                Canvas.SetTop(block, trackIndex * TrackHeight + 9);
                TimelineCanvas.Children.Add(block);
            }
        }
    }

    private void ClipBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string clipId }) return;
        _selectedClipId = clipId;
        foreach (var child in TimelineCanvas.Children.OfType<Button>())
        {
            if (child.Tag is not string candidateId) continue;
            child.Background = ClipBrush(FindClip(candidateId), candidateId == _selectedClipId);
            child.BorderBrush = candidateId == _selectedClipId
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VioletBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(45, 22, 21, 19));
        }
        var media = MediaItems.FirstOrDefault(item => item.ClipId == clipId);
        if (media is not null) MediaList.SelectedItem = media;
        ProjectStatus.Text = "Clip selected · drag to move";
    }

    private void ClipBlock_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button { Tag: string clipId } block) return;
        var clip = FindClip(clipId);
        var point = e.GetCurrentPoint(TimelineCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        _selectedClipId = clipId;
        _drag = new ClipDragState(block, clipId, point.Position.X, clip.StartFrame);
        block.CapturePointer(e.Pointer);
        e.Handled = false;
    }

    private void ClipBlock_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || sender is not Button block || !ReferenceEquals(block, _drag.Block)) return;
        var point = e.GetCurrentPoint(TimelineCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        var newStart = Math.Max(0, _drag.OriginalStartFrame + (int)Math.Round((point.Position.X - _drag.PointerStartX) / PixelsPerFrame));
        Canvas.SetLeft(block, TrackLabelWidth + newStart * PixelsPerFrame);
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is not null) TimelineReadout.Text = $"  /  {Timecode(newStart, timeline.Fps)}";
    }

    private async void ClipBlock_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || sender is not Button block || !ReferenceEquals(block, _drag.Block)) return;
        var point = e.GetCurrentPoint(TimelineCanvas);
        block.ReleasePointerCapture(e.Pointer);
        var drag = _drag;
        _drag = null;
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        var newStart = Math.Max(0, drag.OriginalStartFrame + (int)Math.Round((point.Position.X - drag.PointerStartX) / PixelsPerFrame));
        if (newStart == drag.OriginalStartFrame) return;
        try
        {
            await _document.ExecuteAsync(new MoveClipCommand(timeline.Id, drag.ClipId, newStart));
            RefreshFromDocument();
            ProjectStatus.Text = "Clip moved · undo available";
        }
        catch (Exception error)
        {
            RefreshTimeline();
            ProjectStatus.Text = error.Message;
        }
    }

    private async void SplitSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClipId is null) { ProjectStatus.Text = "Select a timeline clip first"; return; }
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        var clip = FindClip(_selectedClipId);
        if (clip.DurationFrames < 2) { ProjectStatus.Text = "Clip is too short to split"; return; }
        try
        {
            await _document.ExecuteAsync(new SplitClipCommand(timeline.Id, clip.Id, clip.StartFrame + clip.DurationFrames / 2));
            RefreshFromDocument();
            ProjectStatus.Text = "Clip split · undo available";
        }
        catch (Exception error) { ProjectStatus.Text = error.Message; }
    }

    private Clip FindClip(string clipId) => _document.Project.Timelines.SelectMany(timeline => timeline.Tracks).SelectMany(track => track.Clips).First(clip => clip.Id == clipId);

    private Microsoft.UI.Xaml.Media.Brush ClipBrush(Clip clip, bool selected)
    {
        if (selected) return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VioletWashBrush"];
        var color = clip.MediaType == ClipType.Audio ? Windows.UI.Color.FromArgb(90, 119, 112, 201) : Windows.UI.Color.FromArgb(58, 253, 252, 248);
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    private static string Timecode(int frame, int fps)
    {
        var totalSeconds = Math.Max(0, frame) / Math.Max(1, fps);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        var frames = Math.Max(0, frame) % Math.Max(1, fps);
        return $"{hours:00}:{minutes:00}:{seconds:00}:{frames:00}";
    }

    private sealed record ClipDragState(Button Block, string ClipId, double PointerStartX, int OriginalStartFrame);

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

    private async void ConfigureAudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _audio ??= new WindowsAudioEngine();
            var devices = _audio.EnumerateDevices();
            var choices = devices.Where(device => device.Backend != AudioBackendKind.Asio)
                .SelectMany(device => new[]
                {
                    device,
                    device with { Id = device.Id, Name = device.Name + " · WASAPI exclusive", Backend = AudioBackendKind.WasapiExclusive }
                })
                .Concat(devices.Where(device => device.Backend == AudioBackendKind.Asio))
                .ToArray();
            if (choices.Length == 0) throw new InvalidOperationException("No active WASAPI output or installed ASIO driver was found.");
            var selector = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var note = new TextBlock
            {
                Text = "WASAPI shared is the safest default. Exclusive mode reduces mixing latency. ASIO4ALL appears here only when its driver is already installed.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedBrush"]
            };
            var dialog = new ContentDialog
            {
                Title = "Audio output",
                Content = new StackPanel { Spacing = 12, Children = { selector, note } },
                PrimaryButtonText = "Use output",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || selector.SelectedItem is not AudioOutputDevice selected) return;
            _audio.Select(selected);
            ProjectStatus.Text = $"Audio · {selected.Name}";
        }
        catch (Exception error) { await ShowErrorAsync("Could not configure audio", error.Message); }
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
        _audio?.Dispose();
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
    public MediaItem(string assetId, string clipId, string name, string path, ClipType type, double duration, int? width, int? height)
    {
        AssetId = assetId;
        ClipId = clipId;
        Name = name;
        Path = path;
        Type = type;
        Duration = duration;
        Width = width;
        Height = height;
    }

    public string AssetId { get; set; }
    public string ClipId { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public ClipType Type { get; set; }
    public double Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string DurationLabel => Duration <= 0 ? "duration unknown" : TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
