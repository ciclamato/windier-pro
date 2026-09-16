using NAudio.CoreAudioApi;
using NAudio.Wave;
using PalmierPro.WinUI.Playback;

namespace PalmierPro.WinUI.Audio;

public enum AudioBackendKind
{
    WasapiShared,
    WasapiExclusive,
    Asio
}

public sealed record AudioOutputDevice(
    string Id,
    string Name,
    AudioBackendKind Backend,
    bool IsAsio4All = false);

/// <summary>
/// Selectable Windows audio output. WASAPI is always available; ASIO is opt-in and
/// enumerates drivers already installed by the user, including ASIO4ALL when present.
/// Palmier never bundles or silently installs a third-party driver.
/// </summary>
public sealed class WindowsAudioEngine : IDisposable
{
    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private readonly SharedPlaybackClock _clock;
    private IWavePlayer? _player;
    private AudioFileReader? _reader;
    private AudioOutputDevice? _selected;
    private bool _disposed;

    public WindowsAudioEngine(SharedPlaybackClock? clock = null) => _clock = clock ?? new SharedPlaybackClock();

    public SharedPlaybackClock Clock => _clock;
    public AudioOutputDevice? SelectedDevice => _selected;
    public bool IsPlaying => _player?.PlaybackState == PlaybackState.Playing;

    public IReadOnlyList<AudioOutputDevice> EnumerateDevices()
    {
        var devices = new List<AudioOutputDevice>();
        foreach (var device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            devices.Add(new AudioOutputDevice(device.ID, device.FriendlyName, AudioBackendKind.WasapiShared));
        string[] asioDrivers;
        try { asioDrivers = AsioOut.GetDriverNames(); }
        catch { asioDrivers = []; }
        devices.AddRange(asioDrivers.Select(name => new AudioOutputDevice(
            name,
            name,
            AudioBackendKind.Asio,
            name.Contains("ASIO4ALL", StringComparison.OrdinalIgnoreCase))));
        return devices;
    }

    public void Select(AudioOutputDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (device.Backend != AudioBackendKind.Asio && !string.IsNullOrWhiteSpace(device.Id))
            _deviceEnumerator.GetDevice(device.Id);
        _selected = device;
    }

    public void PlayFile(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file was not found.", path);
        Stop();
        _reader = new AudioFileReader(path);
        _player = CreatePlayer();
        _player.Init(_reader);
        _player.Play();
        _clock.Seek(TimeSpan.Zero);
        _clock.Start();
    }

    public void Pause()
    {
        _player?.Pause();
        _clock.Pause();
    }

    public void Resume()
    {
        _player?.Play();
        _clock.Start();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is not null) _reader.CurrentTime = position;
        _clock.Seek(position);
    }

    public void Stop()
    {
        _player?.Stop();
        _reader?.Dispose();
        _player?.Dispose();
        _reader = null;
        _player = null;
        _clock.Pause();
    }

    private IWavePlayer CreatePlayer()
    {
        var selected = _selected ?? EnumerateDevices().FirstOrDefault(device => device.Backend == AudioBackendKind.WasapiShared)
            ?? throw new InvalidOperationException("No active Windows audio output was found.");
        return selected.Backend switch
        {
            AudioBackendKind.WasapiShared => new WasapiOut(_deviceEnumerator.GetDevice(selected.Id), AudioClientShareMode.Shared, true, 100),
            AudioBackendKind.WasapiExclusive => new WasapiOut(_deviceEnumerator.GetDevice(selected.Id), AudioClientShareMode.Exclusive, true, 100),
            AudioBackendKind.Asio => new AsioOut(selected.Id),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _deviceEnumerator.Dispose();
    }
}
