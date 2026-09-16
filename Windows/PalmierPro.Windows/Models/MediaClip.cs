using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PalmierPro.Windows.Models;

public sealed class MediaClip : INotifyPropertyChanged
{
    public MediaClip(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public string Name { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
