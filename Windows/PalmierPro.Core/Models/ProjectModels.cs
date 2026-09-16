using System.Text.Json;
using System.Text.Json.Serialization;
using PalmierPro.Core.Serialization;

namespace PalmierPro.Core.Models;

public enum ClipType
{
    Video, Audio, Image, Text, Lottie, Sequence, Subtitle
}

public enum Interpolation
{
    Linear, Hold, Smooth
}

public enum BlendMode
{
    Normal, Darken, Multiply, ColorBurn, Lighten, Screen, ColorDodge,
    Overlay, SoftLight, HardLight, Difference, Exclusion,
    Hue, Saturation, Color, Luminosity
}

public abstract record MediaSource
{
    public sealed record External(string AbsolutePath) : MediaSource;
    public sealed record Project(string RelativePath) : MediaSource;
}

public sealed class ProjectFile
{
    public List<Timeline> Timelines { get; set; } = [];
    public string? ActiveTimelineId { get; set; }
    public List<string>? OpenTimelineIds { get; set; }
    public Dictionary<string, TimelineViewState>? ViewStates { get; set; }
    public List<SpeakerRegistryEntry>? Speakers { get; set; }
    public List<MulticamSource>? MulticamGroups { get; set; }

    public static ProjectFile CreateDefault() 
    {
        var timeline = new Timeline
        {
            Tracks =
            [
                new Track { Type = ClipType.Video, Name = "V1" },
                new Track { Type = ClipType.Audio, Name = "A1" }
            ]
        };
        return new ProjectFile
        {
            Timelines = [timeline],
            ActiveTimelineId = timeline.Id,
            OpenTimelineIds = [timeline.Id]
        };
    }

    public static ProjectFile Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            var file = JsonSerializer.Deserialize<ProjectFile>(data, PalmierJson.Options);
            if (file is { Timelines.Count: > 0 }) return file;
        }
        catch (JsonException)
        {
        }

        var legacy = JsonSerializer.Deserialize<Timeline>(data, PalmierJson.Options)
            ?? throw new JsonException("The project contains no timelines.");
        return new ProjectFile
        {
            Timelines = [legacy],
            ActiveTimelineId = legacy.Id,
            OpenTimelineIds = [legacy.Id]
        };
    }

    public ProjectFile Clone() => JsonSerializer.Deserialize<ProjectFile>(JsonSerializer.Serialize(this, PalmierJson.Options), PalmierJson.Options)!;
}

public sealed class TimelineViewState
{
    public int PlayheadFrame { get; set; }
    public double ZoomScale { get; set; } = 4;
    public double ScrollOffsetX { get; set; }
}

public sealed class Timeline
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Timeline 1";
    public int Fps { get; set; } = 30;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool SettingsConfigured { get; set; }
    public string? FolderId { get; set; }
    public List<Track> Tracks { get; set; } = [];
    public List<TimelineMarker> Markers { get; set; } = [];

    [JsonIgnore]
    public int TotalFrames => Tracks.Count == 0 ? 0 : Tracks.Max(track => track.EndFrame);

    [JsonIgnore]
    public int DisplayFrames => Markers.Count == 0 ? TotalFrames : Math.Max(TotalFrames, Markers.Max(marker => marker.StartFrame + Math.Max(1, marker.DurationFrames)));
}

public sealed class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public ClipType Type { get; set; }
    public string? Name { get; set; }
    public bool Muted { get; set; }
    public bool Hidden { get; set; }
    public bool SyncLocked { get; set; } = true;
    public List<Clip> Clips { get; set; } = [];

    [JsonIgnore]
    public int EndFrame => Clips.Count == 0 ? 0 : Clips.Max(clip => clip.EndFrame);
}

