using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PalmierPro.Windows.Models;

namespace PalmierPro.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private MediaClip? _currentClip;
    private bool _isPlaying;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<MediaClip> Clips { get; } = [];

    public string ClipSummary => Clips.Count == 0 ? "No clips" : $"{Clips.Count} clip{(Clips.Count == 1 ? "" : "s")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media files|*.mp4;*.mov;*.m4v;*.avi;*.mkv;*.webm;*.mp3;*.wav;*.m4a;*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) AddClips(dialog.FileNames);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        AddClips((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    private void AddClips(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists).Where(IsSupportedMedia))
        {
            if (Clips.Any(clip => string.Equals(clip.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            Clips.Add(new MediaClip(path));
        }

        if (_currentClip is null && Clips.Count > 0) MediaList.SelectedIndex = 0;
        OnPropertyChanged(nameof(ClipSummary));
    }

    private void RemoveClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MediaClip clip) return;
        var wasCurrent = ReferenceEquals(_currentClip, clip);
        Clips.Remove(clip);
        if (wasCurrent)
        {
            Preview.Stop();
            _isPlaying = false;
            _currentClip = null;
            EmptyState.Visibility = Visibility.Visible;
            PreviewStatus.Text = "No media selected";
        }
        OnPropertyChanged(nameof(ClipSummary));
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaClip clip) return;
        _currentClip = clip;
        Preview.Source = new Uri(clip.Path);
        Preview.Play();
        _isPlaying = true;
        PreviewStatus.Text = clip.Name;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null) return;
        if (_isPlaying)
        {
            Preview.Pause();
            _isPlaying = false;
        }
        else
        {
            Preview.Play();
            _isPlaying = true;
        }
    }

    private void Preview_MediaOpened(object sender, RoutedEventArgs e) => EmptyState.Visibility = Visibility.Collapsed;

    private void Preview_MediaEnded(object sender, RoutedEventArgs e)
    {
        Preview.Position = TimeSpan.Zero;
        _isPlaying = false;
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Palmier Windows project|*.palmier-windows.json" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var project = await WindowsProject.LoadAsync(dialog.FileName);
            Clips.Clear();
            AddClips(project.Media);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not open project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Palmier Windows project|*.palmier-windows.json", DefaultExt = ".palmier-windows.json" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await new WindowsProject { Media = Clips.Select(clip => clip.Path).ToList() }.SaveAsync(dialog.FileName);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not save project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsSupportedMedia(string path) =>
        new[] { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".mp3", ".wav", ".m4a", ".png", ".jpg", ".jpeg", ".bmp" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
