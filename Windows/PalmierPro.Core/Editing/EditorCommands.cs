using PalmierPro.Core.Models;

namespace PalmierPro.Core.Editing;

public interface IEditorCommand
{
    string Name { get; }
    void Apply(ProjectFile project);

    void Apply(ProjectFile project, MediaManifest manifest) => Apply(project);
}

public sealed record AddTimelineCommand(string TimelineName, int Fps = 30, int Width = 1920, int Height = 1080) : IEditorCommand
{
    public string Name => "Add timeline";

    public void Apply(ProjectFile project)
    {
        if (string.IsNullOrWhiteSpace(TimelineName) || TimelineName.Length > 120) throw new ArgumentException("Timeline name is invalid.");
        if (Fps is < 1 or > 240 || Width is < 1 or > 16_384 || Height is < 1 or > 16_384) throw new ArgumentOutOfRangeException(nameof(Fps));
        var timeline = new Timeline { Name = TimelineName.Trim(), Fps = Fps, Width = Width, Height = Height };
        project.Timelines.Add(timeline);
        project.ActiveTimelineId = timeline.Id;
        project.OpenTimelineIds = [.. (project.OpenTimelineIds ?? []), timeline.Id];
    }
}

public sealed record AddTrackCommand(string TimelineId, ClipType Type, string? TrackName = null) : IEditorCommand
{
    public string Name => "Add track";

    public void Apply(ProjectFile project)
    {
        var timeline = EditorCommandHelpers.FindTimeline(project, TimelineId);
        if (TrackName is { Length: > 80 }) throw new ArgumentException("Track name is too long.");
        timeline.Tracks.Add(new Track { Type = Type, Name = string.IsNullOrWhiteSpace(TrackName) ? null : TrackName.Trim() });
    }
}

public sealed record InsertClipCommand(string TimelineId, string TrackId, Clip Clip) : IEditorCommand
{
    public string Name => "Insert clip";

    public void Apply(ProjectFile project)
    {
        if (Clip.StartFrame < 0 || Clip.DurationFrames <= 0 || !double.IsFinite(Clip.Speed) || Clip.Speed <= 0)
            throw new ArgumentException("Clip timing is invalid.");
        var track = EditorCommandHelpers.FindTrack(project, TimelineId, TrackId);
        if (track.Clips.Any(existing => existing.Id == Clip.Id)) throw new InvalidOperationException("Clip already exists.");
        if (!Clip.MediaType.IsCompatibleWith(track.Type)) throw new InvalidOperationException("Clip type does not match track type.");
        track.Clips.Add(Clip.Clone());
        track.Clips.Sort((left, right) => left.StartFrame.CompareTo(right.StartFrame));
    }
}

public sealed record MoveClipCommand(string TimelineId, string ClipId, int NewStartFrame, string? NewTrackId = null) : IEditorCommand
{
    public string Name => "Move clip";

    public void Apply(ProjectFile project)
    {
        if (NewStartFrame < 0) throw new ArgumentOutOfRangeException(nameof(NewStartFrame));
        var source = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out var sourceTrack);
        var targetTrack = NewTrackId is null ? sourceTrack : EditorCommandHelpers.FindTrack(project, TimelineId, NewTrackId);
        if (!source.MediaType.IsCompatibleWith(targetTrack.Type)) throw new InvalidOperationException("Clip type does not match track type.");
        sourceTrack.Clips.Remove(source);
        source.StartFrame = NewStartFrame;
        targetTrack.Clips.Add(source);
        targetTrack.Clips.Sort((left, right) => left.StartFrame.CompareTo(right.StartFrame));
    }
}

public sealed record TrimClipCommand(string TimelineId, string ClipId, int NewDurationFrames, int? NewTrimStartFrame = null) : IEditorCommand
{
    public string Name => "Trim clip";

