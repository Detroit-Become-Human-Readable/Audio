using DetroitAudio.Core;

namespace DetroitAudio.Audio;

public enum WavFormat { Pcm16, Pcm24, Float32 }
public enum AudioOperation { Probe, Decode, WwiseVorbisRemux, Reencode, Render }
public enum DecodePurpose { Conversion, Preview, PartialPreview }
public enum PreviewKind { Complete, Available, Partial }

public sealed record AudioToolPaths(
    string? VgmstreamCli,
    string? Ffmpeg,
    string? Ffprobe,
    string? Ww2Ogg,
    string? Revorb,
    string? PackedCodebooks,
    string? PackedCodebooksAoTuV,
    string? ConfiguredDirectory = null)
{
    public static AudioToolPaths Discover(string? configuredDirectory = null, string? applicationDirectory = null)
    {
        var appDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredDirectory)) roots.Add(Path.GetFullPath(configuredDirectory));
        roots.Add(Path.Combine(appDirectory, "tools"));
        roots.Add(appDirectory);

        string? Find(params string[] candidates)
        {
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var candidate in candidates)
                {
                    var full = Path.GetFullPath(Path.Combine(root, candidate));
                    if (File.Exists(full)) return full;
                }
            return null;
        }

        string? FindInToolFolder(string folder, params string[] names)
        {
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var name in names)
                {
                    var direct = Path.GetFullPath(Path.Combine(root, name));
                    if (File.Exists(direct)) return direct;
                    var nested = Path.GetFullPath(Path.Combine(root, folder, name));
                    if (File.Exists(nested)) return nested;
                }
            return null;
        }

        return new AudioToolPaths(
            FindInToolFolder("vgmstream", "vgmstream-cli.exe", "vgmstream-cli"),
            FindInToolFolder("ffmpeg", "ffmpeg.exe", "ffmpeg"),
            FindInToolFolder("ffmpeg", "ffprobe.exe", "ffprobe"),
            FindInToolFolder("ww2ogg", "ww2ogg.exe", "ww2ogg"),
            FindInToolFolder("revorb", "revorb.exe", "revorb"),
            Find("packed_codebooks.bin", Path.Combine("ww2ogg", "packed_codebooks.bin")),
            Find("packed_codebooks_aoTuV_603.bin", Path.Combine("ww2ogg", "packed_codebooks_aoTuV_603.bin")),
            string.IsNullOrWhiteSpace(configuredDirectory) ? null : Path.GetFullPath(configuredDirectory));
    }
}

public sealed record AudioProgress(string Phase, int Completed = 0, int Total = 0, string? Detail = null);
public sealed record AudioWavMetadata(int Channels, int SampleRate, int BlockAlign, long SampleFrames, ushort FormatCode, ushort BitsPerSample,
    uint? ChannelMask = null);
public sealed record AudioProbeResult(bool Success, string? Codec, int? SampleRate, int? Channels, double? Duration, string? Diagnostic = null);
public sealed record AudioDecodeResult(string OutputPath, WavFormat Format, int Channels, int SampleRate, long SampleFrames, TimeSpan Duration, string Decoder);
public sealed record AudioConversionResult(string OutputPath, string Method, int Channels, int SampleRate, long SampleFrames, TimeSpan Duration,
    IReadOnlyList<string> Diagnostics, AudioOperation Operation = AudioOperation.Reencode);
public sealed record AudioRenderResult(string OutputPath, int Channels, int SampleRate, long SampleFrames, TimeSpan Duration, IReadOnlyList<string> Limitations);

public sealed record EventOptions(
    string Language = "SFX",
    int RandomSeed = 1,
    int DefaultLoopCount = 2,
    double FadeOutSeconds = 5,
    double? MaximumDurationSeconds = null,
    uint? SwitchValueId = null,
    IReadOnlyDictionary<uint, uint>? SwitchValues = null,
    bool AllowPartialMedia = false,
    string? RelatedPlayEventKey = null,
    IReadOnlyDictionary<uint, uint>? StateValues = null);

public sealed record PlaybackPlan(
    string EventKey,
    uint EventId,
    string EventName,
    int RandomSeed,
    IReadOnlyList<PlaybackItem> Items,
    IReadOnlyList<string> Limitations,
    double DurationSeconds,
    string TxptText,
    double FadeOutSeconds = 5,
    bool AllowPartialMedia = false,
    double? MaximumDurationSeconds = null);

public sealed record PlaybackItem(
    string MediaKey,
    string MediaName,
    uint MediaId,
    double StartSeconds,
    double DurationSeconds,
    int LoopCount,
    float GainDb,
    float PitchCents,
    IReadOnlyList<string> Limitations,
    double TrimBeginSeconds = 0,
    double TrimEndSeconds = 0,
    double SourceDurationSeconds = 0,
    double PlaybackSpeed = 1);

public sealed class AudioToolException(string message, string? diagnostic = null, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string? Diagnostic { get; } = diagnostic;
}
