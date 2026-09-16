using System.Net.Http;
using System.Security.Cryptography;

namespace PalmierPro.Core.AI;

public sealed record LocalModelDefinition(string Id, Uri DownloadUri, string Sha256, long? ExpectedBytes = null);

public sealed class LocalModelStore
{
    private readonly HttpClient _http;

    public LocalModelStore(string rootDirectory, HttpClient? httpClient = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _http = httpClient ?? new HttpClient();
    }

    public string RootDirectory { get; }

    public string GetPath(LocalModelDefinition model) => Path.Combine(RootDirectory, model.Id, "model.onnx");

    public async Task<string> EnsureAsync(LocalModelDefinition model, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var target = GetPath(model);
        if (File.Exists(target) && await MatchesChecksumAsync(target, model.Sha256, cancellationToken)) return target;
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var partial = target + ".partial";
        using var response = await _http.GetAsync(model.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? model.ExpectedBytes;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            if (total is > 0) progress?.Report((double)written / total.Value);
        }
        await output.FlushAsync(cancellationToken);
        if (!await MatchesChecksumAsync(partial, model.Sha256, cancellationToken))
        {
            TryDelete(partial);
            throw new InvalidDataException($"Model '{model.Id}' failed its SHA-256 checksum.");
        }
        File.Move(partial, target, true);
        return target;
    }

    private static async Task<bool> MatchesChecksumAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var checksum = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(checksum).Equals(expected.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

public static class PalmierLocalModels
{
    public static readonly LocalModelDefinition BeatThis = new(
        "beat-this",
        new Uri("https://huggingface.co/benjamin-paine/beat-this/resolve/main/beat_this.onnx"),
        "REPLACE_WITH_RELEASE_CHECKSUM");

    public static readonly LocalModelDefinition SigLip2 = new(
        "siglip2",
        new Uri("https://huggingface.co/google/siglip2-base-patch16-224/resolve/main/model.onnx"),
        "REPLACE_WITH_RELEASE_CHECKSUM");
}