    public void Apply(ProjectFile project)
    {
        if (NewDurationFrames <= 0) throw new ArgumentOutOfRangeException(nameof(NewDurationFrames));
        var clip = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out _);
        var newTrimStart = NewTrimStartFrame ?? clip.TrimStartFrame;
        if (newTrimStart < 0) throw new ArgumentOutOfRangeException(nameof(NewTrimStartFrame));
        clip.DurationFrames = NewDurationFrames;
        clip.TrimStartFrame = newTrimStart;
    }
}

public sealed record RemoveClipCommand(string TimelineId, string ClipId) : IEditorCommand
{
    public string Name => "Remove clip";

    public void Apply(ProjectFile project)
    {
        var clip = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out var track);
        track.Clips.Remove(clip);
    }
}

public sealed record ImportMediaCommand(
    string TimelineId,
    string TrackId,
    Clip Clip,
    MediaManifestEntry Asset) : IEditorCommand
{
    public string Name => "Import media";

    public void Apply(ProjectFile project) => throw new InvalidOperationException("Import media requires a manifest.");

    public void Apply(ProjectFile project, MediaManifest manifest)
    {
        new InsertClipCommand(TimelineId, TrackId, Clip).Apply(project);
        if (manifest.Entries.Any(entry => entry.Id == Asset.Id))
            throw new InvalidOperationException($"Media asset '{Asset.Id}' already exists.");
        manifest.Entries.Add(Asset);
    }
}

public sealed record RemoveMediaCommand(string TimelineId, string ClipId, string AssetId) : IEditorCommand
{
    public string Name => "Remove media";

    public void Apply(ProjectFile project) => throw new InvalidOperationException("Remove media requires a manifest.");

    public void Apply(ProjectFile project, MediaManifest manifest)
    {
        new RemoveClipCommand(TimelineId, ClipId).Apply(project);
        manifest.Entries.RemoveAll(entry => entry.Id == AssetId);
    }
}

public sealed record SplitClipCommand(string TimelineId, string ClipId, int AtFrame) : IEditorCommand
{
    public string Name => "Split clip";

    public void Apply(ProjectFile project)
    {
        var clip = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out var track);
        if (AtFrame <= clip.StartFrame || AtFrame >= clip.EndFrame)
            throw new ArgumentOutOfRangeException(nameof(AtFrame), "The split point must be inside the clip.");

        var left = clip.DurationFrames;
        clip.DurationFrames = AtFrame - clip.StartFrame;
        var right = clip.Clone();
        right.Id = Guid.NewGuid().ToString();
        right.StartFrame = AtFrame;
        right.DurationFrames = left - clip.DurationFrames;
        track.Clips.Add(right);
        track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
    }
}

public static class EditorCommandHelpers
{
    public static Timeline FindTimeline(ProjectFile project, string id) => project.Timelines.FirstOrDefault(timeline => timeline.Id == id)
        ?? throw new KeyNotFoundException($"Timeline '{id}' was not found.");

    public static Track FindTrack(ProjectFile project, string timelineId, string trackId) => FindTimeline(project, timelineId).Tracks.FirstOrDefault(track => track.Id == trackId)
        ?? throw new KeyNotFoundException($"Track '{trackId}' was not found.");

    public static Clip FindClip(ProjectFile project, string timelineId, string clipId, out Track track)
    {
        foreach (var candidate in FindTimeline(project, timelineId).Tracks)
        {
            var clip = candidate.Clips.FirstOrDefault(item => item.Id == clipId);
            if (clip is not null) { track = candidate; return clip; }
        }
        throw new KeyNotFoundException($"Clip '{clipId}' was not found.");
    }
}

public static class ClipTypeExtensions
{
    public static bool IsCompatibleWith(this ClipType clipType, ClipType trackType) =>
        clipType == trackType || (clipType is not ClipType.Audio and not ClipType.Subtitle && trackType is not ClipType.Audio and not ClipType.Subtitle);
}
