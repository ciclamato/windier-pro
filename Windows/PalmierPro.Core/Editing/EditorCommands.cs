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

public sealed record TrimClipCommand(
    string TimelineId,
    string ClipId,
    int NewDurationFrames,
    int? NewTrimStartFrame = null,
    int? NewStartFrame = null) : IEditorCommand
{
    public string Name => "Trim clip";

    public void Apply(ProjectFile project)
    {
        if (NewDurationFrames <= 0) throw new ArgumentOutOfRangeException(nameof(NewDurationFrames));
        var clip = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out _);
        var newTrimStart = NewTrimStartFrame ?? clip.TrimStartFrame;
        if (newTrimStart < 0) throw new ArgumentOutOfRangeException(nameof(NewTrimStartFrame));
        if (NewStartFrame is < 0) throw new ArgumentOutOfRangeException(nameof(NewStartFrame));
        clip.DurationFrames = NewDurationFrames;
        clip.TrimStartFrame = newTrimStart;
        if (NewStartFrame is int newStart) clip.StartFrame = newStart;
    }
}

public sealed record RollEditCommand(string TimelineId, string LeftClipId, string RightClipId, int NewBoundaryFrame) : IEditorCommand
{
    public string Name => "Roll edit";

    public void Apply(ProjectFile project)
    {
        var left = EditorCommandHelpers.FindClip(project, TimelineId, LeftClipId, out var leftTrack);
        var right = EditorCommandHelpers.FindClip(project, TimelineId, RightClipId, out var rightTrack);
        if (leftTrack.Id != rightTrack.Id) throw new InvalidOperationException("A roll edit requires clips on the same track.");
        if (left.EndFrame > right.StartFrame || NewBoundaryFrame <= left.StartFrame || NewBoundaryFrame >= right.EndFrame)
            throw new ArgumentOutOfRangeException(nameof(NewBoundaryFrame), "The roll boundary must remain inside both clips' combined range.");
        left.DurationFrames = NewBoundaryFrame - left.StartFrame;
        var sourceDelta = NewBoundaryFrame - right.StartFrame;
        right.StartFrame = NewBoundaryFrame;
        right.DurationFrames -= sourceDelta;
        right.TrimStartFrame = Math.Max(0, right.TrimStartFrame + sourceDelta);
        leftTrack.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
    }
}

public sealed record SlipClipCommand(string TimelineId, string ClipId, int NewTrimStartFrame) : IEditorCommand
{
    public string Name => "Slip clip";

    public void Apply(ProjectFile project)
    {
        if (NewTrimStartFrame < 0) throw new ArgumentOutOfRangeException(nameof(NewTrimStartFrame));
        var clip = EditorCommandHelpers.FindClip(project, TimelineId, ClipId, out _);
        clip.TrimStartFrame = NewTrimStartFrame;
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

public sealed record ImportSubtitleCommand(
    string TimelineId,
    string TrackId,
    IReadOnlyList<Clip> Clips,
    MediaManifestEntry Asset) : IEditorCommand
{
    public string Name => "Import captions";

    public void Apply(ProjectFile project) => throw new InvalidOperationException("Import captions requires a manifest.");

    public void Apply(ProjectFile project, MediaManifest manifest)
    {
        if (Clips.Count == 0) throw new InvalidDataException("The caption file contains no cues.");
        if (manifest.Entries.Any(entry => entry.Id == Asset.Id))
            throw new InvalidOperationException($"Media asset '{Asset.Id}' already exists.");
        var track = EditorCommandHelpers.FindTrack(project, TimelineId, TrackId);
        if (track.Type != ClipType.Subtitle) throw new InvalidOperationException("Captions must be inserted into a subtitle track.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in Clips)
        {
            if (!ids.Add(clip.Id) || clip.StartFrame < 0 || clip.DurationFrames <= 0 || clip.MediaType != ClipType.Subtitle)
                throw new InvalidDataException("The caption cues contain invalid timing or identifiers.");
            clip.MediaRef = Asset.Id;
            track.Clips.Add(clip.Clone());
        }
        track.Clips.Sort((left, right) => left.StartFrame.CompareTo(right.StartFrame));
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
