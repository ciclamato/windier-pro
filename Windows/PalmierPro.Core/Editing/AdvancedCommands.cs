using PalmierPro.Core.Models;

namespace PalmierPro.Core.Editing;

public sealed record SetClipOpacityCommand(string TimelineId, string ClipId, double Opacity) : IEditorCommand
{
    public string Name => "Set opacity";

    public void Apply(ProjectFile project)
    {
        if (!double.IsFinite(Opacity) || Opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(Opacity));
        EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out _).Opacity = Opacity;
    }
}

public sealed record SetEffectCommand(string TimelineId, string ClipId, Effect Effect) : IEditorCommand
{
    public string Name => "Set effect";

    public void Apply(ProjectFile project)
    {
        var clip = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out _);
        clip.Effects ??= [];
        var index = clip.Effects.FindIndex(effect => effect.Id == Effect.Id);
        if (index >= 0) clip.Effects[index] = Effect;
        else clip.Effects.Add(Effect);
    }
}

public sealed record AddMarkerCommand(string TimelineId, TimelineMarker Marker) : IEditorCommand
{
    public string Name => "Add marker";

    public void Apply(ProjectFile project)
    {
        if (Marker.StartFrame < 0 || Marker.DurationFrames < 0) throw new ArgumentOutOfRangeException(nameof(Marker));
        var timeline = EditorCommandHelpers.FindTimeline(project, TimelineId);
        if (timeline.Markers.Any(marker => marker.Id == Marker.Id)) throw new InvalidOperationException("Marker already exists.");
        timeline.Markers.Add(Marker);
        timeline.Markers.Sort((left, right) => left.StartFrame.CompareTo(right.StartFrame));
    }
}

public sealed record RelinkMediaCommand(string AssetId, string AbsolutePath) : IEditorCommand
{
    public string Name => "Relink media";

    public void Apply(ProjectFile project) => throw new InvalidOperationException("Relink media requires a manifest.");

    public void Apply(ProjectFile project, MediaManifest manifest)
    {
        if (!Path.IsPathFullyQualified(AbsolutePath)) throw new ArgumentException("A relink path must be absolute.", nameof(AbsolutePath));
        var entry = manifest.Entries.FirstOrDefault(item => item.Id == AssetId) ?? throw new KeyNotFoundException($"Media asset '{AssetId}' was not found.");
        entry.Source = new MediaSource.External(Path.GetFullPath(AbsolutePath));
    }
}

public sealed record RippleDeleteCommand(string TimelineId, string ClipId) : IEditorCommand
{
    public string Name => "Ripple delete";

    public void Apply(ProjectFile project)
    {
        var timeline = EditorCommandHelpers.FindTimeline(project, TimelineId);
        var clip = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out _);
        var end = clip.EndFrame;
        var amount = clip.DurationFrames;
        var link = clip.LinkGroupId;
        foreach (var track in timeline.Tracks)
        {
            track.Clips.RemoveAll(candidate => candidate.Id == ClipId || link is not null && candidate.LinkGroupId == link && candidate.StartFrame == clip.StartFrame);
            foreach (var candidate in track.Clips.Where(candidate => candidate.StartFrame >= end)) candidate.StartFrame -= amount;
        }
    }
}

public static class TimelineSearch
{
    public static IReadOnlyList<ClipSearchResult> Search(ProjectFile project, MediaManifest? manifest, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var needle = query.Trim();
        var names = (manifest?.Entries ?? []).ToDictionary(entry => entry.Id, entry => entry.Name, StringComparer.Ordinal);
        return project.Timelines.SelectMany(timeline => timeline.Tracks.SelectMany(track => track.Clips.Select(clip => new { timeline, track, clip })))
            .Where(item => item.clip.TextContent?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true
                || item.clip.MediaRef.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || names.GetValueOrDefault(item.clip.MediaRef)?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => new ClipSearchResult(item.timeline.Id, item.track.Id, item.clip.Id, names.GetValueOrDefault(item.clip.MediaRef) ?? item.clip.TextContent ?? item.clip.MediaRef))
            .ToArray();
    }
}

public sealed record ClipSearchResult(string TimelineId, string TrackId, string ClipId, string Text);
