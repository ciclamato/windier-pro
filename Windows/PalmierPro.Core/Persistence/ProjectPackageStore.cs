using System.Text.Json;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Models;
using PalmierPro.Core.Serialization;

namespace PalmierPro.Core.Persistence;

public sealed record LoadedProjectPackage(string RootPath, ProjectFile Project, MediaManifest Manifest, bool ManifestUnreadable);

public static class ProjectPackageStore
{
    public const string ProjectFileName = "project.json";
    public const string ManifestFileName = "media.json";
    public const string MediaDirectoryName = "media";
    public const string ThumbnailFileName = "thumbnail.jpg";
    public const int SupportedManifestVersion = 2;

    public static async Task<LoadedProjectPackage> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        RecoverInterruptedSave(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Project package '{root}' was not found.");
        var projectPath = Path.Combine(root, ProjectFileName);
        if (!File.Exists(projectPath)) throw new InvalidDataException("The project package has no project.json file.");
        var projectData = await File.ReadAllBytesAsync(projectPath, cancellationToken);
        var project = ProjectFile.Decode(projectData);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath)) return new LoadedProjectPackage(root, project, new MediaManifest(), false);
        try
        {
            var manifestData = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<MediaManifest>(manifestData, PalmierJson.Options) ?? new MediaManifest();
            if (manifest.Version > SupportedManifestVersion)
                throw new InvalidDataException($"This project was created by a newer Palmier format (media manifest v{manifest.Version}). Update the Windows build before editing it.");
            return new LoadedProjectPackage(root, project, manifest, false);
        }
        catch (JsonException)
        {
            return new LoadedProjectPackage(root, project, new MediaManifest(), true);
        }
    }

    public static async Task SaveAsync(string targetPath, ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(targetPath);
        var parent = Directory.GetParent(target)?.FullName ?? throw new InvalidOperationException("Project path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.partial");
        var backup = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.backup");
        if (File.Exists(target)) throw new IOException($"'{target}' is a file. Palmier projects are directory packages; choose a folder ending in .palmier.");
        try
        {
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(Path.Combine(temp, MediaDirectoryName));
            await WriteJsonAsync(Path.Combine(temp, ProjectFileName), snapshot.Project, cancellationToken);
            await WriteJsonAsync(Path.Combine(temp, ManifestFileName), snapshot.Manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var hadTarget = Directory.Exists(target);
            if (hadTarget) Directory.Move(target, backup);
            try
            {
                Directory.Move(temp, target);
            }
            catch
            {
                if (hadTarget && Directory.Exists(backup)) Directory.Move(backup, target);
                throw;
            }
            TryDeleteDirectory(backup);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }

    private static void RecoverInterruptedSave(string root)
    {
        var parent = Directory.GetParent(root)?.FullName;
        if (parent is null) return;
        var name = Path.GetFileName(root);
        var backups = Directory.EnumerateDirectories(parent, $".{name}.*.backup").OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
        var partials = Directory.EnumerateDirectories(parent, $".{name}.*.partial").OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
        if (!Directory.Exists(root) && backups.Length > 0)
        {
            Directory.Move(backups[0], root);
            backups = backups.Skip(1).ToArray();
        }
        foreach (var partial in partials) TryDeleteDirectory(partial);
        foreach (var backup in backups) TryDeleteDirectory(backup);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* A locked recovery artifact is left for the next launch. */ }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, value, PalmierJson.Options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
