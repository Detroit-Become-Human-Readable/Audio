using DetroitAudio.Desktop;
using NAudio.Wave;
using Xunit;

namespace DetroitAudio.Desktop.Tests;

public sealed class PlayerTests
{
    [Fact]
    public async Task LoadingAnotherClipDisposesPreviousBackendAndIgnoresOldCompletion()
    {
        var factory = new FakeFactory(); using var player = new Player(factory);
        var stopped = 0; player.PlaybackStopped += (_, _) => stopped++;
        await player.LoadAsync("first.wav", .5f); var first = factory.Instances[0];
        await player.LoadAsync("second.wav", .8f);
        Assert.True(first.Disposed); Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(.8f, player.Volume); first.Fail(new IOException("stale device event"));
        Assert.Equal(0, stopped); Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task InitializationUsesOneStereoFallback()
    {
        var factory = new FakeFactory { Failures = 1 }; using var player = new Player(factory);
        var normalized = 0;
        await player.LoadAsync("surround.wav", .75f, (path, _) => { Assert.Equal("surround.wav", path); normalized++; return Task.FromResult("stereo.wav"); });
        Assert.Equal(1, normalized); Assert.Equal(new[] { "surround.wav", "stereo.wav" }, factory.Requests.Select(request => request.Path));
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task FailedFallbackLeavesNoActivePlayback()
    {
        var factory = new FakeFactory { Failures = 2 }; using var player = new Player(factory);
        await Assert.ThrowsAsync<DetroitAudio.Audio.AudioToolException>(() => player.LoadAsync("surround.wav", .5f, (_, _) => Task.FromResult("stereo.wav")));
        Assert.Equal(PlaybackState.Stopped, player.State); Assert.Equal(0, player.Length);
        Assert.Equal(2, factory.Requests.Count);
    }

    [Fact]
    public async Task CancelingFallbackDoesNotInstallPlayback()
    {
        var factory = new FakeFactory { Failures = 1 }; using var player = new Player(factory);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.LoadAsync("surround.wav", .5f,
            (_, _) => { cancellation.Cancel(); return Task.FromResult("stereo.wav"); }, cancellation.Token));
        Assert.Equal(PlaybackState.Stopped, player.State); Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task StopDuringFallbackRejectsLateClip()
    {
        var factory = new FakeFactory { Failures = 1 }; using var player = new Player(factory);
        var pending = new TaskCompletionSource<string>();
        var load = player.LoadAsync("first.wav", .5f, (_, _) => pending.Task);
        player.Stop(); pending.SetResult("stereo.wav"); await load;
        Assert.Equal(PlaybackState.Stopped, player.State); Assert.True(Assert.Single(factory.Instances).Disposed);
    }

    [Fact]
    public async Task SeekingRecreatesBuffersAtPositionAndKeepsPause()
    {
        var factory = new FakeFactory(); using var player = new Player(factory);
        await player.LoadAsync("clip.wav", .5f); player.Toggle();
        var first = factory.Instances[0]; player.Position = 3;
        Assert.True(first.Disposed); Assert.Equal(3, player.Position);
        Assert.Equal(PlaybackState.Paused, player.State); player.Toggle();
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(3, factory.Requests[1].Position);
    }

    [Fact]
    public async Task ToggleAfterNaturalCompletionRestartsAtZeroWithFreshBuffers()
    {
        var factory = new FakeFactory(); using var player = new Player(factory);
        await player.LoadAsync("clip.wav", .5f); var first = factory.Instances[0];
        first.Position = first.Length; first.Complete();
        Assert.Equal(PlaybackState.Stopped, player.State);
        player.Toggle();
        Assert.True(first.Disposed); Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(0, player.Position); Assert.Equal(0, factory.Requests[1].Position);
    }

    [Fact]
    public async Task SeekingAfterCompletionStartsFromSelectedPosition()
    {
        var factory = new FakeFactory(); using var player = new Player(factory);
        await player.LoadAsync("clip.wav", .5f); var first = factory.Instances[0];
        first.Position = first.Length; first.Complete(); player.Position = 3; player.Toggle();
        Assert.Equal(PlaybackState.Playing, player.State); Assert.Equal(3, player.Position);
        Assert.Equal(2, factory.Requests.Count);
    }

    [Fact]
    public async Task DeviceLossRaisesErrorAndReleasesResources()
    {
        var factory = new FakeFactory(); using var player = new Player(factory);
        var stopped = new TaskCompletionSource<StoppedEventArgs>(); player.PlaybackStopped += (_, args) => stopped.TrySetResult(args);
        await player.LoadAsync("clip.wav", .5f); var backend = factory.Instances[0];
        backend.Fail(new IOException("device removed"));
        var result = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(result.Exception); Assert.Equal(PlaybackState.Stopped, player.State);
        await backend.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task StopAndDisposeReleaseFileBackendOnce()
    {
        var factory = new FakeFactory(); var player = new Player(factory);
        await player.LoadAsync("clip.wav", .5f); var backend = factory.Instances[0];
        player.Stop(); player.Dispose(); Assert.Equal(1, backend.DisposeCount);
        Assert.Equal(0, player.Position);
    }

    [Fact]
    public void PreviewGainKeepsExtensibleLayoutAndEveryChannel()
    {
        var format = new WaveFormatExtensible(48000, 16, 6);
        var samples = Enumerable.Repeat((short)1000, 12).ToArray();
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var gain = new GainWaveProvider(new ByteProvider(format, bytes)) { Volume = .5f };
        var output = new byte[bytes.Length]; var read = gain.Read(output, 0, output.Length);
        Assert.Same(format, gain.WaveFormat); Assert.Equal(6, gain.WaveFormat.Channels); Assert.Equal(bytes.Length, read);
        Assert.All(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(output).ToArray(), sample => Assert.Equal(500, sample));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void MediaFoundationMixRetainsCenterAndSurroundChannels(int activeChannel)
    {
        var format = new WaveFormatExtensible(48000, 16, 6);
        var samples = new short[4800 * 6];
        for (var frame = 0; frame < 4800; frame++) samples[frame * 6 + activeChannel] = (short)(Math.Sin(frame * 2 * Math.PI * 440 / 48000) * 10000);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        using var resampler = new MediaFoundationResampler(new ByteProvider(format, bytes), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ResamplerQuality = 60 };
        var output = new byte[4800 * 2 * 4]; var read = resampler.Read(output, 0, output.Length);
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(output.AsSpan(0, read)).ToArray();
        Assert.NotEmpty(values);
        Assert.Contains(values, value => Math.Abs(value) > .01f);
        if (activeChannel == 2)
        {
            Assert.Contains(values.Where((_, index) => index % 2 == 0), value => Math.Abs(value) > .01f);
            Assert.Contains(values.Where((_, index) => index % 2 == 1), value => Math.Abs(value) > .01f);
        }
    }

    private sealed class ByteProvider(WaveFormat format, byte[] bytes) : IWaveProvider
    {
        private int position;
        public WaveFormat WaveFormat => format;
        public int Read(byte[] buffer, int offset, int count) { var length = Math.Min(bytes.Length - position, count); Array.Copy(bytes, position, buffer, offset, length); position += length; return length; }
    }
    private sealed class FakeFactory : IPlaybackBackendFactory
    {
        public int Failures { get; set; }
        public List<(string Path, double Position)> Requests { get; } = [];
        public List<FakeBackend> Instances { get; } = [];
        public IPlaybackBackend Create(string path, float volume, double positionSeconds = 0)
        {
            Requests.Add((path, positionSeconds));
            if (Failures-- > 0) throw new IOException("device initialization failed");
            var backend = new FakeBackend { Volume = volume, Position = positionSeconds }; Instances.Add(backend); return backend;
        }
    }
    private sealed class FakeBackend : IPlaybackBackend
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState State { get; private set; }
        public double Position { get; set; }
        public double Length => 10;
        public float Volume { get; set; }
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource Disposal { get; } = new();
        public void Play() => State = PlaybackState.Playing;
        public void Pause() => State = PlaybackState.Paused;
        public void Stop() { State = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Complete() { State = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Fail(Exception error) { State = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs(error)); }
        public void Dispose() { Disposed = true; DisposeCount++; State = PlaybackState.Stopped; Disposal.TrySetResult(); }
    }
}
