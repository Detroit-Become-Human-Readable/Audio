using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DetroitAudio.Audio;
using DetroitAudio.Core;

namespace DetroitAudio.Export;

public enum ExportFormat { Original, Bank, BankWithMedia, Ogg, Wav, EventWav, EventOgg }
public enum ExportScope { Selection, Filtered, All }
public sealed record ExportOptions(string Directory, ExportFormat Format, bool Overwrite = false, WavFormat WavFormat = WavFormat.Pcm16, EventOptions? EventOptions = null, ExportScope Scope = ExportScope.Selection,
    bool AllowEmpty = false, IReadOnlyDictionary<string, EventOptions>? EventOverrides = null);
public sealed record ExportItem(string Key, string RelativePath, MediaEntry? Media = null, BankInfo? Bank = null, AudioEvent? Event = null, EventOptions? EventOptions = null);
public sealed record ExportCounts(int Complete, int Fragments, int Unverified, int Unavailable);
public sealed record ExportOmission(string Key, string Reason);
public sealed record ExportPlan(ExportOptions Options, IReadOnlyList<ExportItem> Items, long OriginalBytes, ExportCounts? Counts = null, IReadOnlyList<ExportOmission>? Omitted = null);
public sealed record ExportProgress(int Completed, int Total, string Name, string Status);
public sealed record ExportResult(string Key, string File, string Status, string? Error = null, string? Operation = null, string? Details = null);
public sealed record ExportReport(IReadOnlyList<ExportResult> Results, string ManifestPath, bool Cancelled);

