using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DetroitAudio.Core;

namespace DetroitAudio.Audio;

public sealed class AudioService(AudioToolPaths paths, ProcessRunner? processRunner = null)
{
    private readonly ProcessRunner _processes = processRunner ?? new ProcessRunner();

    public async Task<AudioProbeResult> ProbeAsync(AudioCatalog catalog, MediaEntry media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(media);
        RequireTool(paths.VgmstreamCli, "vgmstream-cli");
        await using var workspace = new OperationWorkspace(catalog.Fingerprint);
        var source = await MaterializeAsync(media, workspace, cancellationToken).ConfigureAwait(false);
        if (media.Completeness == MediaCompleteness.Fragment)
            return await ProbeByDecodingAsync(source, workspace.DirectoryPath, cancellationToken).ConfigureAwait(false);
        var result = await _processes.RunAsync(new ProcessRequest(paths.VgmstreamCli!, ["-m", source], workspace.DirectoryPath,
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        var parsed = ParseVgmstreamProbe(result.StandardOutput + Environment.NewLine + result.StandardError);
        if (result.ExitCode != 0)
        {
            var fallback = await ProbeWithFfprobeAsync(source, cancellationToken).ConfigureAwait(false);
            if (fallback.Success && fallback.Duration is > 0) return fallback;
            var decoded = await ProbeByDecodingAsync(source, workspace.DirectoryPath, cancellationToken).ConfigureAwait(false);
            return decoded.Success ? decoded : new AudioProbeResult(false, null, null, null, null,
                string.Join(Environment.NewLine, new[] { JoinDiagnostic(result), fallback.Diagnostic, decoded.Diagnostic }.Where(text => !string.IsNullOrWhiteSpace(text))));
        }
        if (parsed.Success && parsed.Duration is > 0) return parsed;
        var ffprobe = await ProbeWithFfprobeAsync(source, cancellationToken).ConfigureAwait(false);
        if (ffprobe.Success && ffprobe.Duration is > 0) return ffprobe;
        var decodedMetadata = await ProbeByDecodingAsync(source, workspace.DirectoryPath, cancellationToken).ConfigureAwait(false);
        if (decodedMetadata.Success) return decodedMetadata with { Codec = parsed.Codec ?? media.Codec };
        return new AudioProbeResult(false, parsed.Codec, parsed.SampleRate, parsed.Channels, parsed.Duration,
            string.Join(Environment.NewLine, new[] { parsed.Diagnostic, ffprobe.Diagnostic, decodedMetadata.Diagnostic }.Where(text => !string.IsNullOrWhiteSpace(text))));
    }

    public async Task<PlaybackPlan> CreateEventPlanAsync(AudioCatalog catalog, AudioEvent audioEvent, EventOptions? options = null,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(audioEvent);
        options ??= new EventOptions();
        var selection = EventPlanner.Select(catalog, audioEvent, options);
        var media = selection.SelectedMedia;
        var measurements = new Dictionary<string, AudioProbeResult>(StringComparer.Ordinal);
        for (var index = 0; index < media.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = media[index];
            if (entry.Completeness != MediaCompleteness.Fragment && entry.Duration is > 0 && entry.SampleRate > 0 && entry.Channels > 0) continue;
            progress?.Report(new AudioProgress("Probing event media", index, media.Count, entry.DisplayName));
            var probe = await ProbeAsync(catalog, entry, cancellationToken).ConfigureAwait(false);
            if (!probe.Success || probe.Duration is not > 0 || probe.SampleRate is not > 0 || probe.Channels is not > 0)
                throw new AudioToolException($"Could not measure media '{entry.DisplayName}' for event planning.", probe.Diagnostic);
            measurements[entry.Key] = probe;
            if (entry.Completeness == MediaCompleteness.Fragment) entry.MeasuredDuration = probe.Duration;
            if (entry.Completeness != MediaCompleteness.Fragment)
            {
                entry.Duration = probe.Duration;
                entry.SampleRate = probe.SampleRate.Value;
                entry.Channels = probe.Channels.Value;
                if (!string.IsNullOrWhiteSpace(probe.Codec)) entry.Codec = probe.Codec;
            }
        }
        progress?.Report(new AudioProgress("Planning event", Total: media.Count, Detail: audioEvent.DisplayName));
        return EventPlanner.Schedule(selection, measurements);
    }

    public Task<PlaybackPlan> PrepareEventAsync(AudioCatalog catalog, AudioEvent audioEvent, EventOptions? options = null,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default) =>
        CreateEventPlanAsync(catalog, audioEvent, options, progress, cancellationToken);

    public async Task<AudioDecodeResult> DecodeToWavAsync(
        AudioCatalog catalog,
        MediaEntry media,
        string destinationWav,
        WavFormat format = WavFormat.Pcm16,
        IProgress<AudioProgress>? progress = null,
        CancellationToken cancellationToken = default,
        DecodePurpose purpose = DecodePurpose.Conversion)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationWav);
        if (purpose == DecodePurpose.Conversion) MediaPolicy.RequireConversion(media);
        else if (!MediaPolicy.CanPreview(media)) throw new AudioToolException(MediaPolicy.GetUnavailableReason(media));
        else if (media.Completeness == MediaCompleteness.Fragment && purpose != DecodePurpose.PartialPreview)
            throw new AudioToolException("Use Partial preview for this fragment.");
        RequireTool(paths.VgmstreamCli, "vgmstream-cli");
        await using var workspace = new OperationWorkspace(catalog.Fingerprint);
        progress?.Report(new AudioProgress("Materializing source", Detail: media.DisplayName));
        var source = await MaterializeAsync(media, workspace, cancellationToken).ConfigureAwait(false);
        var rawWav = Path.Combine(workspace.DirectoryPath, "decoded-vgmstream.wav");
        progress?.Report(new AudioProgress("Decoding", Detail: "vgmstream"));
        var formatCode = format switch { WavFormat.Pcm16 => "1", WavFormat.Pcm24 => "2", WavFormat.Float32 => "4", _ => throw new ArgumentOutOfRangeException(nameof(format)) };
        var decoded = await _processes.RunAsync(new ProcessRequest(paths.VgmstreamCli!, ["-i", "-W", formatCode, "-o", rawWav, source], workspace.DirectoryPath,
            Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(decoded, "vgmstream decode failed");
        EnsureFile(rawWav, "vgmstream did not create a WAV file.");
        var metadata = ReadWavMetadata(rawWav);
        progress?.Report(new AudioProgress("Validating WAV"));
        EnsureWav(metadata);
        EnsureWavFormat(metadata, format);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationWav);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(rawWav, staging, overwrite: false); File.Move(staging, destination, overwrite: true); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new AudioDecodeResult(destination, format, metadata.Channels, metadata.SampleRate, metadata.SampleFrames,
            TimeSpan.FromSeconds((double)metadata.SampleFrames / metadata.SampleRate), "vgmstream");
    }

    public async Task<AudioConversionResult> ConvertToOggAsync(
        AudioCatalog catalog,
        MediaEntry media,
        string destinationOgg,
        IProgress<AudioProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationOgg);
        MediaPolicy.RequireConversion(media);
        await using var workspace = new OperationWorkspace(catalog.Fingerprint);
        progress?.Report(new AudioProgress("Materializing source", Detail: media.DisplayName));
        var source = await MaterializeAsync(media, workspace, cancellationToken).ConfigureAwait(false);
        var candidate = Path.Combine(workspace.DirectoryPath, "ww2ogg-output.ogg");
        var diagnostics = new List<string>();
        var remuxPossible = paths.Ww2Ogg is not null && paths.Revorb is not null;
        AudioWavMetadata? expectedFrames = null;
        if (remuxPossible)
        {
            if (paths.VgmstreamCli is not null)
            {
                var expectedWav = Path.Combine(workspace.DirectoryPath, "expected-source.wav");
                var sourceDecode = await _processes.RunAsync(new ProcessRequest(paths.VgmstreamCli, ["-i", "-W", "1", "-o", expectedWav, source], workspace.DirectoryPath,
                    Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
                if (sourceDecode.ExitCode == 0 && File.Exists(expectedWav) && new FileInfo(expectedWav).Length > 0)
                    expectedFrames = ReadWavMetadata(expectedWav);
                else diagnostics.Add("Could not measure source sample frames with vgmstream before attempting packet remux: " + JoinDiagnostic(sourceDecode));
            }
            progress?.Report(new AudioProgress("Reconstructing Wwise Vorbis", Detail: "ww2ogg"));
            var codebooks = new[] { paths.PackedCodebooks, paths.PackedCodebooksAoTuV, null }
                .Where(path => path is null || File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var optionSets = new[] { Array.Empty<string>(), new[] { "--full-setup" }, new[] { "--no-mod-packets" }, new[] { "--full-setup", "--no-mod-packets" } };
            foreach (var codebook in codebooks)
            {
                foreach (var optionSet in optionSets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(candidate)) File.Delete(candidate);
                    var args = new List<string> { source, "-o", candidate };
                    if (codebook is not null) args.AddRange(["--pcb", codebook]);
                    args.AddRange(optionSet);
                    var remux = await _processes.RunAsync(new ProcessRequest(paths.Ww2Ogg!, args, workspace.DirectoryPath,
                        Timeout: TimeSpan.FromMinutes(10)), cancellationToken).ConfigureAwait(false);
                    if (remux.ExitCode != 0 || !File.Exists(candidate) || new FileInfo(candidate).Length == 0)
                    {
                        diagnostics.Add("ww2ogg attempt failed: " + JoinDiagnostic(remux));
                        continue;
                    }
                    var revorb = await _processes.RunAsync(new ProcessRequest(paths.Revorb!, [candidate], workspace.DirectoryPath,
                        Timeout: TimeSpan.FromMinutes(10)), cancellationToken).ConfigureAwait(false);
                    var validation = await DecodeOggForValidationAsync(candidate, workspace.DirectoryPath, "remux-validation.wav", cancellationToken).ConfigureAwait(false);
                    if (validation.Metadata is { } remuxMetadata && validation.Stream is { } remuxStream &&
                        FramesMatchOptional(expectedFrames, remuxStream) && DecoderLengthMatches(remuxMetadata, remuxStream))
                        return await CommitOggAsync(candidate, destinationOgg, "ww2ogg + revorb (Wwise Vorbis remux)", [], AudioOperation.WwiseVorbisRemux, remuxMetadata, remuxStream, cancellationToken).ConfigureAwait(false);
                    if (validation.Stream is { } remuxInfo && expectedFrames is { } expected && !FramesMatch(expected, remuxInfo))
                        diagnostics.Add($"Remux final-granule mismatch: {DescribeFrameMismatch(expected, remuxInfo)}");
                    else if (validation.Metadata is { } remuxDecoded && validation.Stream is { } parsedStream && !DecoderLengthMatches(remuxDecoded, parsedStream))
                        diagnostics.Add($"Remux decoded length does not match its Vorbis granule: {DescribeDecoderMismatch(remuxDecoded, parsedStream)}");
                    else diagnostics.Add("ww2ogg output failed complete FFmpeg/Ogg validation: " + validation.Diagnostic + (revorb.ExitCode == 0 ? "" : "; revorb: " + JoinDiagnostic(revorb)));
                }
            }
        }
        else diagnostics.Add("ww2ogg and revorb are not both available; using the decode/re-encode path.");

        RequireTool(paths.VgmstreamCli, "vgmstream-cli (required for fallback decode)");
        RequireTool(paths.Ffmpeg, "ffmpeg with libvorbis (required for fallback encoding)");
        progress?.Report(new AudioProgress("Decoding source for re-encoding"));
        var wav = Path.Combine(workspace.DirectoryPath, "fallback.wav");
        var decode = await _processes.RunAsync(new ProcessRequest(paths.VgmstreamCli!, ["-i", "-W", "1", "-o", wav, source], workspace.DirectoryPath,
            Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(decode, "vgmstream fallback decode failed");
        EnsureFile(wav, "vgmstream did not create fallback WAV data.");
        EnsureWav(ReadWavMetadata(wav));
        var encoded = Path.Combine(workspace.DirectoryPath, "fallback.ogg");
        progress?.Report(new AudioProgress("Encoding Ogg Vorbis", Detail: "quality 5; lossy re-encode"));
        var ffmpeg = await _processes.RunAsync(new ProcessRequest(paths.Ffmpeg!, ["-v", "error", "-y", "-i", wav,
            "-map", "0:a:0", "-c:a", "libvorbis", "-q:a", "5", "-f", "ogg", encoded], workspace.DirectoryPath,
            Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(ffmpeg, "FFmpeg Ogg Vorbis encode failed");
        var fallbackValidation = await DecodeOggForValidationAsync(encoded, workspace.DirectoryPath, "fallback-validation.wav", cancellationToken).ConfigureAwait(false);
        var fallbackSourceMetadata = ReadWavMetadata(wav);
        if (fallbackValidation.Metadata is not { } fallbackMetadata || fallbackValidation.Stream is not { } fallbackStream)
            throw new AudioToolException("The encoded Ogg file failed complete decode validation.",
                string.Join(Environment.NewLine, diagnostics.Append(fallbackValidation.Diagnostic).Where(value => !string.IsNullOrWhiteSpace(value))));
        if (!FramesMatch(fallbackSourceMetadata, fallbackStream) || !DecoderLengthMatches(fallbackMetadata, fallbackStream))
            throw new AudioToolException("The encoded Ogg stream does not preserve the source sample range.",
                string.Join(Environment.NewLine, diagnostics.Append(DescribeFrameMismatch(fallbackSourceMetadata, fallbackStream))
                    .Append(DescribeDecoderMismatch(fallbackMetadata, fallbackStream))));
        return await CommitOggAsync(encoded, destinationOgg, "vgmstream decode + FFmpeg libvorbis re-encode q5",
            diagnostics, AudioOperation.Reencode, fallbackMetadata, fallbackStream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioRenderResult> RenderAsync(
        AudioCatalog catalog,
        PlaybackPlan plan,
        string destinationWav,
        WavFormat format = WavFormat.Pcm16,
        IProgress<AudioProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Items.Count == 0) throw new AudioToolException("The playback plan contains no media items.");
        if (plan.Items.Count > 128) throw new AudioToolException("The playback plan exceeds the 128-item render limit.");
        RequireTool(paths.Ffmpeg, "ffmpeg (required to combine event layers and timing)");
        RequireTool(paths.VgmstreamCli, "vgmstream-cli");
        await using var workspace = new OperationWorkspace(catalog.Fingerprint);
        var inputWavs = new List<string>();
        var inputMetadata = new List<AudioWavMetadata>();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = plan.Items[index];
            var media = catalog.Media.SingleOrDefault(m => string.Equals(m.Key, item.MediaKey, StringComparison.Ordinal));
            if (media is null) throw new AudioToolException($"Plan media '{item.MediaKey}' no longer resolves in this catalog.");
            if (!plan.AllowPartialMedia) MediaPolicy.RequireConversion(media);
            else if (!MediaPolicy.CanPreview(media)) throw new AudioToolException(MediaPolicy.GetUnavailableReason(media));
            var source = await MaterializeAsync(media, workspace, cancellationToken).ConfigureAwait(false);
            var wav = Path.Combine(workspace.DirectoryPath, $"layer-{index:D3}.wav");
            progress?.Report(new AudioProgress("Decoding event media", index, plan.Items.Count, media.DisplayName));
            var decoded = await _processes.RunAsync(new ProcessRequest(paths.VgmstreamCli!, ["-i", "-W", "1", "-o", wav, source], workspace.DirectoryPath,
                Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(decoded, $"Could not decode '{media.DisplayName}' for event render");
            EnsureFile(wav, $"No WAV data was created for '{media.DisplayName}'.");
            var decodedMetadata = ReadWavMetadata(wav);
            EnsureWav(decodedMetadata);
            inputMetadata.Add(decodedMetadata);
            inputWavs.Add(wav);
        }

        var renderDurations = new double[plan.Items.Count];
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            if (item.DurationSeconds > 0) renderDurations[index] = item.DurationSeconds;
            else
            {
                var decodedSeconds = (double)inputMetadata[index].SampleFrames / inputMetadata[index].SampleRate;
                var onePass = Math.Max(0, decodedSeconds - Math.Max(0, item.TrimBeginSeconds) + Math.Min(0, item.TrimEndSeconds));
                if (onePass <= 0) throw new AudioToolException($"Decoded media '{item.MediaName}' has no samples after the event clip trims.");
                renderDurations[index] = onePass * Math.Max(1, item.LoopCount) / Math.Pow(2, item.PitchCents / 1200d);
            }
        }

        var output = Path.Combine(workspace.DirectoryPath, "event.wav");
        var args = new List<string> { "-v", "error", "-y" };
        foreach (var input in inputWavs) args.AddRange(["-i", input]);
        var filters = new List<string>();
        var labels = new List<string>();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            var meta = inputMetadata[index];
            var delayMs = checked((long)Math.Round(item.StartSeconds * 1000, MidpointRounding.AwayFromZero));
            var pitch = Math.Pow(2, item.PitchCents / 1200d);
            var chain = new List<string>();
            var decodedDuration = (double)meta.SampleFrames / meta.SampleRate;
            var singlePassDuration = item.SourceDurationSeconds > 0 ? item.SourceDurationSeconds :
                Math.Max(0, decodedDuration - Math.Max(0, item.TrimBeginSeconds) + Math.Min(0, item.TrimEndSeconds));
            var startTrim = item.TrimBeginSeconds > 0 ? item.TrimBeginSeconds : 0;
            chain.Add($"atrim=start={Fmt(startTrim)}:duration={Fmt(singlePassDuration)}");
            chain.Add("asetpts=PTS-STARTPTS");
            if (item.LoopCount > 1)
            {
                var loopFrames = Math.Max(1, checked((long)Math.Round(singlePassDuration * meta.SampleRate)));
                chain.Add($"aloop=loop={item.LoopCount - 1}:size={loopFrames}");
            }
            if (Math.Abs(item.PitchCents) > 0.01)
                chain.Add($"asetrate={Fmt(meta.SampleRate * pitch)},aresample={meta.SampleRate}");
            chain.Add($"atrim=duration={Fmt(renderDurations[index])}");
            if (Math.Abs(item.GainDb) > 0.001) chain.Add($"volume={Fmt(item.GainDb)}dB");
            if (delayMs > 0)
            {
                var delays = string.Join('|', Enumerable.Repeat(delayMs.ToString(CultureInfo.InvariantCulture), Math.Max(1, meta.Channels)));
                chain.Add($"adelay={delays}");
            }
            var label = $"a{index}";
            filters.Add($"[{index}:a:0]{string.Join(',', chain)}[{label}]");
            labels.Add($"[{label}]");
        }
        var maxDuration = plan.Items.Select((item, index) => item.StartSeconds + renderDurations[index]).Max();
        if (plan.MaximumDurationSeconds is { } maximum) maxDuration = Math.Min(maxDuration, maximum);
        if (plan.Items.Count == 1) filters.Add($"[a0]apad=whole_dur={Fmt(maxDuration)},atrim=duration={Fmt(maxDuration)}[mix]");
        else filters.Add($"{string.Join(string.Empty, labels)}amix=inputs={labels.Count}:duration=longest:normalize=0,apad=whole_dur={Fmt(maxDuration)},atrim=duration={Fmt(maxDuration)}[mix]");
        var fade = Math.Min(plan.FadeOutSeconds, Math.Max(0, maxDuration));
        if (fade > 0) filters.Add($"[mix]afade=t=out:st={Fmt(Math.Max(0, maxDuration - fade))}:d={Fmt(fade)}[final]");
        var target = format switch
        {
            WavFormat.Pcm16 => "pcm_s16le",
            WavFormat.Pcm24 => "pcm_s24le",
            WavFormat.Float32 => "pcm_f32le",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        args.AddRange(["-filter_complex", string.Join(';', filters), "-map", fade > 0 ? "[final]" : "[mix]", "-c:a", target, "-f", "wav", output]);
        progress?.Report(new AudioProgress("Mixing event layers", Total: plan.Items.Count));
        var render = await _processes.RunAsync(new ProcessRequest(paths.Ffmpeg!, args, workspace.DirectoryPath,
            Timeout: TimeSpan.FromHours(3)), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(render, "Event mix failed");
        EnsureFile(output, "FFmpeg did not create an event WAV.");
        var metadata = ReadWavMetadata(output);
        EnsureWav(metadata);
        EnsureWavFormat(metadata, format);
        var destination = Path.GetFullPath(destinationWav);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(output, destination, overwrite: true);
        return new AudioRenderResult(destination, metadata.Channels, metadata.SampleRate, metadata.SampleFrames,
            TimeSpan.FromSeconds((double)metadata.SampleFrames / metadata.SampleRate), plan.Limitations);
    }

    public async Task<string> NormalizePreviewToStereoAsync(string wavPath, string destinationWav,
        CancellationToken cancellationToken = default)
    {
        RequireTool(paths.Ffmpeg, "ffmpeg (preview device fallback)");
        var destination = Path.GetFullPath(destinationWav);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".wav";
        try
        {
            var result = await _processes.RunAsync(new ProcessRequest(paths.Ffmpeg!,
                ["-v", "error", "-y", "-i", Path.GetFullPath(wavPath), "-map", "0:a:0", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "wav", staging],
                Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "Could not adapt preview audio to the output device");
            var metadata = ReadWavMetadata(staging);
            EnsureWav(metadata);
            if (metadata.Channels != 2) throw new AudioToolException("Preview fallback is not stereo.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, true);
            return destination;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    internal static string CreatePersistentTempDirectory(string? sourceFingerprint)
    {
        var fingerprintKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceFingerprint ?? "unknown"))).ToLowerInvariant()[..16];
        var path = Path.Combine(Path.GetTempPath(), "DetroitAudioExtractor", fingerprintKey, "preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<string> MaterializeAsync(MediaEntry media, OperationWorkspace workspace, CancellationToken cancellationToken)
    {
        if (media.State is MediaState.MissingExternal or MediaState.Ambiguous or MediaState.Malformed or MediaState.Unsupported)
            throw new AudioToolException($"Media '{media.DisplayName}' cannot be processed ({media.State}).");
        if (media.Slice.Length <= 0 || string.IsNullOrWhiteSpace(media.Slice.FilePath) || !File.Exists(media.Slice.FilePath))
            throw new AudioToolException($"Media '{media.DisplayName}' has no available external data.");
        var sourcePath = Path.GetFullPath(media.Slice.FilePath);
        try { SliceReader.Validate(media.Slice); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AudioToolException($"Media '{media.DisplayName}' points outside available source data.", exception.Message, exception);
        }
        var extension = media.IsRiff || Path.GetExtension(sourcePath).Equals(".wem", StringComparison.OrdinalIgnoreCase) ? ".wem" : Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
        var target = Path.Combine(workspace.DirectoryPath, $"input-{Guid.NewGuid():N}{extension}");
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await SliceReader.CopyAsync(media.Slice, output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return target;
    }

    private async Task<AudioProbeResult> ProbeWithFfprobeAsync(string input, CancellationToken cancellationToken)
    {
        if (paths.Ffprobe is null) return new AudioProbeResult(false, null, null, null, null, "ffprobe is unavailable.");
        var result = await _processes.RunAsync(new ProcessRequest(paths.Ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,duration", "-show_entries", "format=duration", "-of", "json", input],
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return new AudioProbeResult(false, null, null, null, null, JoinDiagnostic(result));
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() == 0) return new AudioProbeResult(false, null, null, null, null, "No audio stream was found.");
            var stream = streams[0];
            int? rate = ParseInt(stream, "sample_rate");
            int? channels = ParseInt(stream, "channels");
            double? duration = ParseDouble(stream, "duration") ??
                (document.RootElement.TryGetProperty("format", out var format) ? ParseDouble(format, "duration") : null);
            var codec = stream.TryGetProperty("codec_name", out var codecElement) ? codecElement.GetString() : null;
            return rate is > 0 && channels is > 0
                ? new AudioProbeResult(true, codec, rate, channels, duration)
                : new AudioProbeResult(false, codec, rate, channels, duration, "ffprobe returned incomplete stream metadata.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new AudioProbeResult(false, null, null, null, null, exception.Message);
        }
    }

    private async Task<AudioProbeResult> ProbeByDecodingAsync(string input, string workspace, CancellationToken cancellationToken)
    {
        var wav = Path.Combine(workspace, "probe-duration.wav");
        var result = await _processes.RunAsync(new ProcessRequest(paths.VgmstreamCli!, ["-i", "-W", "1", "-o", wav, input], workspace,
            Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(wav) || new FileInfo(wav).Length == 0)
            return new AudioProbeResult(false, null, null, null, null, JoinDiagnostic(result));
        try
        {
            var metadata = ReadWavMetadata(wav);
            EnsureWav(metadata);
            return new AudioProbeResult(true, null, metadata.SampleRate, metadata.Channels,
                (double)metadata.SampleFrames / metadata.SampleRate);
        }
        catch (AudioToolException exception)
        {
            return new AudioProbeResult(false, null, null, null, null, exception.Message);
        }
    }

    private async Task<OggValidationResult> DecodeOggForValidationAsync(string input, string workspace, string outputName, CancellationToken cancellationToken)
    {
        if (paths.Ffmpeg is null || !File.Exists(input) || new FileInfo(input).Length < 27)
            return new OggValidationResult(null, null, "ffmpeg or a complete Ogg file is unavailable.");
        var wavPath = Path.Combine(workspace, outputName);
        var result = await _processes.RunAsync(new ProcessRequest(paths.Ffmpeg,
            ["-v", "error", "-xerror", "-y", "-i", input, "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "wav", wavPath],
            workspace, Timeout: TimeSpan.FromHours(1)), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(wavPath) || new FileInfo(wavPath).Length == 0)
            return new OggValidationResult(null, null, JoinDiagnostic(result));
        try
        {
            var metadata = ReadWavMetadata(wavPath);
            EnsureWav(metadata);
            EnsureWavFormat(metadata, WavFormat.Pcm16);
            var stream = ReadOggVorbisInfo(input);
            return new OggValidationResult(metadata, stream, null);
        }
        catch (Exception exception) when (exception is AudioToolException or InvalidDataException or EndOfStreamException)
        { return new OggValidationResult(null, null, exception.Message); }
    }

    private async Task<AudioConversionResult> CommitOggAsync(string input, string destinationPath, string method,
        IReadOnlyList<string> diagnostics, AudioOperation operation, AudioWavMetadata metadata, OggStreamInfo stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(input, staging, overwrite: false);
            File.Move(staging, destination, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new AudioConversionResult(destination, method, stream.Channels, stream.SampleRate, stream.FinalGranule,
            TimeSpan.FromSeconds((double)stream.FinalGranule / stream.SampleRate), diagnostics, operation);
    }

    private static AudioProbeResult ParseVgmstreamProbe(string text)
    {
        var rate = RegexValue(text, @"(?im)^\s*(?:sample\s*rate|sampling\s*rate)\s*[:=]\s*(\d+)\s*(?:Hz)?");
        var channels = RegexValue(text, @"(?im)^\s*channels?\s*[:=]\s*(\d+)");
        var sampleCount = RegexValue(text, @"(?im)^\s*stream\s+total\s+samples\s*:\s*(\d+)");
        var duration = rate is > 0 && sampleCount is > 0
            ? (double)sampleCount.Value / rate.Value
            : RegexDouble(text, @"(?im)^\s*(?:play\s+duration|(?:stream\s*)?length)\s*[:=]\s*(\d+(?:\.\d+)?)\s*(?:s|sec(?:onds?)?)?");
        var codecMatch = System.Text.RegularExpressions.Regex.Match(text, @"(?im)^\s*(?:encoding|codec|format)\s*[:=]\s*(.+)$");
        return rate is > 0 && channels is > 0
            ? new AudioProbeResult(true, codecMatch.Success ? codecMatch.Groups[1].Value.Trim() : null, rate, channels, duration)
            : new AudioProbeResult(false, null, rate, channels, duration, "Could not parse vgmstream metadata.");
    }

    private static int? RegexValue(string text, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? RegexDouble(string text, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public async Task<AudioConversionResult> EncodeWavToOggAsync(
        string wavPath,
        string destinationOgg,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationOgg);
        RequireTool(paths.Ffmpeg, "ffmpeg with libvorbis");
        var source = Path.GetFullPath(wavPath);
        EnsureFile(source, "The source WAV does not exist or is empty.");
        var metadata = ReadWavMetadata(source);
        EnsureWav(metadata);
        var destination = Path.GetFullPath(destinationOgg);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp.ogg");
        var validationWav = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.validation.wav");
        try
        {
            var result = await _processes.RunAsync(new ProcessRequest(paths.Ffmpeg!, ["-v", "error", "-y", "-i", source,
                "-map", "0:a:0", "-c:a", "libvorbis", "-q:a", "5", "-f", "ogg", staging],
                Timeout: TimeSpan.FromHours(2)), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "FFmpeg Ogg Vorbis encode failed");
            var validation = await DecodeOggForValidationAsync(staging, Path.GetDirectoryName(staging)!, Path.GetFileName(validationWav), cancellationToken).ConfigureAwait(false);
            if (validation.Metadata is not { } outputMetadata || validation.Stream is not { } outputStream)
                throw new AudioToolException("The encoded Ogg file did not pass complete decode validation.", validation.Diagnostic);
            if (!FramesMatch(metadata, outputStream) || !DecoderLengthMatches(outputMetadata, outputStream))
                throw new AudioToolException("The encoded Ogg stream does not preserve the source sample range.",
                    DescribeFrameMismatch(metadata, outputStream) + Environment.NewLine + DescribeDecoderMismatch(outputMetadata, outputStream));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
            var rate = outputMetadata.SampleRate;
            var channels = outputMetadata.Channels;
            var frames = outputStream.FinalGranule;
            return new AudioConversionResult(destination, "FFmpeg libvorbis re-encode q5", channels, rate, frames,
                TimeSpan.FromSeconds((double)frames / rate), [], AudioOperation.Reencode);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            if (File.Exists(validationWav)) File.Delete(validationWav);
        }
    }

    private static int? ParseInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static double? ParseDouble(JsonElement element, string name) => element.TryGetProperty(name, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw new AudioToolException(operation + $" (exit code {result.ExitCode}).", JoinDiagnostic(result));
    }

    private static void EnsureFile(string path, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new AudioToolException(message);
    }

    private static void EnsureWav(AudioWavMetadata metadata)
    {
        if (metadata.Channels <= 0 || metadata.SampleRate <= 0 || metadata.BlockAlign <= 0 || metadata.SampleFrames <= 0)
            throw new AudioToolException("WAV validation failed: channels, sample rate, block alignment, or sample count is invalid.");
    }

    private static void EnsureWavFormat(AudioWavMetadata metadata, WavFormat requested)
    {
        var valid = requested switch
        {
            WavFormat.Pcm16 => metadata.FormatCode == 1 && metadata.BitsPerSample == 16,
            WavFormat.Pcm24 => metadata.FormatCode == 1 && metadata.BitsPerSample == 24,
            WavFormat.Float32 => metadata.FormatCode == 3 && metadata.BitsPerSample == 32,
            _ => false
        };
        if (!valid) throw new AudioToolException($"WAV output format does not match requested {requested}.");
    }

    public static AudioWavMetadata ReadWavMetadata(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 44 || new string(reader.ReadChars(4)) != "RIFF") throw new AudioToolException("Output is not a supported RIFF/WAVE file.");
        var declaredSize = reader.ReadUInt32();
        var riffEnd = 8L + declaredSize;
        if (riffEnd > stream.Length || riffEnd < 44) throw new AudioToolException("WAV RIFF size is truncated or malformed.");
        if (new string(reader.ReadChars(4)) != "WAVE") throw new AudioToolException("Output WAV header is malformed.");
        int channels = 0, sampleRate = 0, blockAlign = 0;
        ushort formatCode = 0, bitsPerSample = 0;
        uint? channelMask = null;
        long dataBytes = 0;
        var foundFormat = false;
        while (stream.Position + 8 <= riffEnd)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var dataStart = stream.Position;
            if (size > riffEnd - dataStart) throw new AudioToolException($"WAV '{id}' chunk extends beyond the RIFF container.");
            var available = (long)size;
            if (id == "fmt " && available >= 16)
            {
                formatCode = reader.ReadUInt16(); channels = reader.ReadUInt16(); sampleRate = checked((int)reader.ReadUInt32());
                _ = reader.ReadUInt32(); blockAlign = reader.ReadUInt16(); bitsPerSample = reader.ReadUInt16();
                if (formatCode == 0xFFFE)
                {
                    if (available < 40) throw new AudioToolException("WAVE_FORMAT_EXTENSIBLE format chunk is truncated.");
                    var extensionSize = reader.ReadUInt16();
                    if (extensionSize < 22) throw new AudioToolException("WAVE_FORMAT_EXTENSIBLE extension is too short.");
                    _ = reader.ReadUInt16(); // Valid bits per sample; the container width remains BitsPerSample.
                    channelMask = reader.ReadUInt32();
                    var subFormatBytes = reader.ReadBytes(16);
                    if (subFormatBytes.Length != 16) throw new AudioToolException("WAVE_FORMAT_EXTENSIBLE subformat GUID is truncated.");
                    var subFormat = new Guid(subFormatBytes);
                    if (subFormat == new Guid("00000001-0000-0010-8000-00AA00389B71")) formatCode = 1;
                    else if (subFormat == new Guid("00000003-0000-0010-8000-00AA00389B71")) formatCode = 3;
                    else throw new AudioToolException($"WAVE_FORMAT_EXTENSIBLE subformat '{subFormat}' is not PCM or IEEE float.");
                }
                foundFormat = true;
            }
            else if (id == "data") dataBytes += available;
            stream.Position = dataStart + available + (size % 2 == 1 && dataStart + available < riffEnd ? 1 : 0);
        }
        if (!foundFormat || dataBytes == 0) throw new AudioToolException("WAV is missing its format or audio data chunk.");
        if (blockAlign <= 0 || dataBytes % blockAlign != 0) throw new AudioToolException("WAV data is not aligned to complete sample frames.");
        var frames = blockAlign > 0 ? dataBytes / blockAlign : 0;
        return new AudioWavMetadata(channels, sampleRate, blockAlign, frames, formatCode, bitsPerSample, channelMask);
    }

    private static void RequireTool(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new AudioToolException($"Required tool is unavailable: {name}. Configure the bundled or local tool directory first.");
    }

    private static string JoinDiagnostic(ProcessResult result) => string.Join(Environment.NewLine,
        new[] { result.StandardError.Trim(), result.StandardOutput.Trim() }.Where(s => s.Length > 0));

    private static OggStreamInfo ReadOggVorbisInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        var pageHeader = new byte[27];
        var lacing = new byte[255];
        using var identification = new MemoryStream();
        var headerParsed = false;
        uint? serial = null;
        long finalGranule = -1;
        var isEnd = false;
        var pageCount = 0;
        int channels = 0, sampleRate = 0, shortBlockSize = 0;
        while (stream.Position < stream.Length)
        {
            if (++pageCount > 1_000_000) throw new InvalidDataException("Ogg page count exceeds the validation limit.");
            ReadExactly(stream, pageHeader);
            if (!pageHeader.AsSpan(0, 4).SequenceEqual("OggS"u8) || pageHeader[4] != 0)
                throw new InvalidDataException("Ogg page header or version is invalid.");
            var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(pageHeader.AsSpan(14, 4));
            if (serial is null) serial = pageSerial;
            else if (serial.Value != pageSerial) throw new InvalidDataException("Chained Ogg logical streams are not supported in this validation pass.");
            var segmentCount = pageHeader[26];
            ReadExactly(stream, lacing.AsSpan(0, segmentCount));
            for (var segment = 0; segment < segmentCount; segment++)
            {
                var segmentSize = lacing[segment];
                var bytes = reader.ReadBytes(segmentSize);
                if (bytes.Length != segmentSize) throw new EndOfStreamException("Ogg page body is truncated.");
                if (!headerParsed)
                {
                    if (identification.Length + segmentSize > 64 * 1024) throw new InvalidDataException("Vorbis identification packet exceeds its size limit.");
                    identification.Write(bytes);
                    if (segmentSize < 255)
                    {
                        var header = identification.GetBuffer().AsSpan(0, checked((int)identification.Length));
                        if (header.Length < 30 || header[0] != 1 || !header.Slice(1, 6).SequenceEqual("vorbis"u8) ||
                            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(7, 4)) != 0)
                            throw new InvalidDataException("Ogg stream has no valid Vorbis identification header.");
                        channels = header[11];
                        sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4)));
                        var blockSizes = header[28];
                        shortBlockSize = 1 << (blockSizes & 0x0F);
                        var longBlockSize = 1 << (blockSizes >> 4);
                        if (channels is < 1 or > 255 || sampleRate <= 0 || shortBlockSize < 64 || longBlockSize < shortBlockSize)
                            throw new InvalidDataException("Vorbis stream parameters are invalid.");
                        headerParsed = true;
                    }
                }
            }
            if ((pageHeader[5] & 0x04) != 0)
            {
                if (isEnd) throw new InvalidDataException("Ogg contains more than one end-of-stream page.");
                finalGranule = BinaryPrimitives.ReadInt64LittleEndian(pageHeader.AsSpan(6, 8));
                isEnd = true;
            }
        }
        if (!headerParsed || !isEnd || finalGranule < 0)
            throw new InvalidDataException("Ogg stream is missing its Vorbis header or final granule position.");
        return new OggStreamInfo(channels, sampleRate, finalGranule, shortBlockSize);
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0) throw new EndOfStreamException("Ogg page header is truncated.");
            offset += read;
        }
    }

    private static bool FramesMatchOptional(AudioWavMetadata? expected, OggStreamInfo actual) => expected is { } source && FramesMatch(source, actual);
    private static bool FramesMatch(AudioWavMetadata expected, OggStreamInfo actual) =>
        expected.SampleRate == actual.SampleRate && expected.Channels == actual.Channels && Math.Abs(expected.SampleFrames - actual.FinalGranule) <= 1;
    private static bool DecoderLengthMatches(AudioWavMetadata decoded, OggStreamInfo stream) =>
        decoded.SampleRate == stream.SampleRate && decoded.Channels == stream.Channels &&
        (Math.Abs(decoded.SampleFrames - stream.FinalGranule) <= 1 ||
         Math.Abs(decoded.SampleFrames + stream.ShortBlockSize / 2L - stream.FinalGranule) <= 1);
    private static string DescribeFrameMismatch(AudioWavMetadata expected, OggStreamInfo actual) =>
        $"Expected source: {expected.SampleFrames} sample frames, {expected.SampleRate} Hz, {expected.Channels} channel(s). Ogg final granule: {actual.FinalGranule} frames, {actual.SampleRate} Hz, {actual.Channels} channel(s). Delta: {actual.FinalGranule - expected.SampleFrames} frames.";
    private static string DescribeDecoderMismatch(AudioWavMetadata decoded, OggStreamInfo stream) =>
        $"Full FFmpeg decode produced {decoded.SampleFrames} frames; Ogg final granule is {stream.FinalGranule}, Vorbis short block is {stream.ShortBlockSize} samples.";
    private static string Fmt(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private sealed record OggStreamInfo(int Channels, int SampleRate, long FinalGranule, int ShortBlockSize);
    private sealed record OggValidationResult(AudioWavMetadata? Metadata, OggStreamInfo? Stream, string? Diagnostic);

    private sealed class OperationWorkspace : IAsyncDisposable
    {
        public string DirectoryPath { get; }
        public OperationWorkspace(string? fingerprint)
        {
            var fingerprintKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint ?? "unknown"))).ToLowerInvariant()[..16];
            DirectoryPath = Path.Combine(Path.GetTempPath(), "DetroitAudioExtractor", fingerprintKey, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }
        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
