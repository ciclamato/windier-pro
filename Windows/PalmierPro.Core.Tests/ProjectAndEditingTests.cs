using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using PalmierPro.Core.Automation;
using PalmierPro.Core.Editing;
using PalmierPro.Core.Export;
using PalmierPro.Core.Models;
using PalmierPro.Core.Persistence;
using PalmierPro.Core.Serialization;
using Xunit;

namespace PalmierPro.Core.Tests;

public sealed class ProjectAndEditingTests
{
    [Fact]
    public void DecodesSourceTimelineAndLegacyMediaSources()
    {
        const string json = """
        {
          "timelines": [{
            "id": "timeline-1",
            "name": "Cut",
            "fps": 24,
            "width": 1920,
            "height": 1080,
            "tracks": [{
              "id": "track-1",
              "type": "video",
              "clips": [{
                "id": "clip-1",
                "mediaRef": "asset-1",
                "mediaType": "video",
                "sourceClipType": "video",
                "startFrame": 12,
                "durationFrames": 48,
                "transform": { "centerX": 0.5, "centerY": 0.5, "width": 1, "height": 1 }
              }]
            }]
          }],
          "activeTimelineId": "timeline-1"
        }
        """;

        var project = ProjectFile.Decode(Encoding.UTF8.GetBytes(json));

        Assert.Equal("timeline-1", project.ActiveTimelineId);
        Assert.Equal(60, project.Timelines[0].TotalFrames);
        Assert.Equal("clip-1", project.Timelines[0].Tracks[0].Clips[0].Id);

        var source = new MediaManifestEntry { Name = "Camera", Source = new MediaSource.External("C:\\Media\\camera.mp4") };
        var encoded = JsonSerializer.Serialize(source, PalmierJson.Options);
        var decoded = JsonSerializer.Deserialize<MediaManifestEntry>(encoded, PalmierJson.Options)!;
        Assert.Equal(source.Source, decoded.Source);
    }

    [Fact]
    public void PreservesSwiftManifestKeysAndReferenceDates()
    {
        var created = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entry = new MediaManifestEntry
        {
            Name = "Camera",
            Type = ClipType.Video,
            Source = new MediaSource.External("C:\\Media\\camera.mp4"),
            SourceFps = 29.97,
            CachedRemoteUrl = "https://example.invalid/file",
            CreatedAt = created
        };
        var json = JsonSerializer.Serialize(entry, PalmierJson.Options);
        Assert.Contains("\"sourceFPS\"", json);
        Assert.Contains("\"cachedRemoteURL\"", json);
        Assert.DoesNotContain("2025-01-01", json);
        var decoded = JsonSerializer.Deserialize<MediaManifestEntry>(json, PalmierJson.Options)!;
        Assert.Equal(created, decoded.CreatedAt);
        Assert.Equal(entry.SourceFps, decoded.SourceFps);
    }

    [Fact]
    public void LegacyBareTimelineIsWrapped()
    {
        const string json = """
        { "id": "legacy", "name": "Legacy", "fps": 30, "width": 1920, "height": 1080, "tracks": [] }
        """;

        var project = ProjectFile.Decode(Encoding.UTF8.GetBytes(json));

        Assert.Single(project.Timelines);
        Assert.Equal("legacy", project.ActiveTimelineId);
    }

    [Fact]
    public async Task CommandsShareUndoAndRedoState()
    {
        var project = ProjectFile.CreateDefault();
        var document = new EditorDocument(project);
        var timeline = project.Timelines[0];

        await document.ExecuteAsync(new AddTrackCommand(timeline.Id, ClipType.Video, "V1"));
        var track = document.Project.Timelines[0].Tracks[0];
        await document.ExecuteAsync(new InsertClipCommand(timeline.Id, track.Id, new Clip
        {
            Id = "clip-1", MediaRef = "asset-1", StartFrame = 0, DurationFrames = 30, MediaType = ClipType.Video
        }));

        Assert.Single(document.Project.Timelines[0].Tracks[0].Clips);
        await document.UndoAsync();
        Assert.Empty(document.Project.Timelines[0].Tracks[0].Clips);
        await document.RedoAsync();
        Assert.Single(document.Project.Timelines[0].Tracks[0].Clips);
    }

    [Fact]
    public async Task PackageSaveReplacesDirectoryAsOneCompleteOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "palmier-core-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var packagePath = Path.Combine(root, "Test.palmier");
            Directory.CreateDirectory(root);
            var document = new EditorDocument(ProjectFile.CreateDefault(), new MediaManifest());
            await ProjectPackageStore.SaveAsync(packagePath, await document.SnapshotAsync());

