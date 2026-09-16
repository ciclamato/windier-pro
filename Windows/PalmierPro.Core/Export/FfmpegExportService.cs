using System.Diagnostics;
using System.Globalization;
using System.Text;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Models;

namespace PalmierPro.Core.Export;

public sealed record ExportProfile(string Id, string Extension, string VideoCodec, string AudioCodec, bool SupportsAlpha = false);

public static class ExportProfiles
{
    public static readonly ExportProfile H264 = new("h264", ".mp4", "libx264", "aac");
    public static readonly ExportProfile ProRes = new("prores", ".mov", "prores_ks", "pcm_s16le");
}

public sealed record ExportPreflight(bool IsSupported, string? Message, int ClipCount, int FrameCount);

public sealed class FfmpegExportService
{
    public FfmpegExportService(string executable = "ffmpeg") => Executable = executable;
    public string Executable { get; }

    public static ExportPreflight Preflight(ProjectSnapshot snapshot)
    {
        var timeline = snapshot.Project.Timelines.FirstOrDefault(t => t.Id == snapshot.Project.ActiveTimelineId) ?? snapshot.Project.Timelines.FirstOrDefault();
        if (timeline is null) return new ExportPreflight(false, "The project has no timeline.", 0, 0);
        var clips = timeline.Tracks.Where(track => track.Type == ClipType.Video).SelectMany(track => track.Clips).OrderBy(clip => clip.StartFrame).ToArray();
        if (clips.Length == 0) return new ExportPreflight(false, "The active timeline has no video clips.", 0, 0);
        if (timeline.Fps is < 1 or > 240 || timeline.Width is < 1 or > 16_384 || timeline.Height is < 1 or > 16_384)
            return new ExportPreflight(false, "The active timeline has invalid output settings.", clips.Length, timeline.TotalFrames);
        return new ExportPreflight(true, null, clips.Length, timeline.TotalFrames);
    }