public sealed class Clip
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MediaRef { get; set; } = string.Empty;
    public ClipType MediaType { get; set; } = ClipType.Video;
    public ClipType SourceClipType { get; set; } = ClipType.Video;
    public int StartFrame { get; set; }
    public int DurationFrames { get; set; }
    public int TrimStartFrame { get; set; }
    public int TrimEndFrame { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public int FadeInFrames { get; set; }
    public int FadeOutFrames { get; set; }
    public Interpolation FadeInInterpolation { get; set; } = Interpolation.Linear;
    public Interpolation FadeOutInterpolation { get; set; } = Interpolation.Linear;
    public double Opacity { get; set; } = 1;
    public Transform Transform { get; set; } = new();
    public Crop Crop { get; set; } = new();
    public double EdgeRounding { get; set; }
    public double EdgeSoftness { get; set; }
    public string? LinkGroupId { get; set; }
    public string? CaptionGroupId { get; set; }
    public string? MulticamGroupId { get; set; }
    public string? TextContent { get; set; }
    public JsonElement? TextStyle { get; set; }
    public JsonElement? TextAnimation { get; set; }
    public List<WordTiming>? WordTimings { get; set; }
    public string? TextFillMode { get; set; }
    public KeyframeTrack<double>? OpacityTrack { get; set; }
    public KeyframeTrack<AnimPair>? PositionTrack { get; set; }
    public KeyframeTrack<AnimPair>? ScaleTrack { get; set; }
    public KeyframeTrack<double>? RotationTrack { get; set; }
    public KeyframeTrack<Crop>? CropTrack { get; set; }
    public KeyframeTrack<double>? VolumeTrack { get; set; }
    public KeyframeTrack<double>? BlurKeyframeTrack { get; set; }
    public List<Effect>? Effects { get; set; }
    public BlendMode? BlendMode { get; set; }

    [JsonIgnore]
    public int EndFrame => checked(StartFrame + DurationFrames);

    [JsonIgnore]
    public int SourceFramesConsumed => (int)Math.Round(DurationFrames * Speed, MidpointRounding.ToEven);

    public bool Contains(int frame) => frame >= StartFrame && frame < EndFrame;

    public Clip Clone() => JsonSerializer.Deserialize<Clip>(
        JsonSerializer.Serialize(this, PalmierJson.Options), PalmierJson.Options) ?? throw new InvalidOperationException("Could not clone clip.");

    public double OpacityAt(int frame)
    {
        var value = OpacityTrack?.Sample(frame - StartFrame, Opacity) ?? Opacity;
        if (MediaType == ClipType.Audio) return value;
        var fadeIn = FadeInFrames <= 0 ? 1 : Math.Clamp((double)(frame - StartFrame + 1) / FadeInFrames, 0, 1);
        var fadeOut = FadeOutFrames <= 0 ? 1 : Math.Clamp((double)(EndFrame - frame) / FadeOutFrames, 0, 1);
        return value * Math.Min(fadeIn, fadeOut);
    }
}

public sealed class KeyframeTrack<T>
{
    public List<Keyframe<T>> Keyframes { get; set; } = [];

    [JsonIgnore]
    public bool IsActive => Keyframes.Count > 0;

    public T? Sample(int frame, T? fallback)
    {
        if (Keyframes.Count == 0) return fallback;
        var ordered = Keyframes.OrderBy(keyframe => keyframe.Frame).ToArray();
        if (frame <= ordered[0].Frame) return ordered[0].Value;
        if (frame >= ordered[^1].Frame) return ordered[^1].Value;
        var rightIndex = Array.FindIndex(ordered, keyframe => keyframe.Frame > frame);
        var left = ordered[rightIndex - 1];
        var right = ordered[rightIndex];
        if (left.InterpolationOut == Interpolation.Hold) return left.Value;
        var amount = (double)(frame - left.Frame) / (right.Frame - left.Frame);
        if (left.InterpolationOut == Interpolation.Smooth) amount = amount * amount * (3 - 2 * amount);
        return Interpolate(left.Value!, right.Value!, amount);
    }

    public void Upsert(Keyframe<T> keyframe)
    {
        var index = Keyframes.FindIndex(existing => existing.Frame == keyframe.Frame);
        if (index >= 0) Keyframes[index] = keyframe;
        else Keyframes.Add(keyframe);
        Keyframes.Sort((left, right) => left.Frame.CompareTo(right.Frame));
    }