public static class OutputNaming
{
    public static string Segment(string name, int maximum = 110)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '<', '>', '|', '?', '*' }).ToHashSet();
        var value = new string(name.Normalize(NormalizationForm.FormC).Where(c => c >= 32 && !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
        if (value.Length == 0 || value is "." or "..") value = "Unnamed";
        var stem = value.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) value = "_" + value;
        if (value.Length > maximum) value = value[..maximum];
        if (value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
        return value.TrimEnd('.', ' ');
    }

    public static string Suffix(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

    public static string Resolve(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("The output path is outside the selected folder.");
        var current = Path.GetDirectoryName(full);
        while (current is not null && current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The output path passes through a linked folder.");
            current = Path.GetDirectoryName(current);
        }
        return full;
    }
}

public sealed class ExportService(AudioService audio)
{
    public ExportPlan Plan(AudioCatalog catalog, ExportOptions options, IEnumerable<MediaEntry>? media = null, IEnumerable<BankInfo>? banks = null, IEnumerable<AudioEvent>? events = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(options.Directory);
        if (catalog.Resources.Count > 0 || catalog.IndexedResourceCount > 0)
        {
            var sourceRoot = (Directory.Exists(catalog.SourcePath) ? catalog.SourcePath : Path.GetDirectoryName(catalog.SourcePath))!;
            sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((root + Path.DirectorySeparatorChar).StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose an export folder outside the game installation.");
        }
        var items = new List<ExportItem>();
        var selectedMedia = (options.Scope == ExportScope.All ? catalog.Media : media ?? []).DistinctBy(m => m.Key).ToList();
        var omitted = new List<ExportOmission>();
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        bool Available(MediaEntry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MediaPolicy.HasBytes(entry)) return false;
            if (!files.TryGetValue(entry.Slice.FilePath, out var length))
            {
                try { length = new FileInfo(entry.Slice.FilePath).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { length = -1; }
                files[entry.Slice.FilePath] = length;
            }
            return entry.Slice.Offset >= 0 && entry.Slice.Offset <= length && entry.Slice.Length <= length - entry.Slice.Offset;
        }
        if (options.Format is ExportFormat.Bank or ExportFormat.BankWithMedia)
        {
            foreach (var bank in (banks ?? []).DistinctBy(b => b.Key))
            {
                items.Add(new(bank.Key, OutputNaming.Segment(bank.Name) + "__" + bank.Id.ToString("X8") + "__" + OutputNaming.Suffix(bank.Key) + ".bnk", Bank: bank));
                if (options.Format == ExportFormat.BankWithMedia) selectedMedia.AddRange(catalog.Media.Where(m => m.BankKey == bank.Key));
            }
        }
        if (options.Format is ExportFormat.EventWav or ExportFormat.EventOgg)
        {
            if (options.EventOptions?.AllowPartialMedia == true) throw new InvalidOperationException("Event exports require complete audio streams.");
            foreach (var entry in (events ?? []).DistinctBy(e => e.Key))
            {
                if (entry.Behavior == EventBehavior.Control && entry.MediaKeys.Count == 0) { omitted.Add(new(entry.Key, "No audio assigned.")); continue; }
                var eventOptions = options.EventOverrides?.GetValueOrDefault(entry.Key) ?? options.EventOptions;
                if (entry.Behavior == EventBehavior.StateChange && entry.RelatedPlayEventKeys.Count > 1 && eventOptions?.RelatedPlayEventKey is null)
                { omitted.Add(new(entry.Key, "Choose a playback event for this state.")); continue; }
                items.Add(new(entry.Key, OutputNaming.Segment(entry.DisplayName) + "__" + entry.Id.ToString("X8") + "__" + OutputNaming.Suffix(entry.Key) + (options.Format == ExportFormat.EventOgg ? ".ogg" : ".wav"), Event: entry, EventOptions: eventOptions));
            }
        }
        else if (options.Format != ExportFormat.Bank)
        {
            foreach (var entry in selectedMedia.DistinctBy(m => m.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Available(entry)) { omitted.Add(new(entry.Key, "Source bytes are unavailable.")); continue; }
                if (options.Format is ExportFormat.Wav or ExportFormat.Ogg && !MediaPolicy.CanConvert(entry))
                { omitted.Add(new(entry.Key, entry.Completeness == MediaCompleteness.Fragment ? "Fragment excluded from conversion." : MediaPolicy.GetUnavailableReason(entry))); continue; }
                var ext = options.Format switch { ExportFormat.Wav => ".wav", ExportFormat.Ogg => ".ogg", _ => entry.Codec == "MIDI" ? ".mid" : entry.IsRiff ? ".wem" : ".bin" };
                if (options.Format is ExportFormat.Original or ExportFormat.BankWithMedia && entry.Completeness != MediaCompleteness.Complete)
                    ext = (entry.Completeness == MediaCompleteness.Fragment ? "__fragment" : "__unverified") + ext;
                var relative = Path.Combine(OutputNaming.Segment(string.IsNullOrEmpty(entry.BankName) ? "Dialogue" : entry.BankName, 65), OutputNaming.Segment(entry.Language, 20),
                    OutputNaming.Segment(entry.DisplayName, 90) + "__" + entry.Id.ToString("X8") + "__" + OutputNaming.Suffix(entry.Key) + ext);
                if (options.Format is ExportFormat.Original or ExportFormat.BankWithMedia && entry.Completeness != MediaCompleteness.Complete)
                    relative = Path.Combine(entry.Completeness == MediaCompleteness.Fragment ? "Fragments" : "Unverified", relative);
                items.Add(new(entry.Key, relative, Media: entry));
            }
        }
        if (items.Count == 0 && options.Scope != ExportScope.All && !options.AllowEmpty) throw new InvalidOperationException(omitted.Count > 0 ? "No eligible audio is available for this export." : "Select audio, a bank or an event to export.");
        foreach (var item in items)
        {
            var destination = OutputNaming.Resolve(root, item.RelativePath);
            if (destination.Length > 245) throw new IOException("The selected folder produces paths longer than 245 characters. Choose a shorter folder path.");
            if (item.Media is { } entry && string.Equals(destination, Path.GetFullPath(entry.Slice.FilePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("The destination is the source file.");
            if (item.Bank is { } bank && string.Equals(destination, Path.GetFullPath(bank.Slice.FilePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("The destination is the source file.");
        }
        var unique = selectedMedia.DistinctBy(entry => entry.Key).ToArray();
        var counts = new ExportCounts(unique.Count(entry => Available(entry) && MediaPolicy.IsComplete(entry)),
            unique.Count(entry => Available(entry) && entry.Completeness == MediaCompleteness.Fragment),
            unique.Count(entry => Available(entry) && !MediaPolicy.IsComplete(entry) && entry.Completeness != MediaCompleteness.Fragment),
            unique.Count(entry => !Available(entry)));
        return new(options with { Directory = root }, items, items.Sum(i => i.Media?.Slice.Length ?? i.Bank?.Slice.Length ?? 0), counts, omitted);
    }

    public async Task<ExportReport> RunAsync(AudioCatalog catalog, ExportPlan plan, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(plan.Options.Directory);
        var resultSlots = new ExportResult?[plan.Items.Count];
        var cancelled = false;
        var completed = 0;
        try
        {
        await Parallel.ForEachAsync(Enumerable.Range(0, plan.Items.Count), new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (index, workerToken) =>
        {
            var item = plan.Items[index];
            var destination = OutputNaming.Resolve(plan.Options.Directory, item.RelativePath);
            if (File.Exists(destination) && !plan.Options.Overwrite)
            {
                resultSlots[index] = new(item.Key, item.RelativePath, "Skipped"); progress?.Report(new(Interlocked.Increment(ref completed), plan.Items.Count, Path.GetFileName(destination), "Skipped")); return;
            }
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp" + Path.GetExtension(destination);
            var decoded = temporary + ".wav";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                progress?.Report(new(completed, plan.Items.Count, Path.GetFileName(destination), "Exporting"));
                string operation = "Copy";
                if (item.Bank is { } bank) await CopyAsync(bank.Slice, temporary, cancellationToken);
                else if (item.Event is { } entry)
                {
                    var eventOptions = item.EventOptions ?? plan.Options.EventOptions ?? new EventOptions();
                    if (eventOptions.AllowPartialMedia) throw new AudioToolException("Event exports require complete audio streams.");
                    var playback = await audio.CreateEventPlanAsync(catalog, entry, eventOptions, cancellationToken: cancellationToken);
                    await audio.RenderAsync(catalog, playback, plan.Options.Format == ExportFormat.EventOgg ? decoded : temporary, plan.Options.WavFormat, cancellationToken: cancellationToken);
                    if (plan.Options.Format == ExportFormat.EventOgg) await audio.EncodeWavToOggAsync(decoded, temporary, cancellationToken);
                    operation = "Event render";
                }
                else if (item.Media is { } media)
                {
                    if (plan.Options.Format is ExportFormat.Wav or ExportFormat.Ogg) MediaPolicy.RequireConversion(media);
                    if (plan.Options.Format == ExportFormat.Wav) { await audio.DecodeToWavAsync(catalog, media, temporary, plan.Options.WavFormat, cancellationToken: cancellationToken); operation = "Decode"; }
                    else if (plan.Options.Format == ExportFormat.Ogg) { var converted = await audio.ConvertToOggAsync(catalog, media, temporary, cancellationToken: cancellationToken); operation = converted.Operation.ToString(); }
                    else await CopyAsync(media.Slice, temporary, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("The export produced an empty file.");
                File.Move(temporary, destination, plan.Options.Overwrite);
                resultSlots[index] = new(item.Key, item.RelativePath, "Complete", Operation: operation);
                progress?.Report(new(Interlocked.Increment(ref completed), plan.Items.Count, Path.GetFileName(destination), "Complete"));
            }
            catch (OperationCanceledException) { resultSlots[index] = new(item.Key, item.RelativePath, "Cancelled"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { resultSlots[index] = new(item.Key, item.RelativePath, "Failed", ex.Message, Details: (ex as AudioToolException)?.Diagnostic); progress?.Report(new(Interlocked.Increment(ref completed), plan.Items.Count, Path.GetFileName(destination), "Failed")); }
            finally { DeleteOwned(temporary); DeleteOwned(decoded); }
        });
        }
        catch (OperationCanceledException) { cancelled = true; }
        cancelled |= cancellationToken.IsCancellationRequested;
        var results = plan.Items.Select((item, index) => resultSlots[index] ?? new ExportResult(item.Key, item.RelativePath, "Cancelled")).ToArray();
        var manifestPath = Path.Combine(plan.Options.Directory, "export-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var references = new ManifestReferenceMapper(catalog, plan);
        var manifest = new
        {
            version = 3, source = Path.GetFileName(catalog.SourcePath), fingerprint = catalog.Fingerprint, format = plan.Options.Format.ToString(), scope = plan.Options.Scope.ToString(), wavFormat = plan.Options.WavFormat.ToString(),
            eventSettings = plan.Options.EventOptions is { } eventOptions ? new
            {
                eventOptions.Language, eventOptions.RandomSeed, eventOptions.DefaultLoopCount, eventOptions.FadeOutSeconds, eventOptions.MaximumDurationSeconds,
                eventOptions.SwitchValueId, eventOptions.SwitchValues, eventOptions.AllowPartialMedia,
                relatedPlayEventKey = eventOptions.RelatedPlayEventKey is null ? null : references.Key(eventOptions.RelatedPlayEventKey), eventOptions.StateValues
            } : null,
            eventOverrides = plan.Options.EventOverrides?.Select(pair => new
            {
                key = references.Key(pair.Key), pair.Value.Language, pair.Value.RandomSeed, pair.Value.DefaultLoopCount, pair.Value.FadeOutSeconds,
                pair.Value.MaximumDurationSeconds, pair.Value.SwitchValueId, pair.Value.SwitchValues, pair.Value.AllowPartialMedia,
                relatedPlayEventKey = pair.Value.RelatedPlayEventKey is null ? null : references.Key(pair.Value.RelatedPlayEventKey), pair.Value.StateValues
            }),
            cancelled, counts = plan.Counts,
            omitted = plan.Omitted?.Select(item => new { Key = references.Key(item.Key), Reason = references.Text(item.Reason, item.Key) }),
            entries = plan.Items.Zip(results, (item, result) => new
            {
                Key = references.Key(result.Key), result.File, result.Status,
                Error = references.Error(result.Error, result.Key, item.Media?.Slice.FilePath, item.Bank?.Slice.FilePath), result.Operation,
                id = item.Media?.Id ?? item.Bank?.Id ?? item.Event?.Id,
                language = item.Media?.Language,
                rawLanguageId = item.Media?.RawLanguageId,
                storedName = item.Media is { } named && named.Names.Any(name => name.Kind == NameKind.Stored && name.Text == named.Name) ? named.Name : null,
                displayLabel = item.Media?.DisplayName,
                category = item.Media?.Category,
                midi = item.Media?.Midi,
                eventKeys = item.Media?.EventKeys.Select(references.Key),
                sourceFile = references.SourcePath(item.Media?.Slice.FilePath ?? item.Bank?.Slice.FilePath),
                offset = item.Media?.Slice.Offset ?? item.Bank?.Slice.Offset,
                length = item.Media?.Slice.Length ?? item.Bank?.Slice.Length,
                state = item.Media?.State.ToString(),
                completeness = item.Media?.Completeness.ToString(),
                container = item.Media?.ContainerValidity.ToString(),
                availability = item.Media?.Availability.ToString(),
                resolution = references.Text(item.Media?.ResolutionDetails, MediaReferences(item.Media)),
                sourceEvidence = item.Media?.SourceEvidence.Select(evidence => new { BankKey = references.Key(evidence.BankKey), evidence.ObjectId, evidence.Declaration, evidence.Offset }),
                equivalenceEvidence = item.Media?.EquivalenceEvidence.Select(evidence => new
                {
                    evidence.Sha256,
                    SourceLocations = evidence.SourceLocations.Select(slice => new { FilePath = references.SourcePath(slice.FilePath), slice.Offset, slice.Length }),
                    evidence.Comparison
                }),
                candidateRejections = item.Media?.CandidateRejections.Select(rejection => new { file = references.SourcePath(rejection.Slice.FilePath), rejection.Slice.Offset, rejection.Slice.Length, Reason = references.Text(rejection.Reason, rejection.Slice.FilePath) }),
                names = item.Media?.Names.Select(n => new { n.Text, n.Namespace, n.Id, kind = n.Kind.ToString(), source = references.NameSource(n.Source), n.Offset, n.Confidence, n.HashMethod, n.CollisionGroup, n.InputSha256 })
            }).ToArray()
        };
        await using (var file = File.Create(manifestPath)) await JsonSerializer.SerializeAsync(file, manifest, new JsonSerializerOptions { WriteIndented = true }, CancellationToken.None);
        return new(results, manifestPath, cancelled);
    }

    private static async Task CopyAsync(DataSlice slice, string destination, CancellationToken token)
    {
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        await SliceReader.CopyAsync(slice, file, token);
    }

    private static string?[] MediaReferences(MediaEntry? media)
    {
        if (media is null) return [];
        return new string?[] { media.Key, media.BankKey, media.Slice.FilePath }
            .Concat(media.EventKeys)
            .Concat(media.SourceEvidence.Select(evidence => evidence.BankKey))
            .Concat(media.Names.Select(name => name.Source))
            .Concat(media.Candidates.Select(slice => slice.FilePath))
            .Concat(media.CandidateRejections.Select(rejection => rejection.Slice.FilePath))
            .Concat(media.EquivalenceEvidence.SelectMany(evidence => evidence.SourceLocations).Select(slice => slice.FilePath))
            .ToArray();
    }

    private static void DeleteOwned(string path) { if (File.Exists(path)) File.Delete(path); }
}
