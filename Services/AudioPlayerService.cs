using NAudio.Wave;

namespace TJCServiceDesk.Services;

public sealed class AudioPlayerService : IDisposable
{
    private IWavePlayer? _output;
    private WaveStream? _reader;
    public event EventHandler? PlaybackStopped;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;
    public float Volume { get => _output?.Volume ?? 0.7f; set { if (_output is not null) _output.Volume = Math.Clamp(value, 0, 1); } }

    public void Play(string path)
    {
        Stop();
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        _reader = extension == ".flac" ? new MediaFoundationReader(path) : new AudioFileReader(path);
        _output = new WaveOutEvent();
        _output.PlaybackStopped += (_, _) => PlaybackStopped?.Invoke(this, EventArgs.Empty);
        _output.Init(_reader);
        _output.Volume = 0.7f;
        _output.Play();
    }

    public void Toggle()
    {
        if (_output?.PlaybackState == PlaybackState.Playing) _output.Pause();
        else _output?.Play();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is not null) _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds));
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose(); _output = null;
        _reader?.Dispose(); _reader = null;
    }

    public void Dispose() => Stop();
}
