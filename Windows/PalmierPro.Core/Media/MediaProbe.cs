using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PalmierPro.Core.Media;

public sealed record MediaMetadata(
    string Path,
    double DurationSeconds,
    int? Width,
    int? Height,
    double? FrameRate,
    bool HasVideo,
    bool HasAudio,
    string? VideoCodec,
    string? AudioCodec);

public interface IMediaProbe
{
    Task<MediaMetadata> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Baseline Windows media adapter. It uses the FFmpeg command line tools so the editor
/// can run on a clean machine before the optional native FFmpeg library is installed.
/// The native C++ engine can implement the same contract without changing editor code.
/// </summary>
public sealed class FfmpegMediaProbe : IMediaProbe
{
    public FfmpegMediaProbe(string executable = "ffprobe") => Executable = executable;

    public string Executable { get; }

    public async Task<MediaMetadata> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Media file was not found.", path);
        var output = await RunAsync(
            ["-v", "error", "-show_entries", "format=duration:stream=index,codec_type,codec_name,width,height,avg_frame_rate", "-of", "json", path],
            cancellationToken);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamArray) ? streamArray.EnumerateArray() : [];
        int? width = null, height = null;
        double? frameRate = null;
        string? videoCodec = null, audioCodec = null;
        var hasVideo = false;
        var hasAudio = false;
        foreach (var stream in streams)
        {
            var type = stream.TryGetProperty("codec_type", out var codecType) ? codecType.GetString() : null;
            if (type == "video")
            {
                hasVideo = true;
                videoCodec ??= stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null;
                width ??= ReadInt(stream, "width");
                height ??= ReadInt(stream, "height");
                if (frameRate is null && stream.TryGetProperty("avg_frame_rate", out var rate)) frameRate = ParseRate(rate.GetString());
            }
            else if (type == "audio")
            {
                hasAudio = true;
                audioCodec ??= stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null;
            }
        }
        var duration = 0d;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationValue))
        {
            duration = durationValue.ValueKind == JsonValueKind.Number
                ? durationValue.GetDouble()
                : double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }
        return new MediaMetadata(path, duration, width, height, frameRate, hasVideo, hasAudio, videoCodec, audioCodec);
    }

    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Executable}. Install FFmpeg or configure its path.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout;
        if (process.ExitCode != 0) throw new InvalidDataException((await stderr).Trim());
        return output;
    }

    private static int? ReadInt(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : null;

    private static double? ParseRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0") return null;
        var parts = value.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0
            ? numerator / denominator
            : null;
    }
}

public sealed class FfmpegFrameRenderer
{
    public FfmpegFrameRenderer(string executable = "ffmpeg") => Executable = executable;

    public string Executable { get; }

    public async Task<byte[]> CapturePngAsync(string path, TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Media file was not found.", path);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-ss");
        process.StartInfo.ArgumentList.Add(Math.Max(0, position.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("-frames:v");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("image2pipe");
        process.StartInfo.ArgumentList.Add("-vcodec");
        process.StartInfo.ArgumentList.Add("png");
        process.StartInfo.ArgumentList.Add("pipe:1");
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Executable}. Install FFmpeg or configure its path.");
        await using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || output.Length == 0) throw new InvalidDataException((await process.StandardError.ReadToEndAsync(cancellationToken)).Trim());
        return output.ToArray();
    }
}