            var loaded = await ProjectPackageStore.OpenAsync(packagePath);
            Assert.Single(loaded.Project.Timelines);
            Assert.True(File.Exists(Path.Combine(packagePath, ProjectPackageStore.ProjectFileName)));
            Assert.True(Directory.Exists(Path.Combine(packagePath, ProjectPackageStore.MediaDirectoryName)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ImportAndRemoveUndoProjectAndManifestTogether()
    {
        var project = ProjectFile.CreateDefault();
        var document = new EditorDocument(project, new MediaManifest());
        var timeline = project.Timelines[0];
        var track = timeline.Tracks[0];
        var clip = new Clip { Id = "clip-1", MediaRef = "asset-1", StartFrame = 0, DurationFrames = 30, MediaType = ClipType.Video };
        var asset = new MediaManifestEntry
        {
            Id = "asset-1",
            Name = "shot.mp4",
            Type = ClipType.Video,
            Duration = 1,
            Source = new MediaSource.External("C:\\Media\\shot.mp4")
        };

        await document.ExecuteAsync(new ImportMediaCommand(timeline.Id, track.Id, clip, asset));
        Assert.Single(document.Project.Timelines[0].Tracks[0].Clips);
        Assert.Single(document.Manifest.Entries);

        await document.UndoAsync();
        Assert.Empty(document.Project.Timelines[0].Tracks[0].Clips);
        Assert.Empty(document.Manifest.Entries);

        await document.RedoAsync();
        Assert.Single(document.Project.Timelines[0].Tracks[0].Clips);
        Assert.Single(document.Manifest.Entries);

        await document.ExecuteAsync(new RemoveMediaCommand(timeline.Id, clip.Id, asset.Id));
        await document.UndoAsync();
        Assert.Single(document.Project.Timelines[0].Tracks[0].Clips);
        Assert.Single(document.Manifest.Entries);
    }

    [Fact]
    public async Task FailedCommandIsAtomicAndDoesNotCreateUndoEntry()
    {
        var document = new EditorDocument(ProjectFile.CreateDefault());
        var timeline = document.Project.Timelines[0];
        await Assert.ThrowsAsync<ArgumentException>(() => document.ExecuteAsync(new AddTimelineCommand("", 30)));
        Assert.Single(document.Project.Timelines);
        Assert.False(document.IsDirty);
        var receipt = await document.UndoAsync();
        Assert.False(receipt.Changed);
    }

    [Fact]
    public async Task TimelineTrimAndPropertiesAreOneUndoableIntent()
    {
        var project = ProjectFile.CreateDefault();
        var timeline = project.Timelines[0];
        var track = timeline.Tracks[0];
        var document = new EditorDocument(project, new MediaManifest());
        await document.ExecuteAsync(new InsertClipCommand(timeline.Id, track.Id, new Clip
        {
            Id = "clip-1", MediaRef = "asset-1", StartFrame = 30, DurationFrames = 90, MediaType = ClipType.Video
        }));

        await document.ExecuteAsync(new TrimClipCommand(timeline.Id, "clip-1", 60, 15, 45));
        await document.ExecuteAsync(new SetClipPropertiesCommand(timeline.Id, "clip-1", .75, .5, 1.5, 48));
        var clip = document.Project.Timelines[0].Tracks[0].Clips[0];
        Assert.Equal(45, clip.StartFrame);
        Assert.Equal(48, clip.DurationFrames);
        Assert.Equal(15, clip.TrimStartFrame);
        Assert.Equal(.75, clip.Opacity);
        Assert.Equal(1.5, clip.Speed);

        await document.UndoAsync();
        clip = document.Project.Timelines[0].Tracks[0].Clips[0];
        Assert.Equal(60, clip.DurationFrames);
        Assert.Equal(1, clip.Opacity);
        await document.UndoAsync();
        clip = document.Project.Timelines[0].Tracks[0].Clips[0];
        Assert.Equal(30, clip.StartFrame);
        Assert.Equal(90, clip.DurationFrames);
        Assert.Equal(0, clip.TrimStartFrame);
    }

    [Fact]
    public async Task RippleDeleteShiftsEveryTrackAndCleansUnusedAsset()
    {
        var project = ProjectFile.CreateDefault();
        var timeline = project.Timelines[0];
        var video = timeline.Tracks[0];
        var audio = timeline.Tracks[1];
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry { Id = "video", Name = "video.mp4" },
                new MediaManifestEntry { Id = "audio", Name = "audio.wav" },
                new MediaManifestEntry { Id = "unused", Name = "unused.wav" }
            ]
        };
        var document = new EditorDocument(project, manifest);
        await document.ExecuteAsync(new InsertClipCommand(timeline.Id, video.Id, new Clip { Id = "v1", MediaRef = "video", StartFrame = 0, DurationFrames = 30, MediaType = ClipType.Video }));
        await document.ExecuteAsync(new InsertClipCommand(timeline.Id, video.Id, new Clip { Id = "v2", MediaRef = "video", StartFrame = 30, DurationFrames = 30, MediaType = ClipType.Video }));
        await document.ExecuteAsync(new InsertClipCommand(timeline.Id, audio.Id, new Clip { Id = "a1", MediaRef = "audio", StartFrame = 30, DurationFrames = 30, MediaType = ClipType.Audio }));

        await document.ExecuteAsync(new RippleDeleteCommand(timeline.Id, "v1"));

        Assert.Equal(0, document.Project.Timelines[0].Tracks[0].Clips.Single(clip => clip.Id == "v2").StartFrame);
        Assert.Equal(0, document.Project.Timelines[0].Tracks[1].Clips.Single().StartFrame);
        Assert.Contains(document.Manifest.Entries, entry => entry.Id == "video");
        Assert.Contains(document.Manifest.Entries, entry => entry.Id == "audio");
        Assert.DoesNotContain(document.Manifest.Entries, entry => entry.Id == "unused");
        await document.UndoAsync();
        Assert.Equal(30, document.Project.Timelines[0].Tracks[0].Clips.Single(clip => clip.Id == "v2").StartFrame);
    }

