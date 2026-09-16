using System.Diagnostics;

namespace PalmierPro.WinUI.Playback;

public sealed class SharedPlaybackClock
{
    private readonly Stopwatch _stopwatch = new();
    private double _baseSeconds;
    private double _rate = 1;

    public bool IsRunning => _stopwatch.IsRunning;
    public double PositionSeconds => _baseSeconds + (_stopwatch.IsRunning ? _stopwatch.Elapsed.TotalSeconds * _rate : 0);
    public double Rate => _rate;

    public void Start()
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();
    }

    public void Pause()
    {
        if (!_stopwatch.IsRunning) return;
        _baseSeconds = PositionSeconds;
        _stopwatch.Stop();
        _stopwatch.Reset();
    }

    public void Seek(TimeSpan position)
    {
        _baseSeconds = Math.Max(0, position.TotalSeconds);
        _stopwatch.Restart();
    }

    public void SetRate(double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0 || rate > 16) throw new ArgumentOutOfRangeException(nameof(rate));
        var wasRunning = _stopwatch.IsRunning;
        if (wasRunning) Pause();
        _rate = rate;
        if (wasRunning) Start();
    }
}