    public async Task ExportAsync(ProjectSnapshot snapshot, string projectRoot, string outputPath, ExportProfile profile, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(outputPath)) throw new IOException($"Export target '{outputPath}' already exists.");
        var preflight = Preflight(snapshot);
        if (!preflight.IsSupported) throw new NotSupportedException(preflight.Message);
        var timeline = snapshot.Project.Timelines.First(t => t.Id == snapshot.Project.ActiveTimelineId);
        var visualClips = timeline.Tracks.Where(track => track.Type is ClipType.Video or ClipType.Image)
            .SelectMany(track => track.Clips)
            .Where(clip => clip.DurationFrames > 0)
            .OrderBy(clip => clip.StartFrame)
            .ToArray();
        var audioClips = timeline.Tracks.Where(track => track.Type == ClipType.Audio)
            .SelectMany(track => track.Clips)
            .Where(clip => clip.DurationFrames > 0)
            .OrderBy(clip => clip.StartFrame)
            .ToArray();
        var allClips = visualClips.Concat(audioClips).ToArray();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var clip in allClips)
        {
            var entry = snapshot.Manifest.Entries.FirstOrDefault(item => item.Id == clip.MediaRef)
                ?? throw new InvalidDataException($"Media asset '{clip.MediaRef}' is missing from the manifest.");
            var path = ResolveMediaPath(entry, projectRoot);
            if (!File.Exists(path)) throw new FileNotFoundException("The export source is offline.", path);
            paths[clip.MediaRef] = path;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var mediaByRef = snapshot.Manifest.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var inputRefs = allClips.Select(clip => clip.MediaRef).Distinct(StringComparer.Ordinal).ToArray();
        var inputIndex = inputRefs.Select((mediaRef, index) => (mediaRef, index)).ToDictionary(item => item.mediaRef, item => item.index, StringComparer.Ordinal);
        var filter = BuildFilterGraph(timeline, visualClips, audioClips, inputIndex, mediaByRef);
        var totalSeconds = Math.Max(1, timeline.TotalFrames) / (double)timeline.Fps;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Executable,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-n");
        foreach (var mediaRef in inputRefs)
        {
            var entry = mediaByRef[mediaRef];
            if (entry.Type == ClipType.Image) process.StartInfo.ArgumentList.Add("-loop");
            if (entry.Type == ClipType.Image) process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(paths[mediaRef]);
        }
        process.StartInfo.ArgumentList.Add("-filter_complex");
        process.StartInfo.ArgumentList.Add(filter);
        process.StartInfo.ArgumentList.Add("-map");
        process.StartInfo.ArgumentList.Add("[vout]");
        if (filter.Contains("[aout]", StringComparison.Ordinal))
        {
            process.StartInfo.ArgumentList.Add("-map");
            process.StartInfo.ArgumentList.Add("[aout]");
        }
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add(totalSeconds.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-c:v");
        process.StartInfo.ArgumentList.Add(profile.VideoCodec);
        process.StartInfo.ArgumentList.Add("-pix_fmt");
        process.StartInfo.ArgumentList.Add("yuv420p");
        if (filter.Contains("[aout]", StringComparison.Ordinal))
        {
            process.StartInfo.ArgumentList.Add("-c:a");
            process.StartInfo.ArgumentList.Add(profile.AudioCodec);
        }
        process.StartInfo.ArgumentList.Add("-movflags");
        process.StartInfo.ArgumentList.Add("+faststart");
        process.StartInfo.ArgumentList.Add(outputPath);
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Executable}. Install FFmpeg or configure its path.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            throw new InvalidDataException(error.Trim());
        }
        progress?.Report(1);
    }

    private static string ResolveMediaPath(MediaManifestEntry entry, string projectRoot) => entry.Source switch
    {
        MediaSource.External external => Path.GetFullPath(external.AbsolutePath),
        MediaSource.Project project => Path.GetFullPath(Path.Combine(projectRoot, project.RelativePath)),
        _ => throw new InvalidDataException("Media asset has no supported source.")
    };

    private static string BuildFilterGraph(
        Timeline timeline,
        IReadOnlyList<Clip> visualClips,
        IReadOnlyList<Clip> audioClips,
        IReadOnlyDictionary<string, int> inputIndex,
        IReadOnlyDictionary<string, MediaManifestEntry> mediaByRef)
    {
        var graph = new StringBuilder();
        var totalSeconds = Math.Max(1, timeline.TotalFrames) / (double)timeline.Fps;
        graph.Append($"color=c=black:s={timeline.Width}x{timeline.Height}:r={timeline.Fps}:d={Fmt(totalSeconds)}[canvas];");
        var canvas = "canvas";
        for (var index = 0; index < visualClips.Count; index++)
        {
            var clip = visualClips[index];
            var input = inputIndex[clip.MediaRef];
            var start = clip.StartFrame / (double)timeline.Fps;
            var sourceStart = clip.TrimStartFrame / (double)timeline.Fps;
            var sourceDuration = clip.SourceFramesConsumed / (double)timeline.Fps;
            var duration = clip.DurationFrames / (double)timeline.Fps;
            var label = $"v{index}";
            graph.Append($"[{input}:v]trim=start={Fmt(sourceStart)}:duration={Fmt(sourceDuration)},setpts=PTS-STARTPTS+{Fmt(start)}/TB,");
            graph.Append($"scale={timeline.Width}:{timeline.Height}:force_original_aspect_ratio=decrease,pad={timeline.Width}:{timeline.Height}:(ow-iw)/2:(oh-ih)/2:color=black");
            if (clip.Opacity < .999) graph.Append($",colorchannelmixer=aa={Fmt(Math.Clamp(clip.Opacity, 0, 1))}");
            graph.Append($"[{label}];");
            var nextCanvas = $"canvas{index}";
            graph.Append($"[{canvas}][{label}]overlay=0:0:format=auto:eof_action=pass:shortest=0[{nextCanvas}];");
            canvas = nextCanvas;
        }
        graph.Append($"[{canvas}]format=yuv420p[vout];");

        var audioLabels = new List<string>();
        foreach (var (clip, index) in audioClips.Select((clip, index) => (clip, index)))
        {
            var entry = mediaByRef[clip.MediaRef];
            if (entry.HasAudio != true) continue;
            var input = inputIndex[clip.MediaRef];
            var start = clip.StartFrame / (double)timeline.Fps;
            var sourceStart = clip.TrimStartFrame / (double)timeline.Fps;
            var sourceDuration = clip.SourceFramesConsumed / (double)timeline.Fps;
            var label = $"a{index}";
            graph.Append($"[{input}:a:0]atrim=start={Fmt(sourceStart)}:duration={Fmt(sourceDuration)},asetpts=PTS-STARTPTS+{Fmt(start)}/TB");
            if (Math.Abs(clip.Volume - 1) > .001) graph.Append($",volume={Fmt(Math.Max(0, clip.Volume))}");
            graph.Append($"[{label}];");
            audioLabels.Add(label);
        }
        foreach (var (clip, index) in visualClips.Select((clip, index) => (clip, index)))
        {
            var entry = mediaByRef[clip.MediaRef];
            if (entry.HasAudio != true) continue;
            var input = inputIndex[clip.MediaRef];
            var start = clip.StartFrame / (double)timeline.Fps;
            var sourceStart = clip.TrimStartFrame / (double)timeline.Fps;
            var sourceDuration = clip.SourceFramesConsumed / (double)timeline.Fps;
            var label = $"av{index}";
            graph.Append($"[{input}:a:0]atrim=start={Fmt(sourceStart)}:duration={Fmt(sourceDuration)},asetpts=PTS-STARTPTS+{Fmt(start)}/TB");
            if (Math.Abs(clip.Volume - 1) > .001) graph.Append($",volume={Fmt(Math.Max(0, clip.Volume))}");
            graph.Append($"[{label}];");
            audioLabels.Add(label);
        }
        if (audioLabels.Count > 0)
            graph.Append(string.Concat(audioLabels.Select(label => $"[{label}]"))).Append($"amix=inputs={audioLabels.Count}:duration=longest:dropout_transition=0[aout];");
        return graph.ToString().TrimEnd(';');
    }

    private static string Fmt(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed record ExportJob(string Id, string OutputPath, string ProfileId, string State, string? Error = null);

public sealed class ExportQueue
{
    private readonly FfmpegExportService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ExportJob> _jobs = [];

    public ExportQueue(FfmpegExportService? service = null) => _service = service ?? new FfmpegExportService();
    public IReadOnlyList<ExportJob> Jobs => _jobs;

    public async Task<ExportJob> EnqueueAsync(ProjectSnapshot snapshot, string projectRoot, string outputPath, ExportProfile profile, CancellationToken cancellationToken = default)
    {
        var job = new ExportJob(Guid.NewGuid().ToString(), outputPath, profile.Id, "queued");
        _jobs.Add(job);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Replace(job with { State = "running" });
            try
            {
                await _service.ExportAsync(snapshot, projectRoot, outputPath, profile, cancellationToken: cancellationToken);
                job = job with { State = "complete" };
            }
            catch (Exception error) { job = job with { State = "failed", Error = error.Message }; }
            Replace(job);
            return job;
        }
        finally { _gate.Release(); }
    }

    private void Replace(ExportJob job)
    {
        var index = _jobs.FindIndex(item => item.Id == job.Id);
        if (index >= 0) _jobs[index] = job;
    }
}
