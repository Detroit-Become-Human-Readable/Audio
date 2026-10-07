using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DetroitAudio.Core;

namespace DetroitAudio.Formats;

/// <summary>Reads the managed, metadata-only view of Detroit: Become Human PC audio resources.</summary>
public sealed class DetroitCatalogReader : IExternalWemInventoryCatalogReader
{
    private const int IndexHeaderLength = 105;
    private const int IndexRecordLength = 28;
    private const int MaximumIndexBytes = 128 * 1024 * 1024;
    private const int MaximumWrapperBytes = 16 * 1024 * 1024;
    private const int MaximumProbeBytes = 4 * 1024;
    private const uint WwiseVorbisPluginId = 0x00040001;
    private const byte LanguageSpecificSourceFlag = 1;
    private static readonly byte[] IndexMagic = Encoding.ASCII.GetBytes("QUANTICDREAMTABINDEX");
    private static readonly byte[] BankContainerMagic = Encoding.ASCII.GetBytes("CSNDBKDT");
    private static readonly byte[] DialogueMagic = Encoding.ASCII.GetBytes("CSNDDATA");
    private static readonly byte[] EventMagic = Encoding.ASCII.GetBytes("CSNDEVNT");
    private static readonly byte[] LocalizationMagic = Encoding.ASCII.GetBytes("LOCALIZ_");
    private static readonly byte[] RiffMagic = Encoding.ASCII.GetBytes("RIFF");
    private static readonly byte[] MidiMagic = MidiReader.WrapperMagic.ToArray();

