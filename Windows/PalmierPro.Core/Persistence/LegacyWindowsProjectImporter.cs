using System.Text.Json;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Models;

namespace PalmierPro.Core.Persistence;

/// <summary>
/// One-time importer for the disposable WPF prototype's flat { media: [...] } format.
/// New saves never emit this format.
/// </summary>
public static class LegacyWindowsProjectImporter
{
    public static async Task<ProjectSnapshot> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("This is not a Palmier WPF prototype project.");
        var project = ProjectFile.CreateDefault();
        var timeline = project.Timelines[0];
        var track = timeline.Tracks.First(candidate => candidate.Type == ClipType.Video);
        var manifest = new MediaManifest();
        foreach (var value in media.EnumerateArray())
        {
            var sourcePath = value.GetString();
            if (string.IsNullOrWhiteSpace(sourcePath)) continue;
            var id = Guid.NewGuid().ToString();
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            var type = extension is ".mp3" or ".wav" or ".m4a" ? ClipType.Audio : extension is ".png" or ".jpg" or ".jpeg" ? ClipType.Image : ClipType.Video;
            if (type == ClipType.Audio)
            {
                track = timeline.Tracks.First(candidate => candidate.Type == ClipType.Audio);
            }
            else if (track.Type != ClipType.Video)
            {
                track = timeline.Tracks.First(candidate => candidate.Type == ClipType.Video);
            }
            var duration = type == ClipType.Image ? 5 : 10;
            var clip = new Clip { MediaRef = id, MediaType = type, SourceClipType = type, StartFrame = track.EndFrame, DurationFrames = timeline.Fps * duration };
            track.Clips.Add(clip);
            manifest.Entries.Add(new MediaManifestEntry
            {
                Id = id,
                Name = Path.GetFileName(sourcePath),
                Type = type,
                Duration = duration,
                Source = new MediaSource.External(Path.GetFullPath(sourcePath))
            });
        }
        return new ProjectSnapshot(project, manifest);
    }
}
