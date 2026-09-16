using System.IO;
using System.Text.Json;

namespace PalmierPro.Windows.Models;

public sealed class WindowsProject
{
    public List<string> Media { get; set; } = [];

    public static async Task<WindowsProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WindowsProject>(stream, cancellationToken: cancellationToken)
            ?? new WindowsProject();
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }
}