    public Task<AudioCatalog> ReadAsync(string path, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ReadAsync(path, ScanExternalInventory(path, cancellationToken), progress, cancellationToken);

    public async Task<AudioCatalog> ReadAsync(string path, ExternalWemInventory inventory,
        IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var indexPath = Path.Combine(fullPath, "BigFile_PC.idx");
            if (!File.Exists(indexPath)) throw new FileNotFoundException("The selected directory does not contain BigFile_PC.idx.", indexPath);
            return await ReadIndexAsync(indexPath, inventory, progress, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected source does not exist.", fullPath);
        return Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".idx" => await ReadIndexAsync(fullPath, inventory, progress, cancellationToken).ConfigureAwait(false),
            ".bnk" => await ReadStandaloneBankAsync(fullPath, inventory, progress, cancellationToken).ConfigureAwait(false),
            ".wem" => await ReadStandaloneWemAsync(fullPath, progress, cancellationToken).ConfigureAwait(false),
            ".mid" or ".midi" => ReadStandaloneMidi(fullPath, progress, cancellationToken),
            ".dat" => await ReadSegmentAsync(fullPath, inventory, progress, cancellationToken).ConfigureAwait(false),
            _ when IsDataSegmentName(fullPath) => await ReadSegmentAsync(fullPath, inventory, progress, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException("Open an installation folder, a PC .idx index, a .dat/.dNN segment, a .bnk bank, a .wem file, or a .mid file.")
        };
    }

    private static async Task<AudioCatalog> ReadIndexAsync(string indexPath, ExternalWemInventory externalInventory, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var catalog = new AudioCatalog { SourcePath = Path.GetFullPath(indexPath) };
        var directory = Path.GetDirectoryName(indexPath)!;
        var indexInfo = new FileInfo(indexPath);
        if (indexInfo.Length < IndexHeaderLength || indexInfo.Length > MaximumIndexBytes)
            throw new InvalidDataException("The PC index has an unsupported size.");

        List<ResourceRecord> resources;
        using (var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
        {
            var header = new byte[IndexHeaderLength];
            await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
            if (!header.AsSpan(0, IndexMagic.Length).SequenceEqual(IndexMagic))
                throw new InvalidDataException("The selected index does not have the Detroit PC index signature.");
            var version = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
            if (version != 18) throw new InvalidDataException($"Unsupported Detroit PC index version {version}; only version 18 is supported.");

            var tableLength = indexInfo.Length - IndexHeaderLength;
            var count = tableLength / IndexRecordLength;
            if (count > int.MaxValue) throw new InvalidDataException("The index contains too many resource records.");
            var remainder = tableLength % IndexRecordLength;
            if (remainder != 0)
                catalog.Diagnostics.Add(new Diagnostic("IndexTrailingBytes", $"The index ends with {remainder} byte(s) that do not form a complete record."));

            resources = new List<ResourceRecord>((int)count);
            var segmentInfo = Enumerable.Range(0, 30).ToDictionary(package => package, package =>
            {
                var source = SegmentPath(directory, package);
                return File.Exists(source) ? (Path: source, Length: new FileInfo(source).Length) : (Path: source, Length: -1L);
            });
            var row = new byte[IndexRecordLength];
            var occurrences = new Dictionary<(uint Type, uint Id), int>();
            for (long ordinal = 0; ordinal < count; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                await ReadExactlyAsync(stream, row, token).ConfigureAwait(false);
                var type = BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(0, 4));
                var flags = BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(4, 4));
                var id = BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(8, 4));
                var offset = BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(12, 4));
                var size = BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(16, 4));
                var alternativeSize = BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(20, 4));
                var package = BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(24, 4));
                var key = (type, id);
                var occurrence = occurrences.GetValueOrDefault(key);
                occurrences[key] = occurrence + 1;
                var filePath = segmentInfo.TryGetValue((int)package, out var packageInfo)
                    ? packageInfo.Path : SegmentPath(directory, (int)package);
                var recordKey = $"pc:{type:X8}:{id:X8}:{package}:{offset:X8}:{occurrence}";
                var slice = new DataSlice(filePath, offset, size);
                if (segmentInfo.TryGetValue((int)package, out packageInfo) && packageInfo.Length >= 0)
                {
                    var sourceLength = packageInfo.Length;
                    if (offset > sourceLength || size > sourceLength - offset)
                        catalog.Diagnostics.Add(new Diagnostic("ResourceRangeOutOfBounds", "An index record points outside its package segment.", recordKey));
                }
                else
                {
                    catalog.Diagnostics.Add(new Diagnostic("MissingPackageSegment", $"Package segment {package} is not present beside the index.", recordKey));
                }

                resources.Add(new ResourceRecord
                {
                    Key = recordKey,
                    Type = type,
                    Id = id,
                    Flags = flags,
                    AlternativeSize = alternativeSize,
                    Package = checked((int)package),
                    Occurrence = occurrence,
                    Slice = slice
                });
                if (alternativeSize != 0)
                    catalog.Diagnostics.Add(new Diagnostic("AlternativeSizeUninterpreted",
                        "The index carries a nonzero alternate-size field; both sizes are preserved and no compression algorithm is inferred.", recordKey));

                if ((ordinal & 0x3FFF) == 0)
                    progress?.Report(new IndexProgress("Reading index", (int)ordinal, (int)count));
            }
        }

        catalog.Resources = resources;
        catalog.IndexedResourceCount = resources.Count;
        if (resources.Count == 0) throw new InvalidDataException("The index has no resource records.");
        using var archive = new PackageReader();
        await PopulateCatalogAsync(catalog, archive, externalInventory, progress, token).ConfigureAwait(false);
        return catalog;
    }

    private static async Task PopulateCatalogAsync(AudioCatalog catalog, PackageReader archive, ExternalWemInventory inventory,
        IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var bankContainers = catalog.Resources.Where(resource => resource.Type == 29)
            .GroupBy(resource => resource.Id).ToDictionary(group => group.Key, group => group.First());
        var bankNameById = new Dictionary<uint, (string Name, ResourceRecord Resource, bool Verified)>();
        var bankDescriptors = catalog.Resources.Where(resource => resource.Type == 1022).ToList();
        var bankNameEvidenceByDescriptor = new Dictionary<string, int>(StringComparer.Ordinal);
        var namedEvents = new Dictionary<uint, string>();
        var dialogueEvents = new Dictionary<uint, string>();
        var dialogueResourceNames = new Dictionary<uint, string>();
        var dialogueEventByResource = new Dictionary<uint, AudioEvent>();
        var eventRecords = catalog.Resources.Where(resource => resource.Type is 1023 or 1031).ToList();
        var headerRecords = catalog.Resources.Where(resource => resource.Type == 4091).ToList();
        var audioResources = catalog.Resources.Where(resource => resource.Type == 1033).ToList();
        var localizationResources = catalog.Resources.Where(resource => resource.Type == 1016).ToList();
        var mediaByResource = new Dictionary<uint, List<MediaEntry>>();
        var eventIndex = 0;
        var completed = 0;

        foreach (var descriptor in bankDescriptors)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = archive.ReadResource(descriptor, MaximumWrapperBytes);
                if (!TryParseBankDescriptor(bytes, out var bankId, out var bankName, out var containerId))
                {
                    catalog.Diagnostics.Add(new Diagnostic("MalformedBankDescriptor", "The bank descriptor did not contain a bounded name, ID, and data-container reference.", descriptor.Key));
                    continue;
                }
                var verified = Fnv1LowerUtf8(bankName) == bankId;
                bankNameEvidenceByDescriptor[descriptor.Key] = catalog.Names.Count;
                catalog.Names.Add(new NameEvidence(bankName, "WwiseBank", bankId, NameKind.Stored, descriptor.Key, descriptor.Slice.Offset));
                if (bankNameById.TryGetValue(bankId, out var collision) && !string.Equals(collision.Name, bankName, StringComparison.Ordinal))
                    catalog.Diagnostics.Add(new Diagnostic("DuplicateBankId", "Multiple stored bank names reference the same bank ID.", descriptor.Key));
                else
                    bankNameById[bankId] = (bankName, descriptor, verified);

                bankContainers.TryGetValue(containerId, out var container);
                if (container is null)
                {
                    catalog.Diagnostics.Add(new Diagnostic("MissingBankContainer", $"The descriptor refers to missing data-container resource {containerId}.", descriptor.Key));
                    catalog.Banks.Add(new BankInfo { Key = descriptor.Key, Id = bankId, Name = bankName,
                        Slice = new DataSlice(descriptor.Slice.FilePath, descriptor.Slice.Offset, 0),
                        Diagnostics = [new Diagnostic("MissingBankContainer", "The descriptor's data container is absent.", descriptor.Key)] });
                    continue;
                }

                if (!archive.TryFindEmbeddedBank(container.Slice, token, out var bankOffset, out var bankLength, out var bankDiagnostic))
                {
                    var diagnostic = new Diagnostic(bankDiagnostic, "The referenced data container did not contain a complete, structurally valid BKHD bank payload.", descriptor.Key);
                    catalog.Diagnostics.Add(diagnostic);
                    catalog.Banks.Add(new BankInfo { Key = descriptor.Key, Id = bankId, Name = bankName,
                        Slice = new DataSlice(container.Slice.FilePath, container.Slice.Offset, 0), Diagnostics = [diagnostic] });
                    continue;
                }

                if (!archive.TryReadBankHeader(container.Slice.FilePath, checked(container.Slice.Offset + bankOffset), bankLength, out var actualVersion, out var actualId))
                {
                    catalog.Diagnostics.Add(new Diagnostic("MalformedBankPayload", "The selected CSNDBKDT member did not contain a valid BKHD chunk.", descriptor.Key));
                    continue;
                }
                var name = actualId == bankId ? bankName : actualId.ToString(CultureInfo.InvariantCulture);
                var bankKey = descriptor.Key;
                var dataSlice = new DataSlice(container.Slice.FilePath, checked(container.Slice.Offset + bankOffset), bankLength);
                var bank = ParseBankFromSlice(bankKey, name, actualId, dataSlice, bankNameById, catalog, archive);
                if (actualId != bankId)
                    bank.Diagnostics.Add(new Diagnostic("BankIdMismatch", $"Stored descriptor ID {bankId} disagrees with BKHD ID {actualId}; the descriptor name was not attached to the bank.", descriptor.Key));
                else
                {
                    if (verified && bankNameEvidenceByDescriptor.TryGetValue(descriptor.Key, out var evidenceIndex))
                        catalog.Names[evidenceIndex] = catalog.Names[evidenceIndex] with { Kind = NameKind.Verified, Confidence = "High", HashMethod = "FNV-1 lower UTF-8" };
                }
                catalog.Banks.Add(bank);
                EnrichEventsFromBank(catalog, bank, namedEvents);
                completed++;

                if (completed == 1)
                    PublishSnapshot(progress, catalog, "Reading banks", completed, bankDescriptors.Count, bankName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                catalog.Diagnostics.Add(new Diagnostic("BankParseError", ex.Message, descriptor.Key));
            }
            if ((++eventIndex & 0x3F) == 0)
                progress?.Report(new IndexProgress("Reading banks", eventIndex, bankDescriptors.Count, descriptor.Key));
        }

        ResolveAllBankMedia(catalog, token, inventory);

        foreach (var resource in eventRecords)
        {
            token.ThrowIfCancellationRequested();
            if (archive.TryReadResource(resource, MaximumWrapperBytes, out var bytes) && TryParseEventResource(bytes, resource.Type == 1031, out var wwiseId, out var name))
            {
                if (resource.Type == 1031)
                {
                    dialogueEvents[wwiseId] = name;
                    dialogueResourceNames[resource.Id] = name;
                }
                else namedEvents[wwiseId] = name;
                var key = resource.Type == 1031
                    ? $"event:dialogue-resource:{resource.Id:X8}:{resource.Occurrence}"
                    : $"event:sound:{wwiseId:X8}:{resource.Occurrence}";
                var eventRecord = new AudioEvent { Key = key, Id = wwiseId, Name = name, IsDialogue = resource.Type == 1031 };
                eventRecord.Names.Add(new NameEvidence(name, "WwiseEvent", wwiseId, NameKind.Stored, resource.Key, resource.Slice.Offset));
                catalog.Names.AddRange(eventRecord.Names);
                var existing = resource.Type == 1023
                    ? catalog.Events.Where(item => item.Key == key || (!item.IsDialogue && item.Id == wwiseId && item.BankKey.Length > 0)).ToList()
                    : catalog.Events.Where(item => item.Key == key).ToList();
                if (existing.Count == 0) catalog.Events.Add(eventRecord);
                else
                {
                    foreach (var item in existing)
                    {
                        item.Name = name;
                        if (item.BankKey.Length == 0) item.Key = key;
                        item.Names.AddRange(eventRecord.Names);
                    }
                    eventRecord = existing[0];
                }
                if (resource.Type == 1031) dialogueEventByResource[resource.Id] = eventRecord;
            }
            else
                catalog.Diagnostics.Add(new Diagnostic("MalformedEventMetadata", "A CSNDEVNT resource could not be parsed using its declared record lengths.", resource.Key));
        }

        // The 4091 COM_CONT records are the authoritative bridge from named dialogue headers to
        // their type-1033 audio resources and type-1031 dialogue-event resources.
        var audioToDialogue = new Dictionary<uint, List<(uint EventId, string HeaderName, ResourceRecord Header)>>();
        foreach (var header in headerRecords)
        {
            token.ThrowIfCancellationRequested();
            if (!archive.TryReadResource(header, MaximumWrapperBytes, out var bytes) || !TryParseComContainer(bytes, out var references))
            {
                catalog.Diagnostics.Add(new Diagnostic("MalformedDialogueHeader", "The dialogue header did not contain a valid COM_CONT reference table.", header.Key));
                continue;
            }
            var audioIds = references.Where(reference => reference.Type == 1033).Select(reference => reference.Id).Distinct().ToArray();
            var eventIds = references.Where(reference => reference.Type == 1031).Select(reference => reference.Id).Distinct().ToArray();
            var headerName = string.Empty;
            if (audioIds.Length != 1 || eventIds.Length != 1)
            {
                catalog.Diagnostics.Add(new Diagnostic("DialogueReferenceCardinality", "A dialogue header did not resolve to exactly one audio resource and one dialogue event.", header.Key));
                continue;
            }
            if (!audioToDialogue.TryGetValue(audioIds[0], out var links)) audioToDialogue[audioIds[0]] = links = [];
            links.Add((eventIds[0], headerName, header));
        }

        var localizationIndex = 0;
        foreach (var resource in localizationResources)
        {
            token.ThrowIfCancellationRequested();
            if (!archive.TryReadResource(resource, MaximumWrapperBytes, out var bytes))
            {
                catalog.Diagnostics.Add(new Diagnostic("LocalizationResourceTooLarge", "The localization resource exceeds the bounded metadata read limit.", resource.Key));
                continue;
            }
            ParseLocalization(bytes, resource, catalog);
            localizationIndex++;
            if ((localizationIndex & 0x3F) == 0)
                progress?.Report(new IndexProgress("Reading localization", localizationIndex, localizationResources.Count));
        }
        var localizationByKey = catalog.Localization
            .GroupBy(text => $"{text.Key}\0{LocalizationLanguageName(text.Language)}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var audioIndex = 0;
        foreach (var resource in audioResources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!TryParseDialogueMedia(archive, resource, out var parsed))
                {
                    catalog.Diagnostics.Add(new Diagnostic("MalformedDialogueMedia", "The CSNDDATA resource did not contain a valid stored name and bounded audio extent.", resource.Key));
                    continue;
                }

                var entry = MakeMediaEntry(resource.Key, resource, parsed.Name, parsed.Language, parsed.MediaSlice,
                    parsed.State, parsed.IsRiff, parsed.Codec, parsed.SampleRate, parsed.Channels, parsed.Duration, parsed.Validity);
                catalog.Names.AddRange(entry.Names);
                if (audioToDialogue.TryGetValue(resource.Id, out var links))
                {
                    foreach (var link in links)
                    {
                        var eventId = link.EventId;
                        var eventName = dialogueResourceNames.GetValueOrDefault(eventId, eventId.ToString(CultureInfo.InvariantCulture));
                        dialogueEventByResource.TryGetValue(eventId, out var linkedEvent);
                        entry.EventKeys.Add(linkedEvent?.Key ?? $"event:dialogue-resource:{eventId:X8}:0");
                        entry.Aliases.Add(eventName);
                        if (!string.IsNullOrWhiteSpace(link.HeaderName))
                            entry.Names.Add(new NameEvidence(link.HeaderName, "DialogueHeader", resource.Id, NameKind.Stored, link.Header.Key, link.Header.Slice.Offset));
                    }
                }
                var stem = DialogueStem(entry.Name);
                if (stem.Length > 0 && localizationByKey.TryGetValue($"{stem}\0{entry.Language}", out var localized)) entry.Subtitle = localized.Text;
                catalog.Media.Add(entry);
                if (!mediaByResource.TryGetValue(resource.Id, out var entries)) mediaByResource[resource.Id] = entries = [];
                entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                catalog.Diagnostics.Add(new Diagnostic("DialogueMediaParseError", ex.Message, resource.Key));
            }
            audioIndex++;
            if ((audioIndex & 0x3FF) == 0)
                progress?.Report(new IndexProgress("Reading dialogue media", audioIndex, audioResources.Count));
        }

        var midiResources = catalog.Resources.Where(resource => resource.Type == 65).ToArray();
        for (var index = 0; index < midiResources.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var resource = midiResources[index];
            try
            {
                var document = MidiReader.ReadWrapped(resource.Slice, token);
                var entry = MakeMidiEntry(resource.Key, resource.Id, document); catalog.Media.Add(entry); catalog.Names.AddRange(entry.Names);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { catalog.Diagnostics.Add(new("MalformedMidiResource", error.Message, resource.Key)); }
            if ((index & 255) == 0) progress?.Report(new("Reading MIDI", index + 1, midiResources.Length));
        }
        CatalogRelationships.Build(catalog, token);

        MarkNameCollisions(catalog);
        progress?.Report(new IndexProgress("Catalogue ready", catalog.Resources.Count, catalog.Resources.Count));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static AudioCatalog ReadStandaloneMidi(string path, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var document = MidiReader.ReadStandard(new(path, 0, new FileInfo(path).Length), token);
        var entry = MakeMidiEntry("midi:standalone", 0, document);
        if (document.Names.Count == 0)
        {
            entry.Name = Path.GetFileNameWithoutExtension(path); entry.NameKind = NameKind.Stored;
            entry.Names.Add(new(entry.Name, "Filename", 0, NameKind.Stored, path));
        }
        var catalog = new AudioCatalog { SourcePath = path, Media = [entry] }; catalog.Names.AddRange(entry.Names);
        progress?.Report(new("Standalone MIDI ready", 1, 1, Snapshot: CreateSnapshot(catalog))); return catalog;
    }

    private static MediaEntry MakeMidiEntry(string key, uint id, MidiDocument document)
    {
        var names = document.Names.ToList(); return new MediaEntry
        {
            Key = key, Id = id, Name = names.FirstOrDefault()?.Text ?? id.ToString(CultureInfo.InvariantCulture), BankName = "MIDI",
            Category = "MIDI", Codec = "MIDI", Language = "SFX", Slice = document.Slice, Midi = document.Metadata,
            IsRiff = false, State = MediaState.CompleteEmbedded, Completeness = MediaCompleteness.Complete,
            ContainerValidity = ContainerValidity.Valid, Availability = SourceAvailability.Available,
            Names = names, Aliases = names.Select(name => name.Text).Distinct(StringComparer.Ordinal).ToList(),
            NameKind = names.Count > 0 ? NameKind.Stored : NameKind.Description
        };
    }

    private static async Task<AudioCatalog> ReadStandaloneBankAsync(string path, ExternalWemInventory inventory, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var catalog = new AudioCatalog { SourcePath = Path.GetFullPath(path) };
        var length = new FileInfo(path).Length;
        token.ThrowIfCancellationRequested();
        var slice = new DataSlice(Path.GetFullPath(path), 0, length);
        var standaloneArchive = new PackageReader();
        using (standaloneArchive)
        {
            var bank = ParseBankFromSlice("bank:standalone", Path.GetFileNameWithoutExtension(path), 0, slice,
                new Dictionary<uint, (string, ResourceRecord, bool)>(), catalog, standaloneArchive);
            catalog.Banks.Add(bank);
            ResolveAllBankMedia(catalog, token, inventory);
            EnrichEventsFromBank(catalog, bank, new Dictionary<uint, string>());
            CatalogRelationships.Build(catalog, token);
        }
        progress?.Report(new IndexProgress("Standalone bank ready", 1, 1, Snapshot: CreateSnapshot(catalog)));
        return catalog;
    }

    private static async Task<AudioCatalog> ReadStandaloneWemAsync(string path, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var catalog = new AudioCatalog { SourcePath = Path.GetFullPath(path) };
        var slice = new DataSlice(Path.GetFullPath(path), 0, new FileInfo(path).Length);
        var stem = Path.GetFileNameWithoutExtension(path);
        var hasNumericId = uint.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var mediaId);
        var key = hasNumericId ? $"wem:standalone:{mediaId:X8}" : "wem:standalone:filename";
        var resource = new ResourceRecord { Key = key, Type = 0, Id = mediaId, Slice = slice };
        var probe = ProbeWem(slice.FilePath, 0, slice.Length);
        probe = probe with { Slice = probe.Slice with { FilePath = slice.FilePath } };
        var entry = MakeMediaEntry(resource.Key, resource, stem, "SFX", probe.Slice, probe.State, probe.IsRiff, probe.Codec, probe.SampleRate, probe.Channels, probe.Duration, probe.Validity);
        entry.Names.Clear();
        entry.NameKind = NameKind.Stored;
        entry.Names.Add(new NameEvidence(stem, "Filename", mediaId, NameKind.Stored, resource.Key, 0));
        catalog.Names.AddRange(entry.Names);
        catalog.Media.Add(entry);
        progress?.Report(new IndexProgress("Standalone WEM ready", 1, 1, Snapshot: CreateSnapshot(catalog)));
        await Task.CompletedTask.ConfigureAwait(false);
        return catalog;
    }

    private static async Task<AudioCatalog> ReadSegmentAsync(string path, ExternalWemInventory inventory, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var catalog = new AudioCatalog { SourcePath = Path.GetFullPath(path) };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024 + 32];
        var carry = 0;
        long baseOffset = 0;
        var found = 0;
        var seen = new HashSet<long>();
        var midiExtents = new List<(long Start, long End)>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), token).ConfigureAwait(false);
            if (read == 0) break;
            var available = carry + read;
            ScanMagic(buffer.AsSpan(0, available), MidiMagic, baseOffset - carry, path, catalog, seen, ref found, token, midiExtents);
            ScanMagic(buffer.AsSpan(0, available), DialogueMagic, baseOffset - carry, path, catalog, seen, ref found, token, midiExtents);
            ScanMagic(buffer.AsSpan(0, available), BankContainerMagic, baseOffset - carry, path, catalog, seen, ref found, token, midiExtents);
            var keep = Math.Min(Math.Max(MidiMagic.Length, Math.Max(DialogueMagic.Length, BankContainerMagic.Length)) + 32, available);
            carry = keep;
            Buffer.BlockCopy(buffer, available - keep, buffer, 0, keep);
            baseOffset += read;
            if ((baseOffset & ((1 << 23) - 1)) < read)
                progress?.Report(new IndexProgress("Limited segment scan", (int)Math.Min(baseOffset, int.MaxValue), (int)Math.Min(stream.Length, int.MaxValue)));
        }
        ResolveAllBankMedia(catalog, token, inventory);
        CatalogRelationships.Build(catalog, token);
        progress?.Report(new IndexProgress("Limited segment scan complete", found, found, Snapshot: CreateSnapshot(catalog)));
        return catalog;
    }

    private static void ScanMagic(ReadOnlySpan<byte> window, byte[] magic, long windowOffset, string path, AudioCatalog catalog, HashSet<long> seen, ref int found, CancellationToken token = default, List<(long Start, long End)>? midiExtents = null)
    {
        var relative = 0;
        while (relative <= window.Length - magic.Length)
        {
            var hit = window[relative..].IndexOf(magic);
            if (hit < 0) return;
            var position = relative + hit;
            var absolute = windowOffset + position;
            if (midiExtents?.Any(extent => absolute >= extent.Start && absolute < extent.End) == true) { relative = position + 1; continue; }
            if (magic == MidiMagic && !seen.Contains(absolute))
            {
                try
                {
                    var document = MidiReader.ReadWrapped(new(path, absolute, new FileInfo(path).Length - absolute), token);
                    var entry = MakeMidiEntry($"scan:midi:{absolute:X}", unchecked((uint)absolute), document);
                    catalog.Media.Add(entry); catalog.Names.AddRange(entry.Names); seen.Add(absolute); found++;
                    midiExtents?.Add((absolute, checked(document.Slice.Offset + document.Slice.Length)));
                }
                catch (InvalidDataException) { }
            }
            if (magic == DialogueMagic && !seen.Contains(absolute) && TryParseDialogueMediaAt(path, absolute, out var parsed))
            {
                var slice = new DataSlice(Path.GetFullPath(path), parsed.AudioOffset, parsed.MediaSlice.Length);
                catalog.Media.Add(new MediaEntry
                {
                    Key = $"scan:{absolute:X}", Id = unchecked((uint)absolute), Name = parsed.Name, Language = parsed.Language,
                    Category = "Dialogue", Codec = parsed.Codec, SampleRate = parsed.SampleRate, Channels = parsed.Channels,
                    Duration = parsed.Duration, Slice = slice, State = parsed.State, IsRiff = parsed.IsRiff,
                    Completeness = parsed.State == MediaState.CompleteEmbedded ? MediaCompleteness.Complete : MediaCompleteness.Unverified,
                    Availability = SourceAvailability.Available, ContainerValidity = parsed.Validity,
                    NameKind = NameKind.Stored,
                    Names = [new NameEvidence(parsed.Name, "CSNDDATA", unchecked((uint)absolute), NameKind.Stored, "Limited segment scan", absolute)]
                });
                seen.Add(absolute);
                found++;
            }
            else if (magic == BankContainerMagic && !seen.Contains(absolute) && TryParseEmbeddedBankAt(path, absolute, out var bank, out var media))
            {
                catalog.Banks.Add(bank);
                catalog.Media.AddRange(media);
                seen.Add(absolute);
                found++;
            }
            relative = position + 1;
        }
    }

    private static bool TryParseEmbeddedBankAt(string path, long markerOffset, out BankInfo bank, out List<MediaEntry> media)
    {
        bank = new BankInfo();
        media = [];
        var fileLength = new FileInfo(path).Length;
        if (markerOffset < 0 || markerOffset > fileLength - 16) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        stream.Position = markerOffset + 12;
        Span<byte> field = stackalloc byte[4];
        stream.ReadExactly(field);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(field);
        var bankStart = markerOffset + 16;
        if (length is < 12 || bankStart > fileLength || length > fileLength - bankStart) return false;
        var catalog = new AudioCatalog();
        var slice = new DataSlice(Path.GetFullPath(path), bankStart, length);
        using var archive = new PackageReader();
        if (!archive.TryReadBankHeader(slice.FilePath, slice.Offset, slice.Length, out _, out var headerId)) return false;
        bank = ParseBankFromSlice($"scan:bank:{markerOffset:X}", headerId.ToString(CultureInfo.InvariantCulture), headerId, slice,
            new Dictionary<uint, (string, ResourceRecord, bool)>(), catalog, archive);
        media = catalog.Media;
        bank.Diagnostics.Add(new Diagnostic("LimitedScanSource", "This bank was discovered by a bounded signature scan; its archive descriptor and name are unavailable.", bank.Key));
        return true;
    }

    private static bool IsDataSegmentName(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("BigFile_PC.d", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan("BigFile_PC.d".Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static string SegmentPath(string directory, int package) => package switch
    {
        0 => Path.Combine(directory, "BigFile_PC.dat"),
        >= 1 and <= 99 => Path.Combine(directory, $"BigFile_PC.d{package:D2}"),
        _ => Path.Combine(directory, $"BigFile_PC.package-{package}")
    };

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[done..], token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The index ended before its declared record table was complete.");
            done += read;
        }
    }

    private static bool TryParseComContainer(ReadOnlySpan<byte> bytes, out List<ResourceReference> references)
    {
        references = [];
        if (bytes.Length < 20 || !bytes[..8].SequenceEqual("COM_CONT"u8)) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);
        if (version != 6 || count > 4096 || payloadLength > bytes.Length - 16 || 20L + count * 9L > bytes.Length) return false;
        var position = 20;
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 4, 4));
            references.Add(new ResourceReference(type, id));
            position += 9;
        }
        return true;
    }

    private static bool TryParseBankDescriptor(ReadOnlySpan<byte> bytes, out uint bankId, out string bankName, out uint containerId)
    {
        bankId = 0; bankName = string.Empty; containerId = 0;
        if (!TryParseComContainer(bytes, out var refs)) return false;
        var containerRefs = refs.Where(r => r.Type == 29).Select(r => r.Id).Distinct().ToArray();
        if (containerRefs.Length != 1) return false;
        containerId = containerRefs[0];
        var marker = bytes.IndexOf("CSNDBNK_"u8);
        if (marker < 0 || marker + 16 > bytes.Length) return false;
        var pos = marker + 8;
        var recordVersion = ReadUInt32(bytes, ref pos);
        var recordSize = ReadUInt32(bytes, ref pos);
        if (recordVersion != 2 || recordSize > bytes.Length - pos) return false;
        var end = pos + checked((int)recordSize);
        if (!TryReadLengthPrefixedAscii(bytes, ref pos, end, 64, out var idText) ||
            !TryReadLengthPrefixedAscii(bytes, ref pos, end, 512, out bankName) ||
            !uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out bankId)) return false;
        return !string.IsNullOrWhiteSpace(bankName);
    }

    private static bool TryParseEventResource(ReadOnlySpan<byte> bytes, bool dialogue, out uint eventId, out string name)
    {
        eventId = 0; name = string.Empty;
        var marker = bytes.IndexOf(EventMagic);
        if (marker < 0 || marker + 16 > bytes.Length) return false;
        var hasQzip = bytes.Length >= 5 && bytes[..5].SequenceEqual("QZIP\0"u8) && marker == 5;
        var hasContainer = bytes.Length >= 16 && bytes[..8].SequenceEqual("COM_CONT"u8);
        if (!hasQzip && !hasContainer) return false;
        if (hasContainer)
        {
            var containerVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
            var containerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
            if (containerVersion is not (5 or 6) || containerSize > bytes.Length - 16) return false;
        }
        var pos = marker + EventMagic.Length;
        var version = ReadUInt32(bytes, ref pos);
        var declared = ReadUInt32(bytes, ref pos);
        if (version > 1 || declared > bytes.Length - pos + (version == 0 ? 4L : 0L)) return false;
        var end = checked((int)Math.Min(bytes.Length, pos + (long)declared));
        if (!TryReadLengthPrefixedAscii(bytes, ref pos, end, 64, out var idText) ||
            !TryReadLengthPrefixedAscii(bytes, ref pos, end, 4096, out name) ||
            !uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out eventId)) return false;
        return true;
    }

    private static bool TryParseComContainerName(ReadOnlySpan<byte> bytes, byte[] targetTag, out string name)
    {
        name = string.Empty;
        var marker = bytes.IndexOf(targetTag);
        if (marker < 0 || marker + 20 > bytes.Length) return false;
        var pos = marker + targetTag.Length;
        _ = ReadUInt32(bytes, ref pos);
        var declared = ReadUInt32(bytes, ref pos);
        if (declared > bytes.Length - pos) return false;
        var end = pos + checked((int)declared);
        _ = TryReadLengthPrefixedAscii(bytes, ref pos, end, 64, out _);
        return TryReadLengthPrefixedAscii(bytes, ref pos, end, 4096, out name);
    }

    private static bool TryReadLengthPrefixedAscii(ReadOnlySpan<byte> bytes, ref int position, int end, int maximum, out string text)
    {
        text = string.Empty;
        if (position > end - 4) return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
        position += 4;
        if (length > maximum || length > end - position) return false;
        var value = bytes.Slice(position, (int)length);
        if (value.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0) return false;
        text = Encoding.ASCII.GetString(value);
        position += (int)length;
        return true;
    }

    private static bool TryParseDialogueMedia(PackageReader archive, ResourceRecord resource, out ParsedDialogue parsed)
    {
        parsed = default;
        if (resource.Slice.Length < 64) return false;
        var headerLength = checked((int)Math.Min(resource.Slice.Length, 4096));
        var header = archive.ReadAt(resource.Slice.FilePath, resource.Slice.Offset, headerLength);
        return TryParseDialogueHeader(header, resource, out parsed) &&
            ProbeDialoguePayload(archive, resource, parsed, out parsed);
    }

    private static bool TryParseDialogueMediaAt(string path, long markerOffset, out ParsedDialogue parsed)
    {
        parsed = default;
        // Signature windows may end inside the variable-length name. Read the bounded header
        // at its absolute offset instead of treating the current scan window as a resource.
        var fileLength = new FileInfo(path).Length;
        if (markerOffset < 5 || markerOffset > fileLength - 16) return false;
        using var archive = new PackageReader();
        var resource = new ResourceRecord { Slice = new DataSlice(Path.GetFullPath(path), markerOffset - 5, fileLength - markerOffset + 5) };
        var header = archive.ReadAt(path, resource.Slice.Offset, (int)Math.Min(4096, resource.Slice.Length));
        return TryParseDialogueHeader(header, resource, out parsed) && ProbeDialoguePayload(archive, resource, parsed, out parsed);
    }

    private static bool TryParseDialogueHeader(ReadOnlySpan<byte> header, ResourceRecord resource, out ParsedDialogue parsed)
    {
        parsed = default;
        if (header.Length < 21 || !header[..5].SequenceEqual("QZIP\0"u8)) return false;
        var marker = header.IndexOf(DialogueMagic);
        if (marker != 5 || marker + 16 > header.Length) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(marker + DialogueMagic.Length, 4));
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(marker + DialogueMagic.Length + 4, 4));
        if (version != 0 || declared > resource.Slice.Length - 21 || declared == 0) return false;
        var fixedNameOffset = marker + 0x2C;
        if (fixedNameOffset + 4 > header.Length) return false;
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(fixedNameOffset, 4));
        if (nameLength is 0 or > 512 || fixedNameOffset + 4L + nameLength + 4 > header.Length) return false;
        var name = Encoding.UTF8.GetString(header.Slice(fixedNameOffset + 4, (int)nameLength));
        if (name.Any(char.IsControl)) return false;
        var audioLengthOffset = fixedNameOffset + 4 + (int)nameLength;
        var audioLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(audioLengthOffset, 4));
        var audioRelative = checked(audioLengthOffset + 4);
        if (audioLength == 0 || audioRelative > resource.Slice.Length || audioLength > resource.Slice.Length - audioRelative)
            return false;
        var audioOffset = checked(resource.Slice.Offset + audioRelative);
        if (audioRelative + audioLength > 21L + declared) return false;
        parsed = new ParsedDialogue(name, ParseLanguage(name), new DataSlice(resource.Slice.FilePath, audioOffset, audioLength), audioOffset, audioLength,
            MediaState.Unsupported, false, "Unknown", 0, 0, null, ContainerValidity.Unsupported);
        return true;
    }

    private static bool ProbeDialoguePayload(PackageReader archive, ResourceRecord resource, ParsedDialogue parsed, out ParsedDialogue result)
    {
        result = parsed;
        var probe = archive.ProbeWem(parsed.MediaSlice.FilePath, parsed.AudioOffset, parsed.AudioLength);
        result = parsed with { MediaSlice = probe.Slice with { FilePath = parsed.MediaSlice.FilePath }, State = probe.State, IsRiff = probe.IsRiff, Codec = probe.Codec, SampleRate = probe.SampleRate, Channels = probe.Channels, Duration = probe.Duration, Validity = probe.Validity };
        return probe.Slice.Length > 0;
    }

    private static ParsedProbe ProbeWem(string path, long offset, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return ProbeWem(stream.SafeFileHandle, offset, length);
    }

    private static ParsedProbe ProbeWem(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, long length)
    {
        if (length < 8) return new ParsedProbe(new DataSlice(string.Empty, offset, Math.Max(0, length)), MediaState.Malformed, false, "Unknown", 0, 0, null, ContainerValidity.Truncated);
        var probeLength = (int)Math.Min(length, MaximumProbeBytes);
        var bytes = new byte[probeLength];
        ReadAtExactly(handle, offset, bytes);
        if (!bytes.AsSpan(0, 4).SequenceEqual(RiffMagic))
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Unsupported, false, "Unknown", 0, 0, null, ContainerValidity.Unsupported);
        var riffLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) + 8;
        if (riffLength < 12 || riffLength > length)
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, "Unknown", 0, 0, null, riffLength > length ? ContainerValidity.Truncated : ContainerValidity.Malformed);

        if (!bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, "Unknown", 0, 0, null, ContainerValidity.Malformed);
        var codec = "Unknown";
        var sampleRate = 0;
        var channels = 0;
        long? sampleCount = null;
        long position = 12;
        var foundFormat = false;
        var foundData = false;
        var chunks = 0;
        while (position <= riffLength - 8)
        {
            if (++chunks > 4096) return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, sampleRate, channels, null, ContainerValidity.Malformed);
            var chunkHeader = new byte[8];
            ReadAtExactly(handle, offset + position, chunkHeader);
            var tag = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            var payload = position + 8;
            if (chunkSize > riffLength - payload)
                return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, sampleRate, channels, null, ContainerValidity.Malformed);
            if (tag == "fmt " && chunkSize >= 16)
            {
                var formatBytes = new byte[(int)Math.Min(chunkSize, 66)];
                ReadAtExactly(handle, offset + payload, formatBytes);
                var format = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(2, 2));
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.AsSpan(4, 4));
                if (rate > int.MaxValue || channels == 0 || rate == 0)
                    return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, 0, channels, null, ContainerValidity.Malformed);
                sampleRate = (int)rate;
                codec = format switch { 1 => "PCM", 2 => "Wwise ADPCM", 0x69 => "XMA", 0xFFFF => "Wwise Vorbis", 0x0165 => "AAC", _ => $"Wwise format 0x{format:X4}" };
                foundFormat = true;
                if (format == 0xFFFF && chunkSize >= 28) sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.AsSpan(24, 4));
            }
            else if (tag == "vorb" && chunkSize >= 4)
            {
                var count = new byte[4]; ReadAtExactly(handle, offset + payload, count);
                sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(count);
            }
            else if (tag == "data") foundData = true;
            position = payload + chunkSize;
            if ((chunkSize & 1) != 0 && position < riffLength) position++;
        }
        if (!foundFormat || !foundData || position != riffLength)
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, sampleRate, channels, null, ContainerValidity.Malformed);
        double? duration = sampleCount is > 0 && sampleRate > 0 ? sampleCount.Value / (double)sampleRate : null;
        return new ParsedProbe(new DataSlice(string.Empty, offset, riffLength), MediaState.CompleteEmbedded, true, codec, sampleRate, channels, duration, ContainerValidity.Valid);
    }

    private static void ReadAtExactly(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var amount = RandomAccess.Read(handle, destination[read..], checked(offset + read));
            if (amount == 0) throw new EndOfStreamException("The package ended during a bounded media-header read.");
            read += amount;
        }
    }

    private static MediaEntry MakeMediaEntry(string key, ResourceRecord resource, string name, string language, DataSlice mediaSlice,
        MediaState state, bool isRiff, string codec, int sampleRate, int channels, double? duration, ContainerValidity validity = ContainerValidity.Unknown)
    {
        var entry = new MediaEntry
        {
            Key = key, Id = resource.Id, Name = name, Language = language, Category = language == "SFX" ? "Unknown" : "Dialogue",
            Codec = codec, SampleRate = sampleRate, Channels = channels, Duration = duration, Slice = mediaSlice,
            State = state, IsRiff = isRiff, NameKind = NameKind.Stored,
            Completeness = state == MediaState.CompleteEmbedded ? MediaCompleteness.Complete : MediaCompleteness.Unverified,
            ContainerValidity = validity,
            Availability = mediaSlice.Length > 0 ? SourceAvailability.Available : SourceAvailability.Missing
        };
        if (resource.Type != 0 || key == "wem:standalone")
            entry.Names.Add(new NameEvidence(name, language == "SFX" ? "Media" : "Dialogue", resource.Id, NameKind.Stored, resource.Key, resource.Slice.Offset));
        return entry;
    }

    private static BankInfo ParseBankFromSlice(string key, string name, uint expectedId, DataSlice bankSlice,
        IReadOnlyDictionary<uint, (string Name, ResourceRecord Resource, bool Verified)> knownNames, AudioCatalog catalog, PackageReader archive)
    {
        var bank = new BankInfo { Key = key, Name = name, Id = expectedId, Slice = bankSlice };
        var chunks = new Dictionary<string, (long Offset, uint Length)>();
        long position = 0;
        var chunkCount = 0;
        while (position <= bankSlice.Length - 8)
        {
            if (++chunkCount > 4096)
            {
                bank.Diagnostics.Add(new Diagnostic("BankChunkCountLimit", "The bank contains too many chunks to parse safely.", key));
                break;
            }
            var header = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + position), 8);
            var tag = Encoding.ASCII.GetString(header, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (size > bankSlice.Length - position - 8)
            {
                bank.Diagnostics.Add(new Diagnostic("MalformedBankChunk", $"Chunk {tag} extends beyond the declared bank payload.", key));
                break;
            }
            var payloadOffset = position + 8;
            bank.Chunks.Add(new BankChunk(tag, new DataSlice(bankSlice.FilePath, checked(bankSlice.Offset + payloadOffset), size)));
            chunks[tag] = (payloadOffset, size);
            position = checked(payloadOffset + size);
        }
        if (position != bankSlice.Length)
            bank.Diagnostics.Add(new Diagnostic("BankChunkTableTail", $"The bank has {bankSlice.Length - position} byte(s) outside complete chunk headers.", key));

        if (!chunks.TryGetValue("BKHD", out var headerChunk) || headerChunk.Length < 8)
        {
            bank.Diagnostics.Add(new Diagnostic("MissingBankHeader", "The bank has no complete BKHD header.", key));
            return bank;
        }
        var bkhdLength = checked((int)Math.Min(headerChunk.Length, 16));
        var bkhd = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + headerChunk.Offset), bkhdLength);
        bank.Version = BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(0, 4));
        bank.Id = BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(4, 4));
        bank.LanguageId = bkhd.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(8, 4)) : 0;
        bank.HasFeedback = bank.Version <= 126 && bkhd.Length >= 16 &&
            (BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(12, 4)) & 1) != 0;
        if (bank.Version is not (118 or 120))
            bank.Diagnostics.Add(new Diagnostic("UnsupportedBankVersion", $"Wwise bank version {bank.Version} is preserved, but HIRC fields were not interpreted.", key));

        var didxRows = new List<(uint Id, uint Offset, uint Size, int Occurrence)>();
        long dataOffset = -1;
        long dataLength = 0;
        if (chunks.TryGetValue("DIDX", out var didx))
        {
            if (didx.Length % 12 != 0)
                bank.Diagnostics.Add(new Diagnostic("MalformedDIDX", "The DIDX chunk length is not divisible by its 12-byte record size.", key));
            if (didx.Length > MaximumWrapperBytes)
                bank.Diagnostics.Add(new Diagnostic("DIDXSizeLimit", "The DIDX table exceeds the metadata read limit; embedded media slices remain available through the bank bytes.", key));
            else
            {
                var bytes = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + didx.Offset), checked((int)didx.Length));
                var mediaOccurrences = new Dictionary<uint, int>();
                for (var local = 0; local + 12 <= bytes.Length; local += 12)
                {
                    var row = bytes.AsSpan(local, 12);
                    var mediaId = BinaryPrimitives.ReadUInt32LittleEndian(row[..4]);
                    var mediaOffset = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4, 4));
                    var mediaSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4));
                    var occurrence = mediaOccurrences.GetValueOrDefault(mediaId);
                    mediaOccurrences[mediaId] = occurrence + 1;
                    didxRows.Add((mediaId, mediaOffset, mediaSize, occurrence));
                }
            }
        }
        if (chunks.TryGetValue("DATA", out var data))
        {
            dataOffset = data.Offset;
            dataLength = data.Length;
        }
        else if (didxRows.Count > 0)
            bank.Diagnostics.Add(new Diagnostic("MissingDATA", "The bank has DIDX entries but no DATA chunk.", key));

        foreach (var (id, offset, size, occurrence) in didxRows)
        {
            if (dataOffset < 0 || offset > dataLength || size > dataLength - offset)
            {
                var missingKey = $"{key}:media:{id:X8}:{occurrence}";
                catalog.Media.Add(new MediaEntry { Key = missingKey, Id = id, BankKey = key, BankName = name,
                    Name = id.ToString(CultureInfo.InvariantCulture), Slice = new DataSlice(bankSlice.FilePath, bankSlice.Offset, 0),
                    Availability = SourceAvailability.Missing, ContainerValidity = ContainerValidity.Malformed,
                    State = MediaState.Malformed, NameKind = NameKind.Stored, Category = "Malformed embedded media" });
                bank.Diagnostics.Add(new Diagnostic("MediaRangeOutOfBounds", $"DIDX media {id} points outside DATA; its location is retained as malformed.", key));
                continue;
            }
            var absolute = checked(bankSlice.Offset + dataOffset + offset);
            var probe = archive.ProbeWem(bankSlice.FilePath, absolute, size);
            var entryKey = $"{key}:media:{id:X8}:{occurrence}";
            var resource = new ResourceRecord { Key = entryKey, Type = 0, Id = id, Occurrence = occurrence, Slice = new DataSlice(bankSlice.FilePath, absolute, size) };
            var media = MakeMediaEntry(entryKey, resource, id.ToString(CultureInfo.InvariantCulture), "SFX", probe.Slice, probe.State,
                probe.IsRiff, probe.Codec, probe.SampleRate, probe.Channels, probe.Duration, probe.Validity);
            media.BankKey = key;
            media.BankName = name;
            media.RawLanguageId = bank.LanguageId;
            media.Completeness = MediaCompleteness.Unverified;
            if (probe.State == MediaState.Unsupported) media.Category = "Other";
            catalog.Media.Add(media);
        }

        if (chunks.TryGetValue("HIRC", out var hirc))
        {
            if (bank.Version is 118 or 120 && hirc.Length <= MaximumWrapperBytes)
            {
                var hircBytes = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + hirc.Offset), checked((int)hirc.Length));
                ParseHirc(bank, hircBytes, catalog, checked(bankSlice.Offset + hirc.Offset));
            }
            else if (bank.Version is 118 or 120)
                bank.Diagnostics.Add(new Diagnostic("HIRCSizeLimit", "HIRC metadata exceeds the bounded parsing limit; raw bank export remains available.", key));
            else
                bank.Diagnostics.Add(new Diagnostic("UnsupportedHIRC", "Unknown HIRC objects were preserved as bounded opaque chunks.", key));
        }
        if (!chunks.ContainsKey("DIDX")) bank.Diagnostics.Add(new Diagnostic("NoEmbeddedMedia", "The bank has no DIDX embedded-media table.", key));
        if (chunks.TryGetValue("STID", out var strings) && strings.Length <= MaximumWrapperBytes)
        {
            var bytes = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + strings.Offset), checked((int)strings.Length));
            if (bytes.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 1)
            {
                var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
                var offset = 8; var names = new List<NameEvidence>();
                for (uint index = 0; index < count && index < 100_000; index++)
                {
                    if (offset > bytes.Length - 5) { names.Clear(); break; }
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                    var length = bytes[offset + 4]; offset += 5;
                    if (length > bytes.Length - offset) { names.Clear(); break; }
                    var text = Encoding.UTF8.GetString(bytes, offset, length).TrimEnd('\0');
                    if (text.Length > 0) names.Add(new(text, "WwiseBank", id, NameKind.Stored, key, bankSlice.Offset + strings.Offset + offset));
                    offset += length;
                }
                if (count <= 100_000 && names.Count == count) catalog.Names.AddRange(names);
                else bank.Diagnostics.Add(new("MalformedSTID", "The bank string mapping is truncated or exceeds the entry limit.", key));
            }
            else bank.Diagnostics.Add(new("UnsupportedSTID", "This string mapping type is not supported.", key));
        }
        return bank;
    }

    private static void ParseHirc(BankInfo bank, ReadOnlySpan<byte> bytes, AudioCatalog catalog, long hircOffset)
    {
        if (bytes.Length < 4)
        {
            bank.Diagnostics.Add(new Diagnostic("MalformedHIRC", "The HIRC object count is truncated.", bank.Key));
            return;
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        if (count > 500_000)
        {
            bank.Diagnostics.Add(new Diagnostic("HIRCObjectCountLimit", "The HIRC object count exceeds the safety limit.", bank.Key));
            return;
        }
        var position = 4;
        for (uint i = 0; i < count; i++)
        {
            if (position > bytes.Length - 9)
            {
                bank.Diagnostics.Add(new Diagnostic("MalformedHIRC", "The HIRC object header is truncated.", bank.Key));
                return;
            }
            var type = bytes[position];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 1, 4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 5, 4));
            if (size < 4 || size > bytes.Length - position - 5)
            {
                bank.Diagnostics.Add(new Diagnostic("MalformedHIRCObject", $"HIRC object {id} has an invalid declared size.", bank.Key));
                return;
            }
            var payloadLength = checked((int)size - 4);
            var payload = bytes.Slice(position + 9, payloadLength);
            var item = new BankObject { Id = id, Type = type, BodySlice = new DataSlice(bank.Slice.FilePath, checked(hircOffset + position + 9), payloadLength) };
            WwiseNodeReader.Parse(item, payload, bank.Version, bank.HasFeedback);
            if (bank.Objects.ContainsKey(id))
                bank.Diagnostics.Add(new Diagnostic("DuplicateHircObjectId", $"HIRC ID {id} occurs more than once in this bank.", bank.Key));
            else
                bank.Objects.Add(id, item);
            position = checked(position + 5 + (int)size);
        }
        if (position != bytes.Length)
            bank.Diagnostics.Add(new Diagnostic("HIRCUnparsedTail", $"HIRC contains {bytes.Length - position} trailing byte(s).", bank.Key));
        LinkBankObjects(bank);
    }

    private static void ParseLocalization(ReadOnlySpan<byte> bytes, ResourceRecord resource, AudioCatalog catalog)
    {
        var marker = bytes.IndexOf(LocalizationMagic);
        if (marker < 0)
        {
            catalog.Diagnostics.Add(new Diagnostic("LocalizationSignatureMissing", "The resource does not contain a LOCALIZ_ payload.", resource.Key));
            return;
        }
        var start = marker + LocalizationMagic.Length;
        if (start > bytes.Length - 4) { catalog.Diagnostics.Add(new("MalformedLocalization", "The localization version field is truncated.", resource.Key)); return; }
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start, 4));
        int body;
        uint languageCount;
        int end;
        if (version == 6)
        {
            if (start + 12 > bytes.Length) { catalog.Diagnostics.Add(new Diagnostic("MalformedLocalization", "Version 6 localization header is truncated.", resource.Key)); return; }
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 4, 4));
            languageCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 8, 4));
            body = start + 12;
            end = Math.Min(bytes.Length, checked(body + (int)Math.Min(declared, (uint)(bytes.Length - body))));
        }
        else if (version == 5)
        {
            if (start + 13 > bytes.Length) { catalog.Diagnostics.Add(new Diagnostic("MalformedLocalization", "Version 5 localization header is truncated.", resource.Key)); return; }
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 4, 4));
            languageCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 9, 4));
            body = start + 13;
            end = Math.Min(bytes.Length, checked(body + (int)Math.Min(declared, (uint)(bytes.Length - body))));
        }
        else
        {
            catalog.Diagnostics.Add(new Diagnostic("UnsupportedLocalizationVersion", $"Localization version {version} is preserved but not interpreted.", resource.Key));
            return;
        }
        if (languageCount > 64)
        {
            catalog.Diagnostics.Add(new Diagnostic("LocalizationLanguageCountLimit", "The localization language count exceeds the safety limit.", resource.Key));
            return;
        }

        var pointerCapacity = TryParseComContainer(bytes, out var comReferences) ? comReferences.Count : 0;

        var cursor = body;
        var unresolvedPointers = 0;
        for (var languageNumber = 0; languageNumber < languageCount && cursor < end; languageNumber++)
        {
            if (cursor > end - 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4)) != 0x00000301)
            {
                catalog.Diagnostics.Add(new Diagnostic("MalformedLocalizationLanguage", "A language block has an invalid marker or truncated header.", resource.Key));
                return;
            }
            var language = Encoding.ASCII.GetString(bytes.Slice(cursor + 5, 3));
            cursor += 8;
            while (cursor <= end - 8)
            {
                // The next language marker terminates this language's records.
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4)) == 0x00000301) break;
                var keyCountOrFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor + 4, 4));
                cursor += 8;
                if (keyLength == 0) break;

                if (keyLength <= 256 && keyLength <= end - cursor && IsAsciiKey(bytes.Slice(cursor, (int)keyLength)))
                {
                    var keyOffset = cursor;
                    var key = Encoding.ASCII.GetString(bytes.Slice(cursor, (int)keyLength));
                    cursor += (int)keyLength;
                    if (cursor > end - 4) break;
                    var textLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                    cursor += 4;
                    if ((textLength & 1) != 0 || textLength > end - cursor)
                    {
                        catalog.Diagnostics.Add(new Diagnostic("MalformedLocalizationText", "A UTF-16 text record extends beyond its resource extent.", resource.Key));
                        return;
                    }
                    var text = Encoding.Unicode.GetString(bytes.Slice(cursor, (int)textLength));
                    catalog.Localization.Add(new LocalizedText(key, language, text, resource.Key, resource.Slice.Offset + keyOffset));
                    cursor += (int)textLength;
                }
                else if (keyLength is > 0 and <= 64 && keyLength <= pointerCapacity)
                {
                    // Pointer records hold several keys whose display text is stored elsewhere.
                    // Keep the keys out of the subtitle index until a source pointer is validated.
                    var count = keyLength;
                    var valid = true;
                    for (var keyIndex = 0; keyIndex < count; keyIndex++)
                    {
                        if (cursor > end - 4) { valid = false; break; }
                        var pointerKeyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                        cursor += 4;
                        if (pointerKeyLength is 0 or > 256 || pointerKeyLength > end - cursor || !IsAsciiKey(bytes.Slice(cursor, (int)pointerKeyLength))) { valid = false; break; }
                        cursor += (int)pointerKeyLength;
                        if (cursor >= end) { valid = false; break; }
                        var pointerKind = bytes[cursor++];
                        var pointerLength = pointerKind switch { 0 => 0, 1 => 9, _ => -1 };
                        if (pointerLength < 0 || pointerLength > end - cursor) { valid = false; break; }
                        cursor += pointerLength;
                    }
                    if (!valid)
                    {
                        catalog.Diagnostics.Add(new Diagnostic("MalformedLocalizationPointer", "A multi-key localization pointer record is malformed and was not associated with text.", resource.Key));
                        return;
                    }
                    unresolvedPointers += (int)count;
                }
                else
                {
                    catalog.Diagnostics.Add(new Diagnostic("UnsupportedLocalizationRecord", "An unknown localization record form was not interpreted.", resource.Key));
                    return;
                }
            }
        }
        if (unresolvedPointers > 0)
            catalog.Diagnostics.Add(new Diagnostic("LocalizationPointersUnresolved", $"{unresolvedPointers} multi-key pointer reference(s) were kept out of the subtitle index because target text could not be validated.", resource.Key));
    }

    private static bool IsAsciiKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return false;
        foreach (var value in bytes)
            if (value < 0x20 || value > 0x7E) return false;
        return true;
    }

    private static void LinkBankObjects(BankInfo bank)
    {
        foreach (var parent in bank.Objects.Values)
        {
            var missingChildren = parent.Children.Where(childId => !bank.Objects.ContainsKey(childId)).ToArray();
            if (missingChildren.Length > 0)
            {
                parent.Support = PlanSupport.Approximate;
                parent.Limitations.Add("Some explicitly serialized child IDs are outside this bank; cross-bank resolution is deferred until graph linking.");
            }
            foreach (var childId in parent.Children)
            {
                if (bank.Objects.TryGetValue(childId, out var child) && child.ParentId is null) child.ParentId = parent.Id;
            }
        }
    }

    private static void ResolveAllBankMedia(AudioCatalog catalog, CancellationToken token, ExternalWemInventory? inventory = null)
    {
        var mediaByBankAndId = catalog.Media.Where(item => !item.Key.Contains(":source:", StringComparison.Ordinal))
            .GroupBy(item => (BankKey: item.BankKey, Id: item.Id)).ToDictionary(group => group.Key, group => group.ToArray());
        var mediaByBank = mediaByBankAndId.GroupBy(pair => pair.Key.BankKey)
            .ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value).ToArray());
        var declarationsByBankAndId = catalog.Banks.SelectMany(bank => bank.Objects.Values.SelectMany(obj => obj.Sources
                .Select(source => (BankKey: bank.Key, Source: new MediaSourceEvidence(bank.Key, obj.Id, source, obj.BodySlice?.Offset ?? 0)))))
            .GroupBy(item => (BankKey: item.BankKey, Id: item.Source.Declaration.Id))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Source).ToArray());
        var declarationsByBank = declarationsByBankAndId.GroupBy(pair => pair.Key.BankKey)
            .ToDictionary(group => group.Key, group => group.Select(pair => (Id: pair.Key.Id, Evidence: pair.Value)).ToArray());
        // All embedded occurrences must be classified before any is offered as a full stream.
        foreach (var bank in catalog.Banks)
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in mediaByBank.GetValueOrDefault(bank.Key, []))
            {
                var evidence = declarationsByBankAndId.GetValueOrDefault((bank.Key, entry.Id), []);
                entry.SourceEvidence.AddRange(evidence);
                entry.Completeness = MediaCompleteness.Unverified;
                if (entry.Slice.Length == 0) { entry.Availability = SourceAvailability.Missing; continue; }
                entry.Availability = SourceAvailability.Available;
                var declarations = evidence.Select(item => item.Declaration).Distinct().ToArray();
                if (declarations.Length == 0)
                {
                    entry.ResolutionDetails = "No matching HIRC source declaration.";
                    continue;
                }
                var streamModes = declarations.Select(value => value.StreamType).Distinct().ToArray();
                var prefetch = declarations.Any(value => value.StreamType is 1 or 2 || (value.Flags & 2) != 0);
                if (prefetch)
                {
                    entry.Completeness = MediaCompleteness.Fragment;
                    entry.State = MediaState.PrefetchOnly;
                    entry.ResolutionDetails = "Embedded streaming prefix; a full stream is required.";
                    // Keep DIDX extent, including any bytes after a locally valid RIFF prefix.
                    var row = FindDidxExtent(bank, entry);
                    if (row is not null) entry.Slice = row;
                }
                else if (streamModes.Length == 1 && streamModes[0] == 0 &&
                         declarations.All(value => (value.PluginId & 15) is not (2 or 5) && (value.Flags & 0x74) == 0 &&
                             (value.InMemorySize == 0 || value.InMemorySize == entry.Slice.Length)) && entry.ContainerValidity == ContainerValidity.Valid)
                {
                    entry.Completeness = MediaCompleteness.Complete;
                    entry.State = MediaState.CompleteEmbedded;
                    entry.ResolutionDetails = "Bounded embedded payload matches Data source declarations.";
                }
                else entry.ResolutionDetails = "Source declaration, declared size, flags or container bounds require verification.";
                if (streamModes.Length > 1)
                {
                    entry.ResolutionDetails += " Conflicting stream declarations were retained.";
                    bank.Diagnostics.Add(new("ConflictingSourceDeclarations", $"Media {entry.Id} has conflicting source storage declarations.", entry.Key));
                }
            }
        }
        if (inventory is not null) catalog.Diagnostics.AddRange(inventory.Diagnostics);
        DiscoverExternalWems(catalog, token, inventory);
        // A fixed set prevents newly synthesized resolutions from becoming candidates.
        var independent = catalog.Media.Where(value => value.Completeness == MediaCompleteness.Complete &&
            value.ContainerValidity == ContainerValidity.Valid && value.Availability == SourceAvailability.Available).ToArray();
        var independentById = independent.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var comparisonCache = new ResolutionComparisonCache(inventory);
        foreach (var bank in catalog.Banks)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (mediaId, group) in declarationsByBank.GetValueOrDefault(bank.Key, []))
            {
                var declarations = group.Select(value => value.Declaration).Distinct().ToArray();
                var embedded = mediaByBankAndId.GetValueOrDefault((bank.Key, mediaId), []);
                if (embedded.Any(item => item.Completeness == MediaCompleteness.Complete)) continue;
                // Unmapped embedded bytes stay visible, but never satisfy a streaming declaration.
                if (declarations.All(value => value.StreamType == 0 && (value.Flags & 2) == 0) && embedded.Length > 0) continue;
                var source = declarations[0];
                var nonWem = declarations.Any(value => (value.PluginId & 15) is 2 or 5);
                var languageSpecific = declarations.Any(value => (value.Flags & 1) != 0);
                var candidates = nonWem ? [] : independentById.GetValueOrDefault(source.Id, []).Where(item => item.BankKey != bank.Key &&
                    (languageSpecific ? item.RawLanguageId == bank.LanguageId : item.RawLanguageId is null or 0 || item.RawLanguageId == bank.LanguageId))
                    .DistinctBy(item => item.Slice).ToList();
                if (source.StreamType == 0 && source.InMemorySize > 0)
                    candidates = candidates.Where(item => item.Slice.Length == source.InMemorySize).ToList();
                var requestedCodec = GetDeclaredWemCodec(declarations);
                var codecRejected = new List<MediaCandidateRejection>();
                if (requestedCodec is null)
                {
                    codecRejected.AddRange(candidates.Select(item => new MediaCandidateRejection(item.Slice,
                        "The source declarations do not identify one supported WEM codec.")));
                    candidates.Clear();
                }
                else
                {
                    codecRejected.AddRange(candidates.Where(item => !CodecMatches(item.Codec, requestedCodec))
                        .Select(item => new MediaCandidateRejection(item.Slice,
                            $"Declared codec {requestedCodec} does not match candidate codec {item.Codec}.")));
                    candidates = candidates.Where(item => CodecMatches(item.Codec, requestedCodec)).ToList();
                }
                var resolvedFromRaw = false;
                if (candidates.Count == 0 && !nonWem && CanInspectInMemoryWemCandidates(declarations))
                {
                    var request = declarations[0];
                    var rawCandidates = catalog.Media.Where(item => item.Id == mediaId && item.BankKey != bank.Key &&
                            item.State == MediaState.CompleteEmbedded && item.IsRiff && item.ContainerValidity == ContainerValidity.Valid &&
                            item.Availability == SourceAvailability.Available && item.Completeness == MediaCompleteness.Unverified &&
                            item.SourceEvidence.Count == 0 && item.Slice.Length == request.InMemorySize &&
                            (languageSpecific ? item.RawLanguageId == bank.LanguageId : item.RawLanguageId is null or 0 || item.RawLanguageId == bank.LanguageId))
                        .DistinctBy(item => item.Slice).ToList();
                    codecRejected.AddRange(rawCandidates.Where(item => !CodecMatchesDeclarations(item.Codec, declarations))
                        .Select(item => new MediaCandidateRejection(item.Slice,
                            $"Candidate codec {item.Codec} does not match every source declaration ({DescribeDeclaredCodecs(declarations)}).")));
                    candidates = rawCandidates.Where(item => CodecMatchesDeclarations(item.Codec, declarations)).ToList();
                    resolvedFromRaw = candidates.Count > 0 && IsSupportedInMemoryWemRequest(declarations);
                    if (!resolvedFromRaw) candidates.Clear();
                }
                var equivalence = GroupEquivalentCandidates(candidates, token, comparisonCache, resolvedFromRaw);
                var selected = equivalence.Count == 1 ? equivalence[0].Representative : null;
                var state = nonWem ? MediaState.Unsupported : equivalence.Count == 1 ? MediaState.CompleteExternal :
                    equivalence.Count > 1 ? MediaState.Ambiguous : MediaState.MissingExternal;
                var entry = new MediaEntry
                {
                    Key = $"{bank.Key}:source:{source.Id:X8}:{source.StreamType}", Id = source.Id, BankKey = bank.Key, BankName = bank.Name,
                    Name = selected?.Name ?? "", Language = selected?.Language ?? "SFX", RawLanguageId = bank.LanguageId,
                    Category = selected?.Category ?? "Unknown", Codec = selected?.Codec ?? "Unknown", SampleRate = selected?.SampleRate ?? 0,
                    Channels = selected?.Channels ?? 0, Duration = selected?.Duration, IsRiff = selected?.IsRiff ?? false,
                    Slice = selected?.Slice ?? new DataSlice(bank.Slice.FilePath, bank.Slice.Offset, 0), State = state,
                    Completeness = selected is null || resolvedFromRaw ? MediaCompleteness.Unverified : MediaCompleteness.Complete,
                    ContainerValidity = selected?.ContainerValidity ?? ContainerValidity.Unknown,
                    Availability = selected is not null ? SourceAvailability.Available : candidates.Count > 1 ? SourceAvailability.Ambiguous : SourceAvailability.Missing,
                    NameKind = selected is null ? NameKind.Candidate : NameKind.Description,
                    ResolutionDetails = selected is not null ? resolvedFromRaw
                        ? $"A compatible HIRC memory-source declaration matches this valid raw WEM by media ID and exact size; {equivalence[0].Locations.Count} byte-identical source location(s) were retained. The payload remains independently unverified."
                        : equivalence[0].Locations.Count > 1
                            ? $"Full stream resolved from {equivalence[0].Locations.Count} byte-identical sources; a deterministic representative was selected."
                            : $"Full stream from {selected.Key}."
                        : equivalence.Count > 1
                            ? $"{equivalence.Count} distinct compatible payloads match; no candidate selected."
                            : codecRejected.Count > 0
                                ? $"Full external stream candidates were rejected: {string.Join(" ", codecRejected.Select(rejection => rejection.Reason).Distinct(StringComparer.Ordinal))}"
                                : "Full external stream was not found."
                };
                entry.SourceEvidence.AddRange(group);
                entry.Candidates.AddRange(candidates.Select(candidate => candidate.Slice));
                entry.CandidateRejections.AddRange(codecRejected);
                if (resolvedFromRaw || equivalence.Count > 1 || equivalence.Any(group => group.Locations.Count > 1))
                    foreach (var equivalenceGroup in equivalence)
                        entry.EquivalenceEvidence.Add(new(equivalenceGroup.Sha256, equivalenceGroup.Locations,
                            resolvedFromRaw ? "Compatible media ID/language, supported non-streamed WEM declaration, exact declared size, SHA-256, and exact bytes; raw source records remain unverified"
                                : "SHA-256, length, and exact bytes in compatible media ID/language context"));
                catalog.Media.Add(entry);
                if (selected is not null)
                {
                    entry.Names.Add(new(selected.DisplayName, "ExternalMediaCandidate", source.Id, NameKind.Description, selected.Key, selected.Slice.Offset));
                    catalog.Names.AddRange(entry.Names);
                }
                else bank.Diagnostics.Add(new(nonWem ? "NonWemSourcePlugin" : equivalence.Count > 1 ? "AmbiguousExternalMedia" : "MissingExternalMedia", entry.ResolutionDetails, entry.Key));
            }
        }
    }

    private static DataSlice? FindDidxExtent(BankInfo bank, MediaEntry entry)
    {
        var didx = bank.Chunks.FirstOrDefault(chunk => chunk.Tag == "DIDX");
        var data = bank.Chunks.FirstOrDefault(chunk => chunk.Tag == "DATA");
        if (didx is null || data is null || didx.Slice.Length > MaximumWrapperBytes) return null;
        using var reader = new PackageReader();
        var bytes = reader.ReadAt(didx.Slice.FilePath, didx.Slice.Offset, (int)didx.Slice.Length);
        for (var position = 0; position <= bytes.Length - 12; position += 12)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4));
            if (id == entry.Id && data.Slice.Offset + offset == entry.Slice.Offset && offset <= data.Slice.Length && length <= data.Slice.Length - offset)
                return new(data.Slice.FilePath, data.Slice.Offset + offset, length);
        }
        return null;
    }

    private static bool IsSupportedInMemoryWemRequest(IReadOnlyCollection<MediaReference> declarations) =>
        declarations.Count > 0 && declarations.All(value => value.StreamType == 0 && value.PluginId == WwiseVorbisPluginId &&
            value.InMemorySize > 0 && (value.Flags & ~LanguageSpecificSourceFlag) == 0) && declarations.Distinct().Count() == 1;

    private static bool CanInspectInMemoryWemCandidates(IReadOnlyCollection<MediaReference> declarations) =>
        declarations.Count > 0 && declarations.All(value => value.StreamType == 0 && value.InMemorySize > 0 &&
            (value.Flags & ~LanguageSpecificSourceFlag) == 0) && declarations.Select(value => value.InMemorySize).Distinct().Count() == 1;

    private static bool CodecMatchesDeclarations(string? candidateCodec, IReadOnlyCollection<MediaReference> declarations) =>
        candidateCodec is not null && declarations.Count > 0 && declarations.All(value =>
            GetDeclaredWemCodec([value]) is { } declaredCodec && CodecMatches(candidateCodec, declaredCodec));

    private static string DescribeDeclaredCodecs(IReadOnlyCollection<MediaReference> declarations) =>
        string.Join(", ", declarations.Select(value => GetDeclaredWemCodec([value]) ?? $"unsupported plugin 0x{value.PluginId:X8}").Distinct(StringComparer.Ordinal));

    private static string? GetDeclaredWemCodec(IReadOnlyCollection<MediaReference> declarations)
    {
        if (declarations.Count == 0) return null;
        var codecs = declarations.Select(value => value.PluginId switch
        {
            0x00010001 => "PCM",
            WwiseVorbisPluginId => "Wwise Vorbis",
            _ => null
        }).Distinct(StringComparer.Ordinal).ToArray();
        return codecs.Length == 1 ? codecs[0] : null;
    }

    private static bool CodecMatches(string? candidateCodec, string? declaredCodec) =>
        declaredCodec is not null && string.Equals(candidateCodec, declaredCodec, StringComparison.OrdinalIgnoreCase);

    private static void DiscoverExternalWems(AudioCatalog catalog, CancellationToken token, ExternalWemInventory? inventory)
    {
        var files = inventory?.Files.Select(file => file.FullPath) ?? EnumerateExternalWemFallback(catalog.SourcePath, catalog.Diagnostics, token);
        var sourceIds = catalog.Banks.SelectMany(bank => bank.Objects.Values).SelectMany(obj => obj.Sources).Select(source => source.Id).ToHashSet();
        var count = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (++count > 100_000) { catalog.Diagnostics.Add(new("ExternalWemScanLimit", "External media discovery reached its file limit.")); break; }
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !sourceIds.Contains(id)) continue;
            try
            {
                var path = Path.GetFullPath(file);
                var length = new FileInfo(path).Length;
                var probe = ProbeWem(path, 0, length);
                if (probe.State != MediaState.CompleteEmbedded || probe.Slice.Length != length) continue;
                var key = $"wem:external:{path}";
                if (catalog.Media.Any(value => value.Key == key)) continue;
                var entry = MakeMediaEntry(key, new ResourceRecord { Id = id, Slice = new(path, 0, length) }, Path.GetFileNameWithoutExtension(path), "—",
                    probe.Slice with { FilePath = path }, probe.State, probe.IsRiff, probe.Codec, probe.SampleRate, probe.Channels, probe.Duration, probe.Validity);
                entry.State = MediaState.CompleteExternal;
                entry.Category = "Unknown";
                entry.ResolutionDetails = "Complete standalone WEM with a numeric Wwise media filename.";
                catalog.Media.Add(entry);
            }
            catch (IOException ex) { catalog.Diagnostics.Add(new("ExternalWemReadError", ex.Message, file)); }
        }
    }

    private static List<EquivalentCandidateGroup> GroupEquivalentCandidates(List<MediaEntry> candidates, CancellationToken token,
        ResolutionComparisonCache comparisonCache, bool requireHash)
    {
        var groups = new List<EquivalentCandidateGroup>();
        foreach (var candidate in candidates.OrderBy(item => item.Key, StringComparer.Ordinal)
                     .ThenBy(item => item.Slice.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Slice.Offset))
        {
            token.ThrowIfCancellationRequested();
            var slice = candidate.Slice;
            var hash = requireHash || candidates.Count > 1 ? comparisonCache.Hash(slice, token) : "";
            var match = hash.Length == 0 ? null : groups.FirstOrDefault(group => group.Representative.Slice.Length == candidate.Slice.Length &&
                group.Sha256.Equals(hash, StringComparison.Ordinal) && comparisonCache.Equal(group.Representative.Slice, candidate.Slice, token));
            if (match is null) groups.Add(new(candidate, hash, [candidate.Slice]));
            else match.Locations.Add(candidate.Slice);
        }
        return groups;
    }

    private static string HashSlice(DataSlice slice, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(slice.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = slice.Offset; var remaining = slice.Length; var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("A media candidate changed during resolution.");
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool EqualSlices(DataSlice left, DataSlice right, CancellationToken token)
    {
        if (left.Length != right.Length) return false;
        using var first = new FileStream(left.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = new FileStream(right.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        first.Position = left.Offset; second.Position = right.Offset;
        var a = new byte[64 * 1024]; var b = new byte[a.Length]; var remaining = left.Length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested(); var wanted = (int)Math.Min(a.Length, remaining);
            var readA = first.Read(a, 0, wanted); var readB = second.Read(b, 0, wanted);
            if (readA != wanted || readB != wanted) throw new EndOfStreamException("A media candidate changed during comparison.");
            if (!a.AsSpan(0, wanted).SequenceEqual(b.AsSpan(0, wanted))) return false;
            remaining -= wanted;
        }
        return true;
    }

    private sealed class EquivalentCandidateGroup(MediaEntry representative, string sha256, List<DataSlice> locations)
    {
        public MediaEntry Representative { get; } = representative;
        public string Sha256 { get; } = sha256;
        public List<DataSlice> Locations { get; } = locations;
    }

    private sealed class ResolutionComparisonCache
    {
        private readonly Dictionary<string, ExternalWemFile> _inventoryFiles;
        private readonly Dictionary<DataSlice, string> _hashes = [];
        private readonly Dictionary<(DataSlice Left, DataSlice Right), bool> _equal = [];
        public ResolutionComparisonCache(ExternalWemInventory? inventory) =>
            _inventoryFiles = inventory?.Files.ToDictionary(file => Path.GetFullPath(file.FullPath), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ExternalWemFile>(StringComparer.OrdinalIgnoreCase);

        public string Hash(DataSlice slice, CancellationToken token)
        {
            if (_hashes.TryGetValue(slice, out var cached)) return cached;
            token.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(slice.FilePath);
            var value = slice.Offset == 0 && _inventoryFiles.TryGetValue(fullPath, out var indexed) &&
                indexed.Length == slice.Length && indexed.Sha256.Length == 64
                    ? indexed.Sha256 : HashSlice(slice, token);
            _hashes.Add(slice, value);
            return value;
        }

        public bool Equal(DataSlice left, DataSlice right, CancellationToken token)
        {
            if (left == right) return true;
            if (CompareSlices(left, right) > 0) (left, right) = (right, left);
            var key = (left, right);
            if (_equal.TryGetValue(key, out var cached)) return cached;
            token.ThrowIfCancellationRequested();
            var value = EqualSlices(left, right, token);
            _equal.Add(key, value);
            return value;
        }

        private static int CompareSlices(DataSlice left, DataSlice right)
        {
            var path = StringComparer.OrdinalIgnoreCase.Compare(left.FilePath, right.FilePath);
            if (path != 0) return path;
            var offset = left.Offset.CompareTo(right.Offset);
            return offset != 0 ? offset : left.Length.CompareTo(right.Length);
        }
    }

    private static ExternalWemInventory ScanExternalInventory(string sourcePath, CancellationToken token)
    {
        var root = Directory.Exists(sourcePath) ? Path.GetFullPath(sourcePath) : Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var result = new ExternalWemInventory { RootPath = root }; var dirs = new Stack<string>(); dirs.Push(root);
        if (!Directory.Exists(sourcePath) && Path.GetExtension(sourcePath).ToLowerInvariant() is ".mid" or ".midi") { token.ThrowIfCancellationRequested(); return result; }
        while (dirs.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dirs.Pop()).EnumerateFileSystemInfos().ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Diagnostics.Add(new("ExternalWemScanAccess", "A directory could not be scanned.")); continue; }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) { dirs.Push(entry.FullName); continue; }
                    if (!Path.GetExtension(entry.Name).Equals(".wem", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(entry.FullName); info.Refresh();
                    result.Files.Add(new(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'), entry.FullName, info.Length,
                        info.LastWriteTimeUtc.Ticks, "", 0, ""));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Diagnostics.Add(new("ExternalWemEntryAccess", "A filesystem entry could not be inspected.")); }
            }
        }
        return result;
    }

    private static IEnumerable<string> EnumerateExternalWemFallback(string sourcePath, List<Diagnostic> diagnostics, CancellationToken token)
    {
        var inventory = ScanExternalInventory(sourcePath, token);
        return AddDiagnostics(inventory, diagnostics);
    }

    private static IEnumerable<string> AddDiagnostics(ExternalWemInventory inventory, List<Diagnostic> diagnostics)
    {
        diagnostics.AddRange(inventory.Diagnostics);
        return inventory.Files.Select(file => file.FullPath);
    }

    private static void EnrichEventsFromBank(AudioCatalog catalog, BankInfo bank, Dictionary<uint, string> namedEvents)
    {
        var existingKeys = catalog.Events.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var obj in bank.Objects.Values.Where(value => value.Type == 4))
        {
            var name = namedEvents.GetValueOrDefault(obj.Id, obj.Id.ToString(CultureInfo.InvariantCulture));
            var key = $"event:sound:{obj.Id:X8}:{bank.Key}";
            if (!existingKeys.Add(key)) continue;
            var audioEvent = new AudioEvent { Key = key, Id = obj.Id, Name = name, BankKey = bank.Key };
            if (namedEvents.ContainsKey(obj.Id))
                audioEvent.Names.Add(new NameEvidence(name, "WwiseEvent", obj.Id, NameKind.Stored, "CSNDEVNT metadata"));
            catalog.Events.Add(audioEvent);
        }

    }

    private static void Unsupported(BankObject item, string reason)
    {
        item.Support = PlanSupport.Unsupported;
        item.Limitations.Add(reason);
    }

    private static void MarkNameCollisions(AudioCatalog catalog)
    {
        var groups = catalog.Names.Select((evidence, index) => (evidence, index))
            .GroupBy(item => (item.evidence.Namespace, item.evidence.Id));
        foreach (var group in groups)
        {
            if (group.Select(item => item.evidence.Text).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                var collision = $"{group.Key.Namespace}:{group.Key.Id:X8}";
                foreach (var item in group)
                    catalog.Names[item.index] = item.evidence with { CollisionGroup = collision };
            }
        }
    }

    private static void PublishSnapshot(IProgress<IndexProgress>? progress, AudioCatalog catalog, string phase, int done, int total, string? detail)
    {
        if (progress is null) return;
        progress.Report(new IndexProgress(phase, done, total, detail, CreateSnapshot(catalog)));
    }

    private static AudioCatalog CreateSnapshot(AudioCatalog source)
    {
            var snapshot = new AudioCatalog { SourcePath = source.SourcePath, Fingerprint = source.Fingerprint, IndexedResourceCount = source.IndexedResourceCount };
        snapshot.Diagnostics.AddRange(source.Diagnostics);
        snapshot.Names.AddRange(source.Names);
        snapshot.Localization.AddRange(source.Localization);
        foreach (var bank in source.Banks)
        {
            var copy = new BankInfo { Key = bank.Key, Id = bank.Id, Name = bank.Name, Version = bank.Version, HasFeedback = bank.HasFeedback, LanguageId = bank.LanguageId, Slice = bank.Slice };
            copy.Names.AddRange(bank.Names);
            copy.Chunks.AddRange(bank.Chunks);
            copy.Diagnostics.AddRange(bank.Diagnostics);
            foreach (var (id, value) in bank.Objects)
            {
                var obj = new BankObject
                {
                    Id = value.Id, Type = value.Type, BodySlice = value.BodySlice, ParentId = value.ParentId, TargetId = value.TargetId,
                    TargetFlags = value.TargetFlags, TargetBankId = value.TargetBankId, StateGroupId = value.StateGroupId, StateValueId = value.StateValueId, SwitchGroupKind = value.SwitchGroupKind,
                    SwitchGroupId = value.SwitchGroupId, DefaultSwitchId = value.DefaultSwitchId, ActionType = value.ActionType,
                    VolumeDb = value.VolumeDb, PitchCents = value.PitchCents, DelaySeconds = value.DelaySeconds,
                    Sequential = value.Sequential, LoopCount = value.LoopCount, Support = value.Support
                };
                obj.Children.AddRange(value.Children); obj.Actions.AddRange(value.Actions); obj.Sources.AddRange(value.Sources);
                obj.OwnedChildren.AddRange(value.OwnedChildren);
                obj.Clips.AddRange(value.Clips); obj.Markers.AddRange(value.Markers); obj.Branches.AddRange(value.Branches); obj.Limitations.AddRange(value.Limitations);
                obj.DecisionPaths.AddRange(value.DecisionPaths.Select(path => path with { Values = path.Values.ToList() }));
                copy.Objects.Add(id, obj);
            }
            snapshot.Banks.Add(copy);
        }
        foreach (var media in source.Media)
        {
            var copy = new MediaEntry
            {
                Key = media.Key, Id = media.Id, BankKey = media.BankKey, BankName = media.BankName, Name = media.Name,
                DisplayLabel = media.DisplayLabel, RawLanguageId = media.RawLanguageId,
                Language = media.Language, Category = media.Category, Codec = media.Codec, Midi = media.Midi, SampleRate = media.SampleRate,
                Channels = media.Channels, Duration = media.Duration, MeasuredDuration = media.MeasuredDuration, Slice = media.Slice, State = media.State,
                NameKind = media.NameKind, IsRiff = media.IsRiff, Subtitle = media.Subtitle,
                Completeness = media.Completeness, ContainerValidity = media.ContainerValidity, Availability = media.Availability, ResolutionDetails = media.ResolutionDetails
            };
            copy.SourceEvidence.AddRange(media.SourceEvidence); copy.Aliases.AddRange(media.Aliases); copy.EventKeys.AddRange(media.EventKeys); copy.Candidates.AddRange(media.Candidates); copy.EquivalenceEvidence.AddRange(media.EquivalenceEvidence); copy.Names.AddRange(media.Names);
            snapshot.Media.Add(copy);
        }
        foreach (var item in source.Events)
        {
            var copy = new AudioEvent { Key = item.Key, Id = item.Id, Name = item.Name, BankKey = item.BankKey, IsDialogue = item.IsDialogue, Behavior = item.Behavior, LinkStatus = item.LinkStatus };
            copy.StateValues.AddRange(item.StateValues); copy.MediaLinks.AddRange(item.MediaLinks); copy.RelatedPlayEventKeys.AddRange(item.RelatedPlayEventKeys); copy.Diagnostics.AddRange(item.Diagnostics);
            copy.MediaKeys.AddRange(item.MediaKeys); copy.Names.AddRange(item.Names); snapshot.Events.Add(copy);
        }
        return snapshot;
    }

    private static string ParseLanguage(string name)
    {
        var suffix = name.AsSpan().TrimEnd();
        var underscore = suffix.LastIndexOf('_');
        if (underscore >= 0 && suffix.Length - underscore == 4)
        {
            var code = suffix[(underscore + 1)..].ToString().ToUpperInvariant();
            return code == "JAP" ? "JPN" : code;
        }
        return "SFX";
    }

    private static string LocalizationLanguageName(string code) => code.Equals("JAP", StringComparison.OrdinalIgnoreCase) ? "JPN" : code.ToUpperInvariant();

    private static string DialogueStem(string name)
    {
        var index = name.LastIndexOf('_');
        return index > 0 ? name[..index] : name;
    }

    private static uint Fnv1LowerUtf8(string value)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash = unchecked(hash * 16777619);
            hash ^= b;
        }
        return hash;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int position)
    {
        if (position < 0 || position > bytes.Length - 4) throw new InvalidDataException("A length-prefixed field is truncated.");
        var result = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
        position += 4;
        return result;
    }

    private readonly record struct ParsedDialogue(string Name, string Language, DataSlice MediaSlice, long AudioOffset, long AudioLength,
        MediaState State, bool IsRiff, string Codec, int SampleRate, int Channels, double? Duration, ContainerValidity Validity = ContainerValidity.Unknown);
    private readonly record struct ParsedProbe(DataSlice Slice, MediaState State, bool IsRiff, string Codec, int SampleRate, int Channels, double? Duration, ContainerValidity Validity = ContainerValidity.Unknown);

    private sealed class PackageReader : IDisposable
    {
        private readonly Dictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _lengths = new(StringComparer.OrdinalIgnoreCase);
        public PackageReader() { }

        public byte[] ReadResource(ResourceRecord resource, int maximum)
        {
            if (resource.Slice.Length > maximum) throw new InvalidDataException("The resource exceeds the parser's bounded metadata read limit.");
            return ReadAt(resource.Slice.FilePath, resource.Slice.Offset, checked((int)resource.Slice.Length));
        }

        public bool TryReadResource(ResourceRecord resource, int maximum, out byte[] bytes)
        {
            bytes = [];
            if (!File.Exists(resource.Slice.FilePath) || resource.Slice.Length > maximum) return false;
            try { bytes = ReadResource(resource, maximum); return true; }
            catch (IOException) { return false; }
        }

        public byte[] ReadAt(string filePath, long offset, int length)
        {
            var handle = GetHandle(filePath);
            var totalLength = _lengths[filePath];
            if (offset < 0 || length < 0 || offset > totalLength || length > totalLength - offset)
                throw new InvalidDataException("The requested resource range falls outside its package file.");
            var bytes = new byte[length];
            ReadAtExactly(handle, offset, bytes);
            return bytes;
        }

        public bool TryFindEmbeddedBank(DataSlice container, CancellationToken token, out long bankOffset, out long bankLength, out string diagnostic)
        {
            bankOffset = 0;
            bankLength = 0;
            diagnostic = "BankPayloadNotFound";
            if (container.Length <= 0 || !File.Exists(container.FilePath)) return false;
            var handle = GetHandle(container.FilePath);
            var fileLength = _lengths[container.FilePath];
            if (container.Offset < 0 || container.Offset > fileLength || container.Length > fileLength - container.Offset)
            {
                diagnostic = "MalformedBankContainerRange";
                return false;
            }

            if (!TryGetDataContainerPayload(container, out var payloadOffset, out var payloadLength, out var containerError))
            {
                diagnostic = "MalformedDataContainer_" + containerError;
                return false;
            }

            const int windowSize = 1024 * 1024;
            var buffer = new byte[windowSize + BankContainerMagic.Length - 1];
            var carry = 0;
            long consumed = 0;
            var allZeroContent = true;
            var sawMarker = false;
            var payloadEnd = checked(payloadOffset + payloadLength);
            while (consumed < payloadLength)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(windowSize, payloadLength - consumed);
                var read = ReadAt(container.FilePath, checked(container.Offset + payloadOffset + consumed), count);
                read.CopyTo(buffer, carry);
                var contentStart = checked(payloadOffset + 16 - (payloadOffset + consumed));
                if (allZeroContent && contentStart < read.Length && read.AsSpan((int)Math.Max(0, contentStart)).IndexOfAnyExcept((byte)0) >= 0)
                    allZeroContent = false;
                var available = carry + read.Length;
                var relative = 0;
                while (relative <= available - BankContainerMagic.Length)
                {
                    var hit = buffer.AsSpan(relative, available - relative).IndexOf(BankContainerMagic);
                    if (hit < 0) break;
                    var markerInWindow = relative + hit;
                    var markerInContainer = payloadOffset + consumed - carry + markerInWindow;
                    sawMarker = true;
                    if (markerInContainer >= payloadOffset && markerInContainer <= payloadOffset + payloadLength - 16)
                    {
                        var prefix = ReadAt(container.FilePath, checked(container.Offset + markerInContainer), 16);
                        var candidateBankLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12, 4));
                        var bankPayloadOffset = markerInContainer + 16;
                        if (candidateBankLength >= 12 && candidateBankLength <= payloadEnd - bankPayloadOffset &&
                            TryReadBankHeader(container.FilePath, checked(container.Offset + bankPayloadOffset), candidateBankLength, out _, out _))
                        {
                            bankOffset = bankPayloadOffset;
                            bankLength = candidateBankLength;
                            diagnostic = string.Empty;
                            return true;
                        }
                    }
                    relative = markerInWindow + 1;
                }
                carry = Math.Min(BankContainerMagic.Length - 1, available);
                Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
                consumed += read.Length;
            }
            if (sawMarker) diagnostic = "MalformedBankPayload";
            else if (allZeroContent) diagnostic = "ZeroFilledBankContainer";
            return false;
        }

        public bool TryReadBankHeader(string filePath, long offset, long length, out uint version, out uint bankId)
        {
            version = 0;
            bankId = 0;
            if (length < 16) return false;
            if (!File.Exists(filePath)) return false;
            var handle = GetHandle(filePath);
            var fileLength = _lengths[filePath];
            if (offset < 0 || offset > fileLength || length > fileLength - offset) return false;
            var position = 0L;
            var chunks = 0;
            var foundHeader = false;
            while (position <= length - 8)
            {
                if (++chunks > 4096) return false;
                var header = ReadAt(filePath, checked(offset + position), 8);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                if (size > length - position - 8) return false;
                if (header.AsSpan(0, 4).SequenceEqual("BKHD"u8))
                {
                    if (foundHeader || size < 8) return false;
                    var payload = ReadAt(filePath, checked(offset + position + 8), 8);
                    version = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                    bankId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                    foundHeader = true;
                }
                position = checked(position + 8 + size);
            }
            return foundHeader && position == length;
        }

        private bool TryGetDataContainerPayload(DataSlice container, out long payloadOffset, out long payloadLength, out string error)
        {
            payloadOffset = 0;
            payloadLength = 0;
            error = "Short";
            if (container.Length < 37) return false;
            var prefix = ReadAt(container.FilePath, container.Offset, (int)Math.Min(container.Length, 1024));
            error = "Magic";
            if (!prefix.AsSpan(0, 5).SequenceEqual("QZIP\0"u8) || !prefix.AsSpan(5, 8).SequenceEqual("DC_INFO "u8)) return false;
            var infoVersion = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(13, 4));
            var infoSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(17, 4));
            error = "InfoHeader";
            if (infoVersion != 3 || infoSize < 8 || infoSize > container.Length - 21 || infoSize > MaximumWrapperBytes || (infoSize - 8) % 8 != 0) return false;
            var infoBody = ReadAt(container.FilePath, checked(container.Offset + 21), checked((int)infoSize));
            var referenceCount = BinaryPrimitives.ReadUInt32LittleEndian(infoBody.AsSpan(4, 4));
            error = "InfoReferences";
            if (referenceCount != (infoSize - 8) / 8) return false;
            for (var index = 0; index < referenceCount; index++)
            {
                var referenceOffset = 8 + checked((int)index) * 8;
                _ = BinaryPrimitives.ReadUInt32LittleEndian(infoBody.AsSpan(referenceOffset, 4));
                _ = BinaryPrimitives.ReadUInt32LittleEndian(infoBody.AsSpan(referenceOffset + 4, 4));
            }
            var dataPosition = 21L + infoSize;
            error = "DataHeaderBounds";
            if (dataPosition > container.Length - 20) return false;
            var dataHeader = ReadAt(container.FilePath, checked(container.Offset + dataPosition), 20);
            error = "DataTag";
            if (!dataHeader.AsSpan(0, 8).SequenceEqual("DC_DATA "u8)) return false;
            var dataVersion = BinaryPrimitives.ReadUInt32LittleEndian(dataHeader.AsSpan(8, 4));
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(dataHeader.AsSpan(12, 4));
            error = "DataVersion";
            if (dataVersion == 0) return false;
            payloadOffset = dataPosition + 16;
            payloadLength = dataSize;
            error = "DataBounds";
            if (payloadLength > container.Length - payloadOffset) return false;
            error = string.Empty;
            return true;
        }

        public ParsedProbe ProbeWem(string path, long offset, long length)
        {
            var handle = GetHandle(path);
            var totalLength = _lengths[path];
            if (offset < 0 || offset > totalLength || length > totalLength - offset)
                throw new InvalidDataException("The WEM slice does not belong to the referenced package segment.");
            var probe = DetroitCatalogReader.ProbeWem(handle, offset, length);
            return probe with { Slice = probe.Slice with { FilePath = path } };
        }

        private Microsoft.Win32.SafeHandles.SafeFileHandle GetHandle(string path)
        {
            if (_handles.TryGetValue(path, out var existing)) return existing;
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _handles[path] = handle;
            _lengths[path] = RandomAccess.GetLength(handle);
            return handle;
        }

        public void Dispose()
        {
            foreach (var handle in _handles.Values) handle.Dispose();
            _handles.Clear();
        }
    }
}