    private static T Interpolate(T from, T to, double amount)
    {
        if (typeof(T) == typeof(double))
            return (T)(object)((double)(object)from! + ((double)(object)to! - (double)(object)from!) * amount);
        if (typeof(T) == typeof(AnimPair))
        {
            var a = (AnimPair)(object)from!;
            var b = (AnimPair)(object)to!;
            return (T)(object)new AnimPair(a.A + (b.A - a.A) * amount, a.B + (b.B - a.B) * amount);
        }
        if (typeof(T) == typeof(Crop))
        {
            var a = (Crop)(object)from!;
            var b = (Crop)(object)to!;
            return (T)(object)new Crop
            {
                Left = a.Left + (b.Left - a.Left) * amount,
                Top = a.Top + (b.Top - a.Top) * amount,
                Right = a.Right + (b.Right - a.Right) * amount,
                Bottom = a.Bottom + (b.Bottom - a.Bottom) * amount
            };
        }
        return from;
    }
}

public sealed class Keyframe<T>
{
    public int Frame { get; set; }
    public T? Value { get; set; }
    public Interpolation InterpolationOut { get; set; } = Interpolation.Smooth;
}

public readonly record struct AnimPair(double A, double B);

public sealed class Transform
{
    public double CenterX { get; set; } = .5;
    public double CenterY { get; set; } = .5;
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
    public double Rotation { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
}

public sealed class Crop
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
}

public sealed class WordTiming
{
    public string Text { get; set; } = string.Empty;
    public int StartFrame { get; set; }
    public int EndFrame { get; set; }
}

public sealed class Effect
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, EffectParam> Params { get; set; } = [];
}

public sealed class EffectParam
{
    public double? Value { get; set; }
    public string? String { get; set; }
    public KeyframeTrack<double>? Track { get; set; }
}

public sealed class TimelineMarker
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public int StartFrame { get; set; }
    public int DurationFrames { get; set; }
    public JsonElement? Color { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string Status { get; set; } = "open";
}

public sealed class MediaManifest
{
    public int Version { get; set; } = 2;
    public List<MediaManifestEntry> Entries { get; set; } = [];
    public List<MediaFolder> Folders { get; set; } = [];

    public MediaManifest Clone() => JsonSerializer.Deserialize<MediaManifest>(
        JsonSerializer.Serialize(this, PalmierJson.Options), PalmierJson.Options) ?? new MediaManifest();
}

public sealed class MediaManifestEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public ClipType Type { get; set; }
    public MediaSource Source { get; set; } = new MediaSource.External(string.Empty);
    public double Duration { get; set; }
    public JsonElement? GenerationInput { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    [JsonPropertyName("sourceFPS")]
    public double? SourceFps { get; set; }
    public bool? HasAudio { get; set; }
    public string? FolderId { get; set; }
    [JsonPropertyName("cachedRemoteURL")]
    public string? CachedRemoteUrl { get; set; }
    [JsonPropertyName("cachedRemoteURLExpiresAt")]
    public DateTimeOffset? CachedRemoteUrlExpiresAt { get; set; }
    public string? GenerationStatus { get; set; }
    public JsonElement? ImportInput { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public sealed class MediaFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? ParentFolderId { get; set; }
}

public sealed class SpeakerRegistryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public JsonElement? Value { get; set; }
}

public sealed class MulticamSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public List<MulticamMember> Members { get; set; } = [];
    public string MasterMemberId { get; set; } = string.Empty;
}

public sealed class MulticamMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MediaRef { get; set; } = string.Empty;
    public string Kind { get; set; } = "both";
    public string AngleLabel { get; set; } = string.Empty;
    public MulticamSync Sync { get; set; } = new();
}

public sealed class MulticamSync
{
    public double OffsetSeconds { get; set; }
    public double Confidence { get; set; }
    public bool Locked { get; set; }
}
