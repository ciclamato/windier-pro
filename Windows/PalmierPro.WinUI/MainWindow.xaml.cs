using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Automation;
using PalmierPro.Core.Media;
using PalmierPro.Core.Models;
using PalmierPro.Core.Persistence;
using PalmierPro.Core.Export;
using PalmierPro.WinUI.AI;
using PalmierPro.WinUI.Audio;
using PalmierPro.WinUI.Playback;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

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
    private readonly SharedPlaybackClock _playbackClock = new();
    private readonly DispatcherQueueTimer _playbackTimer;
    private CancellationTokenSource? _previewRenderCancellation;
    private Task? _previewRenderTask;
    private const double PixelsPerFrame = 2;
    private const double TrackHeight = 58;
    private const double TrackLabelWidth = 66;
    private string? _selectedClipId;
    private ClipDragState? _drag;
    private int _playheadFrame;
    private Border? _playheadElement;
    private readonly List<MediaItem> _mediaCatalog = [];

    public MainWindow()
    {
        InitializeComponent();
        _playbackTimer = (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("The editor must start on the Windows UI thread.")).CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(50);
        _playbackTimer.Tick += PlaybackTimer_Tick;
        RefreshFromDocument();
        Closed += MainWindow_Closed;
        _ = RestartMcpAsync();
    }

    public ObservableCollection<MediaItem> MediaItems { get; } = [];

    private void MediaSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyMediaFilter();

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
        picker.FileTypeFilter.Add(".srt");
        picker.FileTypeFilter.Add(".vtt");
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

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = await _document.SnapshotAsync();
            var preflight = FfmpegExportService.Preflight(snapshot);
            if (!preflight.IsSupported) throw new NotSupportedException(preflight.Message);
            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, WindowHandle());
            picker.SuggestedFileName = "Palmier export";
            picker.FileTypeChoices.Add("H.264 video", [".mp4"]);
            picker.FileTypeChoices.Add("ProRes video", [".mov"]);
            picker.FileTypeChoices.Add("Final Cut Pro XML", [".fcpxml"]);
            picker.FileTypeChoices.Add("Premiere XML", [".xml"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var extension = Path.GetExtension(file.Path);
            if (extension.Equals(".fcpxml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                await XmlTimelineExporter.ExportAsync(snapshot, _projectPath ?? string.Empty, file.Path,
                    extension.Equals(".fcpxml", StringComparison.OrdinalIgnoreCase) ? TimelineXmlFormat.Fcpxml : TimelineXmlFormat.Xmeml);
                ProjectStatus.Text = $"Exported · {Path.GetFileName(file.Path)}";
                return;
            }
            var profile = extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ? ExportProfiles.ProRes : ExportProfiles.H264;
            ProjectStatus.Text = "Exporting · 0%";
            var progress = new Progress<double>(value => ProjectStatus.Text = $"Exporting · {value:P0}");
            await new FfmpegExportService().ExportAsync(snapshot, _projectPath ?? string.Empty, file.Path, profile, progress);
            ProjectStatus.Text = $"Exported · {Path.GetFileName(file.Path)}";
        }
        catch (Exception error) { await ShowErrorAsync("Could not export timeline", error.Message); }
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
                _audio ??= new WindowsAudioEngine(_playbackClock);
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
            if (type == ClipType.Subtitle)
            {
                var assetId = Guid.NewGuid().ToString();
                var captionClips = await ReadCaptionClipsAsync(file.Path, timeline.Fps, assetId);
                var captionDuration = captionClips.Max(clip => clip.EndFrame) / (double)timeline.Fps;
                await _document.ExecuteAsync(new ImportSubtitleCommand(timeline.Id, track.Id, captionClips, new MediaManifestEntry
                {
                    Id = assetId,
                    Name = file.Name,
                    Type = ClipType.Subtitle,
                    Duration = captionDuration,
                    Source = new MediaSource.External(file.Path),
                    CreatedAt = DateTimeOffset.UtcNow
                }));
                continue;
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
        _mediaCatalog.Clear();
        MediaItems.Clear();
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        foreach (var entry in _document.Manifest.Entries)
        {
            var relatedClips = timeline.Tracks.SelectMany(track => track.Clips).Where(item => item.MediaRef == entry.Id).ToArray();
            var clip = relatedClips.FirstOrDefault();
            if (clip is null) continue;
            var path = entry.Source switch
            {
                MediaSource.External external => external.AbsolutePath,
                MediaSource.Project project => Path.GetFullPath(Path.Combine(_projectPath ?? string.Empty, project.RelativePath)),
                _ => string.Empty
            };
            var textContent = string.Join(Environment.NewLine, relatedClips.Select(item => item.TextContent).Where(text => !string.IsNullOrWhiteSpace(text)));
            _mediaCatalog.Add(new MediaItem(entry.Id, clip.Id, entry.Name, path, entry.Type, entry.Duration, entry.SourceWidth, entry.SourceHeight, textContent));
        }
        ClipSummary.Text = _mediaCatalog.Count == 0 ? "No clips" : $"{_mediaCatalog.Count} clip{(_mediaCatalog.Count == 1 ? "" : "s")}";
        ApplyMediaFilter();
        if (_selectedClipId is not null && !timeline.Tracks.SelectMany(track => track.Clips).Any(clip => clip.Id == _selectedClipId))
            _selectedClipId = null;
        _playheadFrame = Math.Clamp(_playheadFrame, 0, Math.Max(0, timeline.DisplayFrames));
        RefreshInspector();
        RefreshTimeline();
    }

    private void ApplyMediaFilter()
    {
        var query = MediaSearchBox?.Text?.Trim();
        IEnumerable<MediaItem> filtered = string.IsNullOrWhiteSpace(query)
            ? _mediaCatalog
            : _mediaCatalog.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.TextContent?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        MediaItems.Clear();
        foreach (var item in filtered) MediaItems.Add(item);
        if (_mediaCatalog.Count > 0 && !string.IsNullOrWhiteSpace(query))
            ClipSummary.Text = $"{MediaItems.Count} of {_mediaCatalog.Count} clips";
    }

    private void RefreshInspector()
    {
        if (_selectedClipId is null)
        {
            InspectorControls.Visibility = Visibility.Collapsed;
            InspectorActions.Visibility = Visibility.Collapsed;
            InspectorTitle.Text = "Clip controls";
            InspectorSubtitle.Text = "Select a clip to edit its timing and mix.";
            return;
        }

        Clip clip;
        try { clip = FindClip(_selectedClipId); }
        catch (KeyNotFoundException)
        {
            _selectedClipId = null;
            RefreshInspector();
            return;
        }

        var name = _document.Manifest.Entries.FirstOrDefault(entry => entry.Id == clip.MediaRef)?.Name ?? clip.MediaRef;
        InspectorTitle.Text = name;
        InspectorSubtitle.Text = $"{clip.MediaType} · {clip.DurationFrames} frames · drag the clip or its edges in the timeline.";
        OpacityInput.Value = clip.Opacity;
        VolumeInput.Value = clip.Volume;
        SpeedInput.Value = clip.Speed;
        DurationInput.Value = clip.DurationFrames;
        InspectorControls.Visibility = Visibility.Visible;
        InspectorActions.Visibility = Visibility.Visible;
    }

    private void RefreshTimeline()
    {
        TimelineCanvas.Children.Clear();
        _playheadElement = null;
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        var totalFrames = Math.Max(timeline.DisplayFrames, timeline.Fps * 10);
        var width = TrackLabelWidth + totalFrames * PixelsPerFrame + 120;
        TimelineCanvas.Width = width;
        TimelineCanvas.Height = Math.Max(TrackHeight, timeline.Tracks.Count * TrackHeight);
        TimelineFps.Text = $"  /  {timeline.Fps} FPS";
        TimelineReadout.Text = $"  /  {Timecode(_playheadFrame, timeline.Fps)}";

        for (var frame = 0; frame <= totalFrames; frame += Math.Max(1, timeline.Fps))
        {
            var tick = new Border
            {
                Width = 1,
                Height = 8,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 119, 115, 108))
            };
            Canvas.SetLeft(tick, TrackLabelWidth + frame * PixelsPerFrame);
            Canvas.SetTop(tick, 0);
            TimelineCanvas.Children.Add(tick);
            var label = new TextBlock
            {
                Text = Timecode(frame, timeline.Fps)[..8],
                FontSize = 9,
                Foreground = (Brush)Application.Current.Resources["FaintBrush"]
            };
            Canvas.SetLeft(label, TrackLabelWidth + frame * PixelsPerFrame + 4);
            Canvas.SetTop(label, 0);
            TimelineCanvas.Children.Add(label);
        }

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
                    Content = new Grid
                    {
                        Children =
                        {
                            new Border { Width = 5, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)Application.Current.Resources["VioletBrush"], Opacity = .55 },
                            new TextBlock { Text = assetName, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 0, 7, 0) },
                            new Border { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Background = (Brush)Application.Current.Resources["VioletBrush"], Opacity = .55 }
                        }
                    },
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
                ToolTipService.SetToolTip(block, "Drag center to move · drag either edge to trim");
                Canvas.SetLeft(block, TrackLabelWidth + clip.StartFrame * PixelsPerFrame);
                Canvas.SetTop(block, trackIndex * TrackHeight + 9);
                TimelineCanvas.Children.Add(block);
            }
        }
        UpdatePlayheadVisual(timeline);
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
        RefreshInspector();
        ProjectStatus.Text = "Clip selected · drag center to move or an edge to trim";
    }

    private void ClipBlock_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button { Tag: string clipId } block) return;
        var clip = FindClip(clipId);
        var point = e.GetCurrentPoint(TimelineCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        _selectedClipId = clipId;
        var localPoint = e.GetCurrentPoint(block).Position.X;
        var edge = 10;
        var mode = localPoint <= edge
            ? ClipDragMode.TrimStart
            : localPoint >= Math.Max(edge + 1, block.Width - edge) ? ClipDragMode.TrimEnd : ClipDragMode.Move;
        _drag = new ClipDragState(block, clipId, point.Position.X, clip.StartFrame, clip.DurationFrames, clip.TrimStartFrame, mode);
        RefreshInspector();
        block.CapturePointer(e.Pointer);
        e.Handled = false;
    }

    private void ClipBlock_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || sender is not Button block || !ReferenceEquals(block, _drag.Block)) return;
        var point = e.GetCurrentPoint(TimelineCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        var delta = (int)Math.Round((point.Position.X - _drag.PointerStartX) / PixelsPerFrame);
        var newStart = _drag.OriginalStartFrame;
        var newDuration = _drag.OriginalDurationFrames;
        if (_drag.Mode == ClipDragMode.Move)
        {
            newStart = Math.Max(0, _drag.OriginalStartFrame + delta);
            Canvas.SetLeft(block, TrackLabelWidth + newStart * PixelsPerFrame);
        }
        else if (_drag.Mode == ClipDragMode.TrimStart)
        {
            newStart = Math.Clamp(_drag.OriginalStartFrame + delta, 0, _drag.OriginalStartFrame + _drag.OriginalDurationFrames - 1);
            newDuration = _drag.OriginalDurationFrames - (newStart - _drag.OriginalStartFrame);
            Canvas.SetLeft(block, TrackLabelWidth + newStart * PixelsPerFrame);
            block.Width = Math.Max(38, newDuration * PixelsPerFrame);
        }
        else
        {
            var newEnd = Math.Max(_drag.OriginalStartFrame + 1, _drag.OriginalStartFrame + _drag.OriginalDurationFrames + delta);
            newDuration = newEnd - _drag.OriginalStartFrame;
            block.Width = Math.Max(38, newDuration * PixelsPerFrame);
        }
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is not null)
            TimelineReadout.Text = $"  /  {Timecode(_drag.Mode == ClipDragMode.TrimEnd ? _drag.OriginalStartFrame + newDuration : newStart, timeline.Fps)}";
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
        var delta = (int)Math.Round((point.Position.X - drag.PointerStartX) / PixelsPerFrame);
        var newStart = Math.Max(0, drag.OriginalStartFrame + delta);
        var newDuration = drag.OriginalDurationFrames;
        if (drag.Mode == ClipDragMode.TrimStart)
        {
            newStart = Math.Clamp(newStart, 0, drag.OriginalStartFrame + drag.OriginalDurationFrames - 1);
            newDuration = drag.OriginalDurationFrames - (newStart - drag.OriginalStartFrame);
        }
        else if (drag.Mode == ClipDragMode.TrimEnd)
        {
            var newEnd = Math.Max(drag.OriginalStartFrame + 1, drag.OriginalStartFrame + drag.OriginalDurationFrames + delta);
            newDuration = newEnd - drag.OriginalStartFrame;
        }
        if (drag.Mode == ClipDragMode.Move && newStart == drag.OriginalStartFrame
            || drag.Mode != ClipDragMode.Move && newDuration == drag.OriginalDurationFrames && newStart == drag.OriginalStartFrame) return;
        try
        {
            if (drag.Mode == ClipDragMode.Move)
                await _document.ExecuteAsync(new MoveClipCommand(timeline.Id, drag.ClipId, newStart));
            else
            {
                var trimStart = drag.OriginalTrimStartFrame + (newStart - drag.OriginalStartFrame);
                await _document.ExecuteAsync(new TrimClipCommand(timeline.Id, drag.ClipId, newDuration, trimStart, newStart));
            }
            RefreshFromDocument();
            ProjectStatus.Text = drag.Mode == ClipDragMode.Move ? "Clip moved · undo available" : "Clip trimmed · undo available";
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

    private async void ApplyInspector_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClipId is null) return;
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        try
        {
            await _document.ExecuteAsync(new SetClipPropertiesCommand(
                timeline.Id,
                _selectedClipId,
                OpacityInput.Value,
                VolumeInput.Value,
                SpeedInput.Value,
                (int)Math.Round(DurationInput.Value)));
            RefreshFromDocument();
            ProjectStatus.Text = "Clip properties applied · undo available";
        }
        catch (Exception error) { await ShowErrorAsync("Could not apply clip properties", error.Message); }
    }

    private async void RippleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClipId is null) return;
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        try
        {
            await _document.ExecuteAsync(new RippleDeleteCommand(timeline.Id, _selectedClipId));
            _selectedClipId = null;
            RefreshFromDocument();
            ProjectStatus.Text = "Ripple deleted · undo available";
        }
        catch (Exception error) { await ShowErrorAsync("Could not ripple delete", error.Message); }
    }

    private async void AddMarker_Click(object sender, RoutedEventArgs e)
    {
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        try
        {
            await _document.ExecuteAsync(new AddMarkerCommand(timeline.Id, new TimelineMarker
            {
                Name = $"Marker {timeline.Markers.Count + 1}",
                StartFrame = _playheadFrame,
                DurationFrames = 0,
                Comment = "Added in Windows editor"
            }));
            RefreshFromDocument();
            ProjectStatus.Text = "Marker added · undo available";
        }
        catch (Exception error) { await ShowErrorAsync("Could not add marker", error.Message); }
    }

    private void TimelineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        var point = e.GetCurrentPoint(TimelineCanvas).Position;
        if (point.X < TrackLabelWidth) return;
        SetPlayheadFrame((int)Math.Round((point.X - TrackLabelWidth) / PixelsPerFrame));
        e.Handled = true;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null || timeline.TotalFrames == 0)
        {
            ProjectStatus.Text = "Add media before starting playback";
            return;
        }
        if (_playbackClock.IsRunning) PausePlayback();
        else StartPlayback();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
                PlayPause_Click(sender, e);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                RippleDelete_Click(sender, e);
                e.Handled = true;
                break;
            case VirtualKey.S when !e.KeyStatus.IsMenuKeyDown:
                SplitSelected_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void StartPlayback()
    {
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        if (_playheadFrame >= timeline.TotalFrames) SetPlayheadFrame(0, false);
        _playbackClock.Start();
        _playbackTimer.Start();
        PlayPauseButton.Content = "Pause";
        ProjectStatus.Text = "Playing · shared timeline clock";
    }

    private void PausePlayback()
    {
        _playbackClock.Pause();
        _audio?.Pause();
        _playbackTimer.Stop();
        PlayPauseButton.Content = "Play";
        ProjectStatus.Text = "Paused";
    }

    private void PlaybackTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        var nextFrame = (int)Math.Floor(_playbackClock.PositionSeconds * timeline.Fps);
        if (nextFrame >= timeline.TotalFrames)
        {
            SetPlayheadFrame(timeline.TotalFrames, false);
            PausePlayback();
            return;
        }
        if (nextFrame == _playheadFrame) return;
        _playheadFrame = nextFrame;
        TimelineReadout.Text = $"  /  {Timecode(_playheadFrame, timeline.Fps)}";
        UpdatePlayheadVisual(timeline);
        RequestTimelinePreview(_playheadFrame);
    }

    private void SetPlayheadFrame(int frame, bool render = true)
    {
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        _playheadFrame = Math.Clamp(frame, 0, Math.Max(0, timeline.DisplayFrames));
        _playbackClock.Seek(TimeSpan.FromSeconds(_playheadFrame / (double)Math.Max(1, timeline.Fps)));
        TimelineReadout.Text = $"  /  {Timecode(_playheadFrame, timeline.Fps)}";
        UpdatePlayheadVisual(timeline);
        if (render) RequestTimelinePreview(_playheadFrame, cancelCurrent: true);
    }

    private void UpdatePlayheadVisual(Timeline timeline)
    {
        if (_playheadElement is null)
        {
            _playheadElement = new Border
            {
                Tag = "playhead",
                Width = 2,
                Background = (Brush)Application.Current.Resources["VioletBrush"],
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false
            };
            TimelineCanvas.Children.Add(_playheadElement);
        }
        _playheadElement.Height = Math.Max(TrackHeight, timeline.Tracks.Count * TrackHeight);
        Canvas.SetLeft(_playheadElement, TrackLabelWidth + _playheadFrame * PixelsPerFrame);
        Canvas.SetTop(_playheadElement, 0);
    }

    private void RequestTimelinePreview(int frame, bool cancelCurrent = false)
    {
        if (cancelCurrent) _previewRenderCancellation?.Cancel();
        if (_previewRenderTask is { IsCompleted: false }) return;
        _previewRenderTask = RenderTimelineFrameAsync(frame);
    }

    private async Task RenderTimelineFrameAsync(int frame)
    {
        var timeline = _document.Project.Timelines.FirstOrDefault();
        if (timeline is null) return;
        var clip = timeline.Tracks.Where(track => track.Type is ClipType.Video or ClipType.Image)
            .SelectMany(track => track.Clips)
            .Where(candidate => candidate.Contains(frame))
            .OrderByDescending(candidate => candidate.StartFrame)
            .FirstOrDefault();
        if (clip is null) return;
        var item = MediaItems.FirstOrDefault(candidate => candidate.ClipId == clip.Id);
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path)) return;
        var cancellation = new CancellationTokenSource();
        _previewRenderCancellation = cancellation;
        try
        {
            var sourceFps = _document.Manifest.Entries.FirstOrDefault(entry => entry.Id == clip.MediaRef)?.SourceFps ?? timeline.Fps;
            var sourceFrame = clip.TrimStartFrame + (int)Math.Round((frame - clip.StartFrame) * clip.Speed, MidpointRounding.ToEven);
            var bytes = await _frameRenderer.CapturePngAsync(item.Path, TimeSpan.FromSeconds(Math.Max(0, sourceFrame / Math.Max(.001, sourceFps))), cancellation.Token);
            if (cancellation.IsCancellationRequested || frame != _playheadFrame) return;
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            PreviewFrame.Source = bitmap;
            EmptyState.Visibility = Visibility.Collapsed;
            PreviewStatus.Text = $"{item.Name}  ·  {Timecode(frame, timeline.Fps)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ProjectStatus.Text = "Preview: " + error.Message; }
        finally
        {
            if (ReferenceEquals(_previewRenderCancellation, cancellation)) _previewRenderCancellation = null;
            cancellation.Dispose();
        }
    }

    private static T? FindParent<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value is not null)
        {
            if (value is T match) return match;
            value = VisualTreeHelper.GetParent(value);
        }
        return null;
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

    private enum ClipDragMode { Move, TrimStart, TrimEnd }

    private sealed record ClipDragState(
        Button Block,
        string ClipId,
        double PointerStartX,
        int OriginalStartFrame,
        int OriginalDurationFrames,
        int OriginalTrimStartFrame,
        ClipDragMode Mode);

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
            _audio ??= new WindowsAudioEngine(_playbackClock);
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
        _playbackTimer.Stop();
        _playbackClock.Pause();
        _previewRenderCancellation?.Cancel();
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
        ".srt" or ".vtt" => ClipType.Subtitle,
        _ => null
    };

    private static async Task<IReadOnlyList<Clip>> ReadCaptionClipsAsync(string path, int fps, string assetId)
    {
        var lines = await File.ReadAllLinesAsync(path);
        var clips = new List<Clip>();
        for (var index = 0; index < lines.Length;)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
            if (index >= lines.Length) break;
            if (!lines[index].Contains("-->", StringComparison.Ordinal))
            {
                index++;
                continue;
            }
            var timing = lines[index++].Split("-->", 2, StringSplitOptions.TrimEntries);
            if (timing.Length != 2 || !TryCaptionSeconds(timing[0], out var start) || !TryCaptionSeconds(timing[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var end))
                continue;
            var text = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) text.Add(lines[index++].Trim());
            if (end <= start || text.Count == 0) continue;
            var startFrame = Math.Max(0, (int)Math.Round(start * fps, MidpointRounding.ToEven));
            var endFrame = Math.Max(startFrame + 1, (int)Math.Round(end * fps, MidpointRounding.ToEven));
            clips.Add(new Clip
            {
                Id = Guid.NewGuid().ToString(),
                MediaRef = assetId,
                MediaType = ClipType.Subtitle,
                SourceClipType = ClipType.Subtitle,
                StartFrame = startFrame,
                DurationFrames = endFrame - startFrame,
                TextContent = string.Join(Environment.NewLine, text),
                CaptionGroupId = assetId
            });
        }
        if (clips.Count == 0) throw new InvalidDataException("The caption file contains no readable cues.");
        return clips;
    }

    private static bool TryCaptionSeconds(string value, out double seconds)
    {
        seconds = 0;
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 1 or > 3) return false;
        if (!double.TryParse(parts[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) return false;
        var minutes = 0d;
        var hours = 0d;
        if (parts.Length >= 2 && !double.TryParse(parts[^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minutes)) return false;
        if (parts.Length == 3 && !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hours)) return false;
        seconds = hours * 3600 + minutes * 60 + s;
        return double.IsFinite(seconds) && seconds >= 0;
    }

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
    public MediaItem(string assetId, string clipId, string name, string path, ClipType type, double duration, int? width, int? height, string? textContent = null)
    {
        AssetId = assetId;
        ClipId = clipId;
        Name = name;
        Path = path;
        Type = type;
        Duration = duration;
        Width = width;
        Height = height;
        TextContent = textContent;
    }

    public string AssetId { get; set; }
    public string ClipId { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public ClipType Type { get; set; }
    public double Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? TextContent { get; set; }
    public string DurationLabel => Duration <= 0 ? "duration unknown" : TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
