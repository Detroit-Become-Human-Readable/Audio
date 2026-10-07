using DetroitAudio.Audio;

namespace DetroitAudio.Desktop;

/// <summary>Owns one preview request and all of the resources it prepares.</summary>
public sealed class PreviewSession : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private int disposed;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => completion.Task;
    public CancellationToken Token { get; }
    public Player Player { get; }
    public PreviewClip? Clip { get; set; }
    public string? Directory { get; set; }
    public bool Prepared { get; set; }
    public string? Key { get; set; }
    public PreviewKind Kind { get; set; } = PreviewKind.Complete;
    public PreviewSession(IPlaybackBackendFactory? factory = null) { Token = cancellation.Token; Player = new(factory); }
    public void Cancel() { if (Volatile.Read(ref disposed) == 0) cancellation.Cancel(); }
    public void MarkPrepared() { Prepared = true; completion.TrySetResult(); }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        cancellation.Cancel(); Player.Dispose();
        if (Clip is not null) { Clip.DisposeAsync().GetAwaiter().GetResult(); Clip = null; }
        if (Directory is { } path) { try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } Directory = null; }
        cancellation.Dispose();
        return ValueTask.CompletedTask;
    }
}
