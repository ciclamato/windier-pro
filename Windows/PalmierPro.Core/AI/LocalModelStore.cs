using System.Net.Http;
using System.Net;
using System.Security.Cryptography;

namespace PalmierPro.Core.AI;

public sealed record LocalModelDefinition(string Id, Uri DownloadUri, string Sha256, long? ExpectedBytes = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Contains(Path.DirectorySeparatorChar) || Id.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("A local model needs a safe identifier.", nameof(Id));
        if (DownloadUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Local model downloads must use HTTPS.", nameof(DownloadUri));
        if (Sha256.Length != 64 || Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A verified model SHA-256 is required.", nameof(Sha256));
    }
}

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
        model.Validate();
        var target = GetPath(model);
        if (File.Exists(target) && await MatchesChecksumAsync(target, model.Sha256, cancellationToken)) return target;
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var partial = target + ".partial";
        var existingBytes = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUri);
        if (existingBytes > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var append = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append && existingBytes > 0)
        {
            TryDelete(partial);
            existingBytes = 0;
        }
        var total = response.Content.Headers.ContentLength is long contentLength
            ? contentLength + (append ? existingBytes : 0)
            : model.ExpectedBytes;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[64 * 1024];
            long written = existingBytes;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                if (total is > 0) progress?.Report((double)written / total.Value);
            }
            await output.FlushAsync(cancellationToken);
        }
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
    public static LocalModelDefinition BeatThis(Uri downloadUri, string sha256, long? expectedBytes = null) =>
        new("beat-this", downloadUri, sha256, expectedBytes);

    public static LocalModelDefinition SigLip2(Uri downloadUri, string sha256, long? expectedBytes = null) =>
        new("siglip2", downloadUri, sha256, expectedBytes);
}
