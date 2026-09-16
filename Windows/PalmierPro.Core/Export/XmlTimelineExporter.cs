using System.Text;
using System.Xml.Linq;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Models;

namespace PalmierPro.Core.Export;

public enum TimelineXmlFormat
{
    Xmeml,
    Fcpxml
}

public static class XmlTimelineExporter
{
    public static async Task ExportAsync(
        ProjectSnapshot snapshot,
        string projectRoot,
        string outputPath,
        TimelineXmlFormat format,
        CancellationToken cancellationToken = default)
    {
        var xml = Render(snapshot, projectRoot, format);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, xml, new UTF8Encoding(false), cancellationToken);
    }

    public static string Render(ProjectSnapshot snapshot, string projectRoot, TimelineXmlFormat format)
    {
        var timeline = snapshot.Project.Timelines.FirstOrDefault(item => item.Id == snapshot.Project.ActiveTimelineId)
            ?? snapshot.Project.Timelines.FirstOrDefault()
            ?? throw new InvalidDataException("The project has no timeline.");
        var assets = ResolveAssets(snapshot, timeline, projectRoot);
        return format switch
        {
            TimelineXmlFormat.Xmeml => RenderXmeml(timeline, assets),
            TimelineXmlFormat.Fcpxml => RenderFcpxml(timeline, assets),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static Dictionary<string, ResolvedAsset> ResolveAssets(ProjectSnapshot snapshot, Timeline timeline, string projectRoot)
    {
        var result = new Dictionary<string, ResolvedAsset>(StringComparer.Ordinal);
        foreach (var clip in timeline.Tracks.SelectMany(track => track.Clips).Where(clip => clip.MediaType is not ClipType.Text and not ClipType.Subtitle))
        {
            if (result.ContainsKey(clip.MediaRef)) continue;
            var entry = snapshot.Manifest.Entries.FirstOrDefault(item => item.Id == clip.MediaRef)
                ?? throw new InvalidDataException($"Media asset '{clip.MediaRef}' is missing from the manifest.");
            var path = entry.Source switch
            {
                MediaSource.External external => Path.GetFullPath(external.AbsolutePath),
                MediaSource.Project project => Path.GetFullPath(Path.Combine(projectRoot, project.RelativePath)),
                _ => throw new InvalidDataException($"Media asset '{entry.Name}' has no supported source.")
            };
            if (!File.Exists(path)) throw new FileNotFoundException("The export source is offline.", path);
            result.Add(clip.MediaRef, new ResolvedAsset(entry, path, new Uri(path).AbsoluteUri));
        }
        return result;
    }

    private static string RenderFcpxml(Timeline timeline, IReadOnlyDictionary<string, ResolvedAsset> assets)
    {
        var fps = timeline.Fps;
        var duration = Rational(timeline.TotalFrames, fps);
        var root = new XElement("fcpxml", new XAttribute("version", "1.10"));
        var resources = new XElement("resources");
        resources.Add(new XElement("format",
            new XAttribute("id", "r-format"),
            new XAttribute("frameDuration", Rational(1, fps)),
            new XAttribute("width", timeline.Width),
            new XAttribute("height", timeline.Height)));
        foreach (var (id, asset) in assets)
        {
            resources.Add(new XElement("asset",
                new XAttribute("id", "r-" + id),
                new XAttribute("name", asset.Entry.Name),
                new XAttribute("src", asset.Uri),
                new XAttribute("start", "0s"),
                new XAttribute("duration", Seconds(asset.Entry.Duration)),
                new XAttribute("hasVideo", asset.Entry.Type is ClipType.Video or ClipType.Image ? "1" : "0"),
                new XAttribute("hasAudio", asset.Entry.HasAudio == true ? "1" : "0"),
                new XAttribute("format", "r-format")));
        }
        root.Add(resources);
        var spine = new XElement("spine");
        foreach (var (track, trackIndex) in timeline.Tracks.Select((track, index) => (track, index)))
        {
            foreach (var clip in track.Clips.OrderBy(clip => clip.StartFrame))
            {
                if (!assets.TryGetValue(clip.MediaRef, out var asset)) continue;
                var node = new XElement("asset-clip",
                    new XAttribute("name", asset.Entry.Name),
                    new XAttribute("ref", "r-" + clip.MediaRef),
                    new XAttribute("offset", Rational(clip.StartFrame, fps)),
                    new XAttribute("start", Rational(clip.TrimStartFrame, fps)),
                    new XAttribute("duration", Rational(clip.DurationFrames, fps)));
                if (trackIndex > 0) node.Add(new XAttribute("lane", trackIndex));
                if (clip.Volume != 1) node.Add(new XAttribute("volume", clip.Volume.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)));
                spine.Add(node);
            }
        }
        root.Add(new XElement("library",
            new XElement("event", new XAttribute("name", timeline.Name),
                new XElement("project", new XAttribute("name", timeline.Name),
                    new XElement("sequence",
                        new XAttribute("format", "r-format"),
                        new XAttribute("duration", duration),
                        spine)))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).ToString(SaveOptions.DisableFormatting);
    }

    private static string RenderXmeml(Timeline timeline, IReadOnlyDictionary<string, ResolvedAsset> assets)
    {
        var videoTracks = timeline.Tracks.Where(track => track.Type is ClipType.Video or ClipType.Image).Select(track => new XElement("track", track.Clips.OrderBy(clip => clip.StartFrame).Select(clip => XmemlClip(clip, timeline, assets))));
        var audioTracks = timeline.Tracks.Where(track => track.Type == ClipType.Audio).Select(track => new XElement("track", track.Clips.OrderBy(clip => clip.StartFrame).Select(clip => XmemlClip(clip, timeline, assets))));
        var video = new XElement("video",
            new XElement("format", new XElement("samplecharacteristics", Rate(timeline.Fps), new XElement("width", timeline.Width), new XElement("height", timeline.Height))),
            videoTracks);
        var audio = new XElement("audio", audioTracks);
        var sequence = new XElement("sequence",
            new XAttribute("id", timeline.Id),
            new XElement("name", timeline.Name),
            new XElement("duration", timeline.TotalFrames),
            Rate(timeline.Fps),
            new XElement("media", video, audio));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement("xmeml", new XAttribute("version", "5"), sequence)).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement XmemlClip(Clip clip, Timeline timeline, IReadOnlyDictionary<string, ResolvedAsset> assets)
    {
        if (!assets.TryGetValue(clip.MediaRef, out var asset)) return new XElement("clipitem");
        var start = clip.StartFrame;
        var end = clip.EndFrame;
        var sourceIn = clip.TrimStartFrame;
        return new XElement("clipitem",
            new XAttribute("id", "clip-" + clip.Id),
            new XElement("name", asset.Entry.Name),
            new XElement("start", start),
            new XElement("end", end),
            new XElement("in", sourceIn),
            new XElement("out", sourceIn + clip.DurationFrames),
            new XElement("file",
                new XAttribute("id", "file-" + clip.MediaRef),
                new XElement("name", asset.Entry.Name),
                new XElement("pathurl", asset.Uri),
                new XElement("duration", Math.Max(1, (int)Math.Round(asset.Entry.Duration * timeline.Fps)))));
    }

    private static string Rational(int frames, int fps) => $"{frames}/{fps}s";
    private static string Seconds(double seconds) => $"{seconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}s";
    private static XElement Rate(int fps) => new("rate", new XElement("timebase", fps), new XElement("ntsc", "FALSE"));

    private sealed record ResolvedAsset(MediaManifestEntry Entry, string Path, string Uri);
}
