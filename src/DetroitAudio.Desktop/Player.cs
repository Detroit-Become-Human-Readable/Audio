using DetroitAudio.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DetroitAudio.Desktop;

public interface IPlaybackBackend : IDisposable
{
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    PlaybackState State { get; }
    double Position { get; }
    double Length { get; }
    float Volume { get; set; }
    void Play();
    void Pause();
    void Stop();
}

public interface IPlaybackBackendFactory
{
    IPlaybackBackend Create(string path, float volume, double positionSeconds = 0);
}

public sealed class Player(IPlaybackBackendFactory? backendFactory = null) : IDisposable
{
    private readonly IPlaybackBackendFactory factory = backendFactory ?? new WasapiBackendFactory();
    private readonly object gate = new();
    private IPlaybackBackend? backend;
    private string? currentPath;
    private long generation;
    private float volume = .75f;
    private bool paused;
    private bool completed;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState State { get { lock (gate) return backend is null ? PlaybackState.Stopped : paused ? PlaybackState.Paused : backend.State; } }
    public double Position
    {
        get { lock (gate) return backend?.Position ?? 0; }
        set
        {
            string? path; double position; bool playing, keepPaused; long version; IPlaybackBackend? old;
            lock (gate)
            {
                if (backend is null || currentPath is null) return;
                path = currentPath; position = Math.Clamp(value, 0, backend.Length);
                playing = !paused && backend.State == PlaybackState.Playing; keepPaused = paused || backend.State == PlaybackState.Paused; version = ++generation;
                old = backend; backend = null;
            }
            old.Dispose();
            // Recreate the device and resampler to discard queued samples after a seek.
            try { Install(factory.Create(path, volume, position), path, version, playing, keepPaused); }
            catch (Exception error) { RaiseStopped(version, new StoppedEventArgs(error)); }
        }
    }
    public double Length { get { lock (gate) return backend?.Length ?? 0; } }
    public float Volume
    {
        get { lock (gate) return volume; }
        set { lock (gate) { volume = Math.Clamp(value, 0, 1); if (backend is not null) backend.Volume = volume; } }
    }
    public void Load(string path, float volume) => LoadAsync(path, volume).GetAwaiter().GetResult();
    public async Task LoadAsync(string path, float volume,
        Func<string, CancellationToken, Task<string>>? stereoFallback = null, CancellationToken cancellationToken = default)
    {
        long version; IPlaybackBackend? old;
        lock (gate) { version = ++generation; old = backend; backend = null; currentPath = null; paused = completed = false; this.volume = Math.Clamp(volume, 0, 1); }
        old?.Dispose();
        IPlaybackBackend? candidate = null;
        var source = path;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { candidate = factory.Create(source, this.volume); }
            catch (Exception initializationError) when (stereoFallback is not null && initializationError is not OperationCanceledException)
            {
                try
                {
                    source = await stereoFallback(path, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    candidate = factory.Create(source, this.volume);
                }
                catch (Exception fallbackError) when (fallbackError is not OperationCanceledException)
                { throw new AudioToolException("Could not start playback on the output device.", initializationError.Message + Environment.NewLine + fallbackError.Message, fallbackError); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var ready = candidate; candidate = null;
            Install(ready, source, version, true);
        }
        finally { candidate?.Dispose(); }
    }
    private void Install(IPlaybackBackend candidate, string path, long version, bool play, bool keepPaused = false)
    {
        bool stale;
        lock (gate)
        {
            stale = generation != version;
            if (!stale)
            {
                backend = candidate; currentPath = path; paused = keepPaused; completed = false;
                candidate.PlaybackStopped += (_, args) => HandleStopped(candidate, version, args);
            }
        }
        if (stale) { candidate.Dispose(); return; }
        try
        {
            lock (gate) { if (play && generation == version && backend == candidate) candidate.Play(); }
        }
        catch
        {
            bool owns;
            lock (gate)
            {
                owns = backend == candidate;
                if (owns) { backend = null; currentPath = null; paused = completed = false; }
            }
            if (owns) candidate.Dispose();
            throw;
        }
    }
    private void HandleStopped(IPlaybackBackend sender, long version, StoppedEventArgs args)
    {
        lock (gate)
        {
            if (version != generation || backend != sender) return;
            if (args.Exception is not null) { backend = null; currentPath = null; paused = completed = false; }
            else { paused = false; completed = true; }
        }
        if (args.Exception is not null)
        {
            // WASAPI raises this from its render thread; disposal must not join that thread.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { sender.Dispose(); }
                catch (Exception cleanupError)
                {
                    RaiseStopped(version, new StoppedEventArgs(new AudioToolException("Playback stopped and its device could not be released cleanly.", cleanupError.Message, args.Exception)));
                    return;
                }
                RaiseStopped(version, args);
            });
            return;
        }
        RaiseStopped(version, args);
    }
    private void RaiseStopped(long version, StoppedEventArgs args)
    {
        lock (gate) { if (version != generation) return; }
        PlaybackStopped?.Invoke(this, args);
    }
    public void Toggle()
    {
        IPlaybackBackend? old; string path; long version; float currentVolume;
        lock (gate)
        {
            if (backend is null) return;
            if (!paused && backend.State == PlaybackState.Playing) { backend.Pause(); paused = true; return; }
            if (!completed) { backend.Play(); paused = false; return; }
            old = backend; path = currentPath!; currentVolume = volume; version = ++generation;
            backend = null; paused = completed = false;
        }
        // A completed resampler may retain its drained buffers; start a fresh chain at zero.
        old.Dispose();
        try { Install(factory.Create(path, currentVolume, 0), path, version, true); }
        catch (Exception error) { RaiseStopped(version, new StoppedEventArgs(error)); }
    }
    public void Stop()
    {
        IPlaybackBackend? old; long version;
        lock (gate) { version = ++generation; old = backend; backend = null; currentPath = null; paused = completed = false; }
        old?.Dispose();
        RaiseStopped(version, new StoppedEventArgs());
    }
    public void Dispose()
    {
        IPlaybackBackend? old;
        lock (gate) { ++generation; old = backend; backend = null; currentPath = null; paused = completed = false; }
        old?.Dispose();
    }
}