    [Fact]
    public async Task ExportPreflightAcceptsLayeredAndSequentialVideo()
    {
        var project = ProjectFile.CreateDefault();
        var timeline = project.Timelines[0];
        var video = timeline.Tracks[0];
        var document = new EditorDocument(project, new MediaManifest());
        await document.ExecuteAsync(new InsertClipCommand(timeline.Id, video.Id, new Clip
        {
            Id = "v1", MediaRef = "asset-1", StartFrame = 0, DurationFrames = 30, MediaType = ClipType.Video
        }));
        await document.ExecuteAsync(new InsertClipCommand(timeline.Id, video.Id, new Clip
        {
            Id = "v2", MediaRef = "asset-2", StartFrame = 30, DurationFrames = 30, MediaType = ClipType.Video
        }));

        var preflight = FfmpegExportService.Preflight(await document.SnapshotAsync());
        Assert.True(preflight.IsSupported);
        Assert.Equal(2, preflight.ClipCount);
        Assert.Equal(60, preflight.FrameCount);
    }

    [Fact]
    public async Task CaptionImportAddsCuesAsOneUndoableAssetOperation()
    {
        var project = ProjectFile.CreateDefault();
        var timeline = project.Timelines[0];
        var subtitleTrack = new Track { Type = ClipType.Subtitle, Name = "S1" };
        timeline.Tracks.Add(subtitleTrack);
        var document = new EditorDocument(project, new MediaManifest());
        subtitleTrack = document.Project.Timelines[0].Tracks.Single(track => track.Type == ClipType.Subtitle);
        var asset = new MediaManifestEntry
        {
            Id = "captions",
            Name = "captions.srt",
            Type = ClipType.Subtitle,
            Source = new MediaSource.External("C:\\Media\\captions.srt")
        };
        var cues = new[]
        {
            new Clip { Id = "cue-1", MediaType = ClipType.Subtitle, StartFrame = 0, DurationFrames = 30, TextContent = "Hello" },
            new Clip { Id = "cue-2", MediaType = ClipType.Subtitle, StartFrame = 45, DurationFrames = 30, TextContent = "World" }
        };

        await document.ExecuteAsync(new ImportSubtitleCommand(timeline.Id, subtitleTrack.Id, cues, asset));
        Assert.Equal(2, document.Project.Timelines[0].Tracks.Single(track => track.Id == subtitleTrack.Id).Clips.Count);
        Assert.Single(document.Manifest.Entries);
        await document.UndoAsync();
        Assert.Empty(document.Project.Timelines[0].Tracks.Single(track => track.Id == subtitleTrack.Id).Clips);
        Assert.Empty(document.Manifest.Entries);
    }

    [Fact]
    public async Task LoopbackMcpExposesTheSameUndoableDocument()
    {
        var document = new EditorDocument(ProjectFile.CreateDefault());
        await using var server = new McpLoopbackServer(document, 19891);
        await server.StartAsync();
        using var http = new HttpClient();
        using var response = await http.PostAsJsonAsync(server.Endpoint, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
            @params = new { }
        });
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(6, payload.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());

        using var browserRequest = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } })
        };
        browserRequest.Headers.Add("Origin", "https://example.invalid");
        using var browserResponse = await http.SendAsync(browserRequest);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, browserResponse.StatusCode);
    }
}
