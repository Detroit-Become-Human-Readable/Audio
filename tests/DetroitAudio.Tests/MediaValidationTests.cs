using System.Buffers.Binary;
using System.Text;
using DetroitAudio.Core;
using DetroitAudio.Formats;
using DetroitAudio.Indexing;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class MediaValidationTests
{
    [Theory]
    [InlineData((byte)1, 16000u, false)]
    [InlineData((byte)1, 117000u, false)]
    [InlineData((byte)2, 290000u, false)]
    [InlineData((byte)1, 85000u, true)]
    public async Task StreamingDeclarationsKeepValidOrTruncatedPrefixesAsFragments(byte mode, uint samples, bool truncate)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var bytes = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4], samples);
        if (truncate) bytes = bytes[..^2];
        fixture.AddBankFixture(1, "prefix", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("prefix"),
            [(71u, bytes)], [(71u, mode, (uint)bytes.Length, 0x00040001u)]));
        fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        var prefix = Assert.Single(catalog.Media, media => media.Completeness == MediaCompleteness.Fragment);
        Assert.Equal(MediaState.PrefetchOnly, prefix.State);
        Assert.Equal(bytes.Length, prefix.Slice.Length);
        Assert.Equal(SourceAvailability.Available, prefix.Availability);
        Assert.Single(prefix.SourceEvidence);
        Assert.NotNull(Assert.Single(catalog.Banks).Objects.Values.Single().BodySlice);
        Assert.Equal(truncate ? ContainerValidity.Truncated : ContainerValidity.Valid, prefix.ContainerValidity);
        var missing = Assert.Single(catalog.Media, media => media.Availability == SourceAvailability.Missing);
        Assert.Equal(MediaState.MissingExternal, missing.State);
        Assert.Empty(missing.Candidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolverNeverUsesPrefixesAndIsIndependentOfBankOrder(bool reverse)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var wem = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]);
        var banks = new (uint Resource, string Name, byte Mode, bool Bytes)[]
        {
            (1, "stream", 2, false), (2, "prefix", 1, true), (3, "complete", 0, true)
        };
        foreach (var row in reverse ? banks.Reverse() : banks)
            fixture.AddBankFixture(row.Resource, row.Name, FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(row.Name),
                row.Bytes ? [(81u, wem)] : [], [(81u, row.Mode, (uint)wem.Length, 0x00040001u)]));
        fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        var stream = Assert.Single(catalog.Media, media => media.BankName == "stream");
        var complete = Assert.Single(catalog.Media, media => media.BankName == "complete");
        Assert.Equal(MediaCompleteness.Complete, complete.Completeness);
        Assert.Equal(MediaState.CompleteExternal, stream.State);
        Assert.Equal(complete.Slice, Assert.Single(stream.Candidates));
        var prefix = Assert.Single(catalog.Media, media => media.BankName == "prefix" && media.Completeness == MediaCompleteness.Fragment);
        Assert.NotEqual(prefix.Slice, stream.Slice);
    }

    [Fact]
    public async Task ConflictingSourcesRemainFragmentsAndCannotResolveOtherBanks()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var wem = FormatsFixtureBuilder.MakeWem([0, 1, 2, 3]);
        fixture.AddBankFixture(1, "conflicting", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("conflicting"),
            [(91u, wem)], [(91u, (byte)0, (uint)wem.Length, 0x00040001u), (91u, (byte)1, (uint)wem.Length, 0x00040001u)]));
        fixture.AddBankFixture(2, "stream", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("stream"), [], [(91u, (byte)2, 0u, 0x00040001u)]));
        fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        Assert.Contains(catalog.Banks.SelectMany(bank => bank.Diagnostics), value => value.Code == "ConflictingSourceDeclarations");
        Assert.All(catalog.Media.Where(media => media.Slice.Length > 0), media => Assert.Equal(MediaCompleteness.Fragment, media.Completeness));
        Assert.Equal(MediaState.MissingExternal, Assert.Single(catalog.Media, media => media.BankName == "stream").State);
    }

    [Fact]
    public async Task ValidRiffWithoutSourceMetadataRemainsUnverified()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddBankFixture(1, "unmapped", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("unmapped"),
            [(101u, FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]))], []));
        fixture.WriteIndex();
        var media = Assert.Single((await new DetroitCatalogReader().ReadAsync(fixture.IndexPath)).Media);
        Assert.Equal(MediaCompleteness.Unverified, media.Completeness);
        Assert.Equal(ContainerValidity.Valid, media.ContainerValidity);
        Assert.False(MediaPolicy.IsComplete(media));
        Assert.True(MediaPolicy.CanConvert(media));
    }

    [Fact]
    public async Task NumericExternalWemResolvesFullStreamWithoutChangingPrefix()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var wem = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]);
        fixture.AddBankFixture(1, "source", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("source"),
            [(111u, wem[..^2])], [(111u, (byte)1, (uint)(wem.Length - 2), 0x00040001u)]));
        fixture.WriteIndex();
        var nestedMedia = Path.Combine(fixture.DirectoryPath, "streams", "language", "111.wem");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedMedia)!);
        await File.WriteAllBytesAsync(nestedMedia, wem);
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        var prefix = Assert.Single(catalog.Media, media => media.Completeness == MediaCompleteness.Fragment);
        var resolved = Assert.Single(catalog.Media, media => media.BankName == "source" && media.State == MediaState.CompleteExternal);
        Assert.Equal(wem.Length - 2, prefix.Slice.Length);
        Assert.Equal(wem.Length, resolved.Slice.Length);
        Assert.True(MediaPolicy.CanConvert(resolved));
        Assert.False(MediaPolicy.CanConvert(prefix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ByteIdenticalCompleteCandidatesResolveToStableRepresentative(bool reverseBankOrder)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const uint id = 0x10203040;
        var wem = FormatsFixtureBuilder.MakeWem([1, 3, 5, 7]);
        var banks = new (uint Resource, string Name, byte Mode, byte[]? Payload)[]
        {
            (11, "candidate_first", 0, wem),
            (12, "candidate_second", 0, wem.ToArray()),
            (13, "source_stream", 2, null)
        };
        foreach (var bank in reverseBankOrder ? banks.Reverse() : banks)
            fixture.AddBankFixture(bank.Resource, bank.Name,
                FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(bank.Name),
                    bank.Payload is null ? [] : [(id, bank.Payload)], [(id, bank.Mode, bank.Payload is null ? 0u : (uint)bank.Payload.Length, 0x00040001u)]));
        fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        var resolved = Assert.Single(catalog.Media, media => media.BankName == "source_stream");
        Assert.Equal(MediaState.CompleteExternal, resolved.State);
        Assert.Equal(2, resolved.Candidates.Count);
        var equivalent = Assert.Single(resolved.EquivalenceEvidence);
        Assert.Equal(2, equivalent.SourceLocations.Count);
        var completeCandidates = catalog.Media.Where(media => media.Id == id && media.BankKey != resolved.BankKey &&
            media.Completeness == MediaCompleteness.Complete && media.State == MediaState.CompleteEmbedded).OrderBy(media => media.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, completeCandidates.Length);
        Assert.Equal(completeCandidates[0].Slice, resolved.Slice);
    }

    [Fact]
    public async Task StandaloneBankUsesSiblingExternalWemInventoryRecursively()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const uint id = 0x55667788;
        var bankPath = Path.Combine(fixture.DirectoryPath, "standalone.bnk");
        var bankBytes = FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("standalone"), [], [(id, (byte)2, 0u, 0x00040001u)]);
        await File.WriteAllBytesAsync(bankPath, bankBytes);
        var wemPath = Path.Combine(fixture.DirectoryPath, "external", "deep", $"{id}.wem");
        Directory.CreateDirectory(Path.GetDirectoryName(wemPath)!);
        await File.WriteAllBytesAsync(wemPath, FormatsFixtureBuilder.MakeWem([2, 4, 6, 8]));

        var catalog = await new DetroitCatalogReader().ReadAsync(bankPath);

        var resolved = Assert.Single(catalog.Media, media => media.BankName == "standalone" && media.State == MediaState.CompleteExternal);
        Assert.Equal(Path.GetFullPath(wemPath), Path.GetFullPath(resolved.Slice.FilePath));
        Assert.True(MediaPolicy.CanConvert(resolved));
    }

    [Theory]
    [InlineData(false, 118u)]
    [InlineData(true, 118u)]
    [InlineData(false, 120u)]
    [InlineData(true, 120u)]
    public async Task ExactRequestDeclarationResolvesOnlyUnverifiedRawBankPayloads(bool reverseBankOrder, uint requestVersion)
    {
        const uint id = 0x21324354;
        var firstBytes = FormatsFixtureBuilder.MakeWem([1, 3, 5, 7]);
        var secondBytes = firstBytes.ToArray();
        var (fixture, catalog) = await ReadRawCandidateCatalogAsync(id, [firstBytes, secondBytes],
            [(id, (byte)0, (uint)firstBytes.Length, 0x00040001u)], reverseCandidateOrder: reverseBankOrder, requestVersion: requestVersion);
        using (fixture)
        {
            var resolved = Assert.Single(catalog.Media, item => item.BankName == "request_bank");
            Assert.Equal(MediaState.CompleteExternal, resolved.State);
            Assert.Equal(MediaCompleteness.Unverified, resolved.Completeness);
            Assert.Equal(SourceAvailability.Available, resolved.Availability);
            Assert.True(MediaPolicy.CanPreview(resolved));
            Assert.True(MediaPolicy.CanConvert(resolved));
            Assert.Equal(2, resolved.Candidates.Count);
            var equivalence = Assert.Single(resolved.EquivalenceEvidence);
            Assert.Equal(2, equivalence.SourceLocations.Count);
            Assert.Contains("remains independently unverified", resolved.ResolutionDetails);
            var raw = catalog.Media.Where(item => item.BankName.StartsWith("raw_candidate_", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, raw.Length);
            Assert.All(raw, item =>
            {
                Assert.Equal(MediaState.CompleteEmbedded, item.State);
                Assert.Equal(MediaCompleteness.Unverified, item.Completeness);
                Assert.Empty(item.SourceEvidence);
            });
            Assert.Equal(raw.OrderBy(item => item.Key, StringComparer.Ordinal).First().Slice, resolved.Slice);
        }
    }

    [Fact]
    public async Task VorbisDeclarationRejectsPcmRiffForRawAndCompleteCandidatesWithEvidence()
    {
        const uint id = 0x21324355;
        var pcm = FormatsFixtureBuilder.MakePcmWem([1, 3, 5, 7]);
        var (rawFixture, rawCatalog) = await ReadRawCandidateCatalogAsync(id, [pcm],
            [(id, (byte)0, (uint)pcm.Length, 0x00040001u)]);
        using (rawFixture)
        {
            var rejected = Assert.Single(rawCatalog.Media, media => media.BankName == "request_bank");
            Assert.Equal(MediaState.MissingExternal, rejected.State);
            Assert.Empty(rejected.Candidates);
            Assert.Equal("PCM", Assert.Single(rawCatalog.Media, media => media.BankName.StartsWith("raw_candidate_", StringComparison.Ordinal)).Codec);
            Assert.Equal(pcm.Length, Assert.Single(rejected.CandidateRejections).Slice.Length);
            Assert.Contains("PCM", rejected.ResolutionDetails);
        }

        using var completeFixture = FormatsFixtureBuilder.CreateEmpty();
        completeFixture.AddBankFixture(1, "pcm_candidate", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("pcm_candidate"),
            [(id, pcm)], [(id, (byte)0, (uint)pcm.Length, 1u)]));
        completeFixture.AddBankFixture(2, "vorbis_request", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("vorbis_request"), [],
            [(id, (byte)2, 0u, 0x00040001u)]));
        completeFixture.WriteIndex();
        var completeCatalog = await new DetroitCatalogReader().ReadAsync(completeFixture.IndexPath);
        var fullRejected = Assert.Single(completeCatalog.Media, media => media.BankName == "vorbis_request");
        Assert.Equal(MediaState.MissingExternal, fullRejected.State);
        Assert.Empty(fullRejected.Candidates);
        Assert.Single(fullRejected.CandidateRejections);
        Assert.Contains("Declared codec Wwise Vorbis does not match candidate codec PCM", fullRejected.ResolutionDetails);
    }

    [Fact]
    public async Task PcmDeclarationResolvesPcmCompleteCandidate()
    {
        const uint id = 0x21324356;
        var pcm = FormatsFixtureBuilder.MakePcmWem([4, 3, 2, 1]);
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddBankFixture(1, "pcm_candidate", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("pcm_candidate"),
            [(id, pcm)], [(id, (byte)0, (uint)pcm.Length, 0x00010001u)]));
        fixture.AddBankFixture(2, "pcm_request", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("pcm_request"), [],
            [(id, (byte)2, 0u, 0x00010001u)]));
        fixture.WriteIndex();

        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);

        var resolved = Assert.Single(catalog.Media, media => media.BankName == "pcm_request");
        Assert.Equal(MediaState.CompleteExternal, resolved.State);
        Assert.Equal("PCM", resolved.Codec);
        Assert.Equal(Assert.Single(catalog.Media, media => media.BankName == "pcm_candidate").Slice, resolved.Slice);
        Assert.Empty(resolved.CandidateRejections);
    }

    [Fact]
    public async Task StandaloneWemInventoryContainsOnlySelectedFileAndTracksItsDuration()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var selectedPath = Path.Combine(fixture.DirectoryPath, "selected.wem");
        var selectedBytes = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4], 48000);
        await File.WriteAllBytesAsync(selectedPath, selectedBytes);
        var catalog = await new DetroitCatalogReader().ReadAsync(selectedPath);
        var selected = Assert.Single(catalog.Media);
        Assert.Equal("selected", selected.Name);
        Assert.Equal(1d, selected.Duration);

        var sidecar = Path.Combine(fixture.DirectoryPath, "cache", "external-wem-hashes.json");
        var initialInventory = ExternalWemInventoryBuilder.Build(selectedPath, sidecar);
        Assert.Equal(new[] { "selected.wem" }, initialInventory.Files.Select(file => file.RelativePath));
        var initialFingerprint = IndexService.Fingerprint(selectedPath, initialInventory);

        var siblingPath = Path.Combine(fixture.DirectoryPath, "unrelated.wem");
        await File.WriteAllBytesAsync(siblingPath, FormatsFixtureBuilder.MakeWem([8, 7, 6, 5]));
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.DirectoryPath, "nested", "also-unrelated.wem"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(fixture.DirectoryPath, "unrelated.exe"), [4, 5, 6]);
        var withUnrelated = ExternalWemInventoryBuilder.Build(selectedPath, sidecar);
        Assert.Equal(new[] { "selected.wem" }, withUnrelated.Files.Select(file => file.RelativePath));
        Assert.Equal(initialFingerprint, IndexService.Fingerprint(selectedPath, withUnrelated));
        await File.WriteAllBytesAsync(siblingPath, FormatsFixtureBuilder.MakeWem([9, 8, 7, 6], 72000));
        File.Delete(Path.Combine(fixture.DirectoryPath, "nested", "also-unrelated.wem"));
        var afterUnrelatedReplaceAndRemove = ExternalWemInventoryBuilder.Build(selectedPath, sidecar);
        Assert.Equal(initialFingerprint, IndexService.Fingerprint(selectedPath, afterUnrelatedReplaceAndRemove));
        File.Delete(siblingPath);
        var afterUnrelatedRemove = ExternalWemInventoryBuilder.Build(selectedPath, sidecar);
        Assert.Equal(initialFingerprint, IndexService.Fingerprint(selectedPath, afterUnrelatedRemove));

        var replacement = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4], 96000);
        await File.WriteAllBytesAsync(selectedPath, replacement);
        var replacedInventory = ExternalWemInventoryBuilder.Build(selectedPath, sidecar);
        var replacedCatalog = await new DetroitCatalogReader().ReadAsync(selectedPath);
        Assert.NotEqual(initialFingerprint, IndexService.Fingerprint(selectedPath, replacedInventory));
        Assert.Equal(2d, Assert.Single(replacedCatalog.Media).Duration);
    }

    [Fact]
    public async Task RawCandidateResolverRejectsStreamingSizeConflictUnsupportedFlagsAndPlugin()
    {
        const uint id = 0x32435465;
        var bytes = FormatsFixtureBuilder.MakeWem([2, 4, 6, 8]);
        var cases = new (byte Mode, uint DeclaredSize, uint Plugin, byte Flags, (uint Id, byte StreamType, uint InMemorySize, uint PluginId)[]? Extra)[]
        {
            (1, (uint)bytes.Length, 0x00040001, 0, null),
            (0, (uint)bytes.Length + 1, 0x00040001, 0, null),
            (0, (uint)bytes.Length, 0x00040001, 2, null),
            (0, (uint)bytes.Length, 0x00040001, 0, [(id, 0, (uint)bytes.Length + 1, 0x00040001)]),
            (0, (uint)bytes.Length, 0x00040001, 0, [(id, 0, (uint)bytes.Length, 1)]),
            (0, (uint)bytes.Length, 0x00040002, 0, null),
            (0, (uint)bytes.Length, 0x12345678, 0, null)
        };
        foreach (var item in cases)
        {
            var request = new List<(uint Id, byte StreamType, uint InMemorySize, uint PluginId)>
                { (id, item.Mode, item.DeclaredSize, item.Plugin) };
            if (item.Extra is not null) request.AddRange(item.Extra);
            var (fixture, catalog) = await ReadRawCandidateCatalogAsync(id, [bytes], request, item.Flags);
            using (fixture)
            {
                var unresolved = Assert.Single(catalog.Media, media => media.BankName == "request_bank");
                Assert.Equal((item.Plugin & 15) is 2 or 5 ? MediaState.Unsupported : MediaState.MissingExternal, unresolved.State);
                Assert.Equal(MediaCompleteness.Unverified, Assert.Single(catalog.Media, media => media.BankName.StartsWith("raw_candidate_", StringComparison.Ordinal)).Completeness);
                if (item.Plugin == 0x12345678) Assert.Single(unresolved.CandidateRejections);
                if (item.Extra?.Any(value => value.PluginId != item.Plugin) == true) Assert.Single(unresolved.CandidateRejections);
            }
        }
    }

    [Fact]
    public async Task RawCandidateResolverKeepsDifferentPayloadsAmbiguousAndEnforcesLanguage()
    {
        const uint id = 0x43546576;
        var first = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]);
        var second = FormatsFixtureBuilder.MakeWem([9, 8, 7, 6]);
        var (fixture, catalog) = await ReadRawCandidateCatalogAsync(id, [first, second],
            [(id, (byte)0, (uint)first.Length, 0x00040001)], sourceFlags: 1);
        using (fixture)
        {
            var ambiguous = Assert.Single(catalog.Media, item => item.BankName == "request_bank");
            Assert.Equal(MediaState.Ambiguous, ambiguous.State);
            Assert.Equal(SourceAvailability.Ambiguous, ambiguous.Availability);
            Assert.Equal(MediaCompleteness.Unverified, ambiguous.Completeness);
            Assert.Equal(2, ambiguous.EquivalenceEvidence.Count);
            Assert.All(ambiguous.EquivalenceEvidence, evidence => Assert.Single(evidence.SourceLocations));
        }

        var (languageFixture, languageCatalog) = await ReadRawCandidateCatalogAsync(id, [first],
            [(id, (byte)0, (uint)first.Length, 0x00040001)], sourceFlags: 1, candidateLanguage: 202, requestLanguage: 201);
        using (languageFixture)
        {
            var missing = Assert.Single(languageCatalog.Media, item => item.BankName == "request_bank");
            Assert.Equal(MediaState.MissingExternal, missing.State);
        }
    }

    [Fact]
    public async Task RawCandidateResolverNeverUsesAStreamingPrefix()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        const uint id = 0x54657687;
        var fullWem = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]);
        var prefix = fullWem[..^2];
        const string prefixBankName = "prefix_bank";
        fixture.AddBankFixture(1, prefixBankName,
            FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(prefixBankName), [(id, prefix)],
                [(id, (byte)1, (uint)prefix.Length, 0x00040001)]));
        const string requestBankName = "request_bank";
        fixture.AddBankFixture(100, requestBankName,
            FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(requestBankName), [],
                [(id, (byte)0, (uint)fullWem.Length, 0x00040001)]));
        fixture.WriteIndex();

        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);

        var prefixEntry = Assert.Single(catalog.Media, item => item.BankName == prefixBankName && item.State == MediaState.PrefetchOnly);
        Assert.Equal(MediaCompleteness.Fragment, prefixEntry.Completeness);
        Assert.Equal(MediaState.MissingExternal, Assert.Single(catalog.Media, item => item.BankName == requestBankName).State);
    }

    private static async Task<(FormatsFixtureBuilder Fixture, AudioCatalog Catalog)> ReadRawCandidateCatalogAsync(uint mediaId,
        IReadOnlyList<byte[]> candidatePayloads,
        IReadOnlyList<(uint Id, byte StreamType, uint InMemorySize, uint PluginId)> requestSources,
        byte sourceFlags = 0, uint candidateLanguage = 0, uint requestLanguage = 0, bool reverseCandidateOrder = false, uint requestVersion = 120)
    {
        var fixture = FormatsFixtureBuilder.CreateEmpty();
        var candidates = candidatePayloads.Select((payload, index) => (Index: index, Payload: payload));
        if (reverseCandidateOrder) candidates = candidates.Reverse();
        foreach (var item in candidates)
        {
            var name = $"raw_candidate_{item.Index}";
            fixture.AddBankFixture((uint)(item.Index + 1), name,
                FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(name), [(mediaId, item.Payload)], [], languageId: candidateLanguage));
        }
        const string requestName = "request_bank";
        fixture.AddBankFixture(100, requestName,
            FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(requestName), [], requestSources, sourceFlags, requestLanguage, requestVersion));
        fixture.WriteIndex();
        return (fixture, await new DetroitCatalogReader().ReadAsync(fixture.IndexPath));
    }

    [Theory]
    [InlineData((byte)2, 0u, MediaCompleteness.Fragment)]
    [InlineData((byte)0, 900u, MediaCompleteness.Unverified)]
    [InlineData((byte)0, 0u, MediaCompleteness.Complete)]
    public async Task SourceFlagsAndDeclaredSizesParticipateInCompleteness(byte flags, uint size, MediaCompleteness expected)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddBankFixture(1, "flags", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("flags"),
            [(121u, FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]))], [(121u, (byte)0, size, 0x00040001u)], flags));
        fixture.WriteIndex();
        var media = Assert.Single((await new DetroitCatalogReader().ReadAsync(fixture.IndexPath)).Media, item => item.Slice.Length > 0);
        Assert.Equal(expected, media.Completeness);
        Assert.Equal(flags, Assert.Single(media.SourceEvidence).Declaration.Flags);
    }

    [Fact]
    public async Task LanguageSpecificSourceOnlyResolvesMatchingLanguage()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var wem = FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]);
        fixture.AddBankFixture(1, "language_a", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("language_a"),
            [(131u, wem)], [(131u, (byte)0, (uint)wem.Length, 0x00040001u)], 1, 201));
        fixture.AddBankFixture(2, "language_b", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("language_b"),
            [(131u, wem)], [(131u, (byte)0, (uint)wem.Length, 0x00040001u)], 1, 202));
        fixture.AddBankFixture(3, "stream", FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1("stream"),
            [], [(131u, (byte)2, 0u, 0x00040001u)], 1, 201));
        fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        var stream = Assert.Single(catalog.Media, media => media.BankName == "stream");
        Assert.Equal(Assert.Single(catalog.Media, media => media.BankName == "language_a").Slice, stream.Slice);
        Assert.Single(stream.Candidates);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task TruncatedLocalizationVersionIsResourceDiagnostic(int length)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1016, 1, "LOCALIZ_"u8.ToArray().Concat(new byte[length]).ToArray());
        fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        Assert.Contains(catalog.Diagnostics, diagnostic => diagnostic.Code == "MalformedLocalization");
    }

    [Fact]
    public async Task MissingPackageDoesNotPreventOtherResourcesFromIndexing()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var bytes = await File.ReadAllBytesAsync(fixture.IndexPath);
        // Move one resource reference to an absent package, preserving all remaining package records.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(105 + 2 * 28 + 24, 4), 1);
        await File.WriteAllBytesAsync(fixture.IndexPath, bytes);
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        Assert.Single(catalog.Banks);
        Assert.Contains(catalog.Media, media => media.BankKey.Length > 0);
        Assert.Contains(catalog.Diagnostics, diagnostic => diagnostic.Code == "MissingPackageSegment" && diagnostic.EntryKey is not null);
    }

    [Fact]
    public async Task DialogueHeaderAcrossScanBoundaryIsReadByAbsoluteOffset()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var dialogue = FormatsFixtureBuilder.MakeDialogueMedia("SYNTHETIC_LINE_ENG", FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]));
        var path = Path.Combine(fixture.DirectoryPath, "segment.dat");
        var bytes = new byte[1024 * 1024 + 28 + dialogue.Length];
        dialogue.CopyTo(bytes, 1024 * 1024 + 20);
        await File.WriteAllBytesAsync(path, bytes);
        var media = Assert.Single((await new DetroitCatalogReader().ReadAsync(path)).Media);
        Assert.Equal("SYNTHETIC_LINE_ENG", media.Name);
        Assert.Equal(MediaCompleteness.Complete, media.Completeness);
    }
}