public sealed class WasapiBackendFactory : IPlaybackBackendFactory
{
    public IPlaybackBackend Create(string path, float volume, double positionSeconds = 0) => new WasapiBackend(path, volume, positionSeconds);

    private sealed class WasapiBackend : IPlaybackBackend
    {
        private readonly WaveFileReader reader;
        private readonly MMDevice device;
        private readonly MediaFoundationResampler resampler;
        private readonly GainWaveProvider gain;
        private readonly WasapiOut output;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState State => output.PlaybackState;
        public double Position => reader.CurrentTime.TotalSeconds;
        public double Length => reader.TotalTime.TotalSeconds;
        public float Volume { get => gain.Volume; set => gain.Volume = value; }
        public WasapiBackend(string path, float volume, double position)
        {
            WaveFileReader? source = null; MMDevice? endpoint = null;
            MediaFoundationResampler? adapted = null; WasapiOut? player = null;
            try
            {
                source = new WaveFileReader(path);
                source.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(position, 0, source.TotalTime.TotalSeconds));
                using var enumerator = new MMDeviceEnumerator();
                endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                // Feed the original extensible format (including its speaker mask) into MF.
                adapted = new MediaFoundationResampler(source, endpoint.AudioClient.MixFormat) { ResamplerQuality = 60 };
                gain = new GainWaveProvider(adapted) { Volume = Math.Clamp(volume, 0, 1) };
                player = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 100);
                player.Init(gain);
                reader = source; device = endpoint; resampler = adapted; output = player;
                output.PlaybackStopped += (_, args) => PlaybackStopped?.Invoke(this, args);
            }
            catch
            {
                foreach (var resource in new IDisposable?[] { player, adapted, source, endpoint })
                    try { resource?.Dispose(); } catch { }
                throw;
            }
        }
        public void Play() => output.Play();
        public void Pause() => output.Pause();
        public void Stop() => output.Stop();
        public void Dispose()
        {
            try { output.Dispose(); }
            finally { try { resampler.Dispose(); } finally { try { reader.Dispose(); } finally { device.Dispose(); } } }
        }
    }
}

/// <summary>Applies preview gain without discarding an extensible speaker layout.</summary>
public sealed class GainWaveProvider(IWaveProvider source) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public float Volume { get; set; } = .75f;
    public int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var samples = buffer.AsSpan(offset, read);
        var format = WaveFormat;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (isFloat && format.BitsPerSample == 32)
        {
            var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < values.Length; i++) values[i] *= Volume;
        }
        else if (format.BitsPerSample == 16)
        {
            var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(samples);
            for (var i = 0; i < values.Length; i++) values[i] = (short)Math.Clamp(values[i] * Volume, short.MinValue, short.MaxValue);
        }
        else if (format.BitsPerSample == 24)
        {
            for (var i = 0; i + 2 < samples.Length; i += 3)
            {
                var value = samples[i] | samples[i + 1] << 8 | samples[i + 2] << 16;
                if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
                value = (int)Math.Clamp(value * (double)Volume, -8388608, 8388607);
                samples[i] = (byte)value; samples[i + 1] = (byte)(value >> 8); samples[i + 2] = (byte)(value >> 16);
            }
        }
        else if (format.BitsPerSample == 32)
        {
            var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(samples);
            for (var i = 0; i < values.Length; i++) values[i] = (int)Math.Clamp(values[i] * (double)Volume, int.MinValue, int.MaxValue);
        }
        else throw new AudioToolException($"The output device mix format uses unsupported {format.BitsPerSample}-bit samples.");
        return read;
    }
}
