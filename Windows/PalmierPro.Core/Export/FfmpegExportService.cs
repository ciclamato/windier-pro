using System.Diagnostics;
using System.Globalization;
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
        if (clips.Length > 1) return new ExportPreflight(false, "The baseline export adapter requires one video clip; the native render graph will add multi-clip compositing.", clips.Length, timeline.TotalFrames);
        return new ExportPreflight(true, null, 1, timeline.TotalFrames);
    }

    public async Task ExportAsync(ProjectSnapshot snapshot, string projectRoot, string outputPath, ExportProfile profile, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(outputPath)) throw new IOException($"Export target '{outputPath}' already exists.");
        var preflight = Preflight(snapshot);
        if (!preflight.IsSupported) throw new NotSupportedException(preflight.Message);
        var timeline = snapshot.Project.Timelines.First(t => t.Id == snapshot.Project.ActiveTimelineId);
        var clip = timeline.Tracks.First(track => track.Type == ClipType.Video).Clips.OrderBy(item => item.StartFrame).First();
        var entry = snapshot.Manifest.Entries.FirstOrDefault(item => item.Id == clip.MediaRef) ?? throw new InvalidDataException($"Media asset '{clip.MediaRef}' is missing from the manifest.");
        var inputPath = entry.Source switch
        {
            MediaSource.External external => external.AbsolutePath,
            MediaSource.Project project => Path.GetFullPath(Path.Combine(projectRoot, project.RelativePath)),
            _ => throw new InvalidDataException("Media asset has no supported source.")
        };
        if (!File.Exists(inputPath)) throw new FileNotFoundException("The export source is offline.", inputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var startSeconds = (clip.TrimStartFrame / (double)timeline.Fps).ToString(CultureInfo.InvariantCulture);
        var durationSeconds = (clip.DurationFrames / (double)timeline.Fps).ToString(CultureInfo.InvariantCulture);
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
        foreach (var argument in new[] { "-hide_banner", "-n", "-ss", startSeconds, "-i", inputPath, "-t", durationSeconds, "-map", "0:v:0", "-map", "0:a?", "-c:v", profile.VideoCodec, "-pix_fmt", "yuv420p", "-c:a", profile.AudioCodec, "-movflags", "+faststart", outputPath }) process.StartInfo.ArgumentList.Add(argument);
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
