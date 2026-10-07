using DetroitAudio.Core;

namespace DetroitAudio.Audio;

public sealed class PreviewService(AudioService audioService)
{
    public async Task<PreviewClip> CreateAsync(AudioCatalog catalog, MediaEntry media, WavFormat format = WavFormat.Pcm16,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default, bool partialPreview = false)
    {
        if (!MediaPolicy.CanPreview(media)) throw new AudioToolException(MediaPolicy.GetUnavailableReason(media));
        if (media.Completeness == MediaCompleteness.Fragment && !partialPreview)
            throw new AudioToolException("Choose Partial preview to audition the available fragment.");
        if (partialPreview && media.Completeness != MediaCompleteness.Fragment)
            throw new AudioToolException("This entry is not a fragment.");
        var directory = AudioService.CreatePersistentTempDirectory(catalog.Fingerprint);
        var file = Path.Combine(directory, "preview.wav");
        try
        {
            var result = await audioService.DecodeToWavAsync(catalog, media, file, format, progress, cancellationToken,
                purpose: partialPreview ? DecodePurpose.PartialPreview : DecodePurpose.Preview).ConfigureAwait(false);
            return new PreviewClip(result, directory, partialPreview ? PreviewKind.Partial : media.Completeness == MediaCompleteness.Complete ? PreviewKind.Complete : PreviewKind.Available);
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
            throw;
        }
    }
}

public sealed class PreviewClip(AudioDecodeResult result, string ownedDirectory, PreviewKind kind = PreviewKind.Complete) : IAsyncDisposable
{
    public AudioDecodeResult Result { get; } = result;
    public string WavPath => Result.OutputPath;
    public PreviewKind Kind { get; } = kind;
    public string FallbackWavPath => Path.Combine(ownedDirectory, "preview-stereo.wav");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(ownedDirectory)) Directory.Delete(ownedDirectory, recursive: true); } catch { }
        return ValueTask.CompletedTask;
    }
}
