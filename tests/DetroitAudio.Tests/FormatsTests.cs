using System.Buffers.Binary;
using System.Text;
using DetroitAudio.Core;
using DetroitAudio.Formats;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class FormatsTests
{
    [Fact]
    public async Task IndexReaderResolvesTypedBankAndDialogueReferencesWithoutExportingTrailers()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var progress = new InlineProgress<IndexProgress>();

        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath, progress);

        Assert.Equal(9, catalog.Resources.Count);
        var bank = Assert.Single(catalog.Banks);
        Assert.Equal("fixture_bank", bank.Name);
        Assert.True(bank.Version == 120, string.Join(" | ", bank.Diagnostics.Select(item => item.Code + ":" + item.Message)));
        Assert.Single(bank.Chunks, chunk => chunk.Tag == "DATA");
        Assert.Contains(catalog.Names, name => name.Text == "fixture_bank" && name.Kind == NameKind.Verified);

        var dialogue = Assert.Single(catalog.Media, media => media.Language == "ENG");
        Assert.True(dialogue.IsRiff);
        Assert.Equal("Wwise Vorbis", dialogue.Codec);
        Assert.Equal(2, dialogue.Channels);
        Assert.Equal(48000, dialogue.SampleRate);
        Assert.Equal(1.25, dialogue.Duration);
        var dialogueResource = Assert.Single(catalog.Resources, resource => resource.Key == dialogue.Key);
        var wrapperHeaderLength = dialogue.Slice.Offset - dialogueResource.Slice.Offset;
        Assert.Equal(40, dialogueResource.Slice.Length - wrapperHeaderLength - dialogue.Slice.Length);

        var dialogueEvent = Assert.Single(catalog.Events, item => item.IsDialogue);
        Assert.Equal("Play_Dialogue_Line", dialogueEvent.Name);
        Assert.Contains(dialogueEvent.Key, dialogue.EventKeys);
        Assert.Equal("Synthetic subtitle", dialogue.Subtitle);
        Assert.Equal(2, catalog.Localization.Count);
        Assert.Contains(catalog.Diagnostics, diagnostic => diagnostic.Code == "LocalizationPointersUnresolved");
        var soundEvent = Assert.Single(catalog.Events, item => !item.IsDialogue);
        Assert.Equal("Play_Fixture_Sound", soundEvent.Name);
        Assert.Contains(catalog.Names, name => name.Namespace == "WwiseEvent" && name.Text == "Play_Fixture_Sound");

        var bankMedia = Assert.Single(catalog.Media, media => media.BankKey == bank.Key);
        Assert.Equal(0x8A7B6C5Du, bankMedia.Id);
        Assert.Equal(MediaState.CompleteEmbedded, bankMedia.State);
        Assert.Contains(bankMedia.Key, soundEvent.MediaKeys);
        Assert.Contains(soundEvent.Key, bankMedia.EventKeys);
        Assert.Single(progress.Items, item => item.Snapshot is not null);
    }

    [Fact]
    public async Task IndexReaderRejectsBadMagicAndTruncatedRecordTable()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var bytes = await File.ReadAllBytesAsync(fixture.IndexPath);
        bytes[0] ^= 0x20;
        var wrongMagic = Path.Combine(fixture.DirectoryPath, "wrong.idx");
        await File.WriteAllBytesAsync(wrongMagic, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new DetroitCatalogReader().ReadAsync(wrongMagic));

        bytes = await File.ReadAllBytesAsync(fixture.IndexPath);
        await File.WriteAllBytesAsync(wrongMagic, bytes[..^1]);
        var catalog = await new DetroitCatalogReader().ReadAsync(wrongMagic);
        Assert.Contains(catalog.Diagnostics, diagnostic => diagnostic.Code == "IndexTrailingBytes");
    }

    [Fact]
    public async Task IndexReaderHonorsPreCancelledToken()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DetroitCatalogReader().ReadAsync(fixture.IndexPath, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task MediaResolverPreservesPrefetchAndClassifiesExternalMissingAndAmbiguousSources()
    {
        using var fixture = FormatsFixtureBuilder.CreateMediaResolutionCatalog();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);

        var completeExternal = Assert.Single(catalog.Media, media => media.BankName == "source_unique");
        Assert.Equal(MediaState.CompleteExternal, completeExternal.State);
        Assert.NotEmpty(completeExternal.Candidates);
        Assert.True(completeExternal.Slice.Length > 0);

        var prefetchFragment = Assert.Single(catalog.Media, media => media.BankName == "source_prefetch" && media.Completeness == MediaCompleteness.Fragment);
        Assert.Equal(MediaState.PrefetchOnly, prefetchFragment.State);
        Assert.Equal(12, prefetchFragment.Slice.Length);

        var missing = Assert.Single(catalog.Media, media => media.BankName == "source_missing");
        Assert.Equal(MediaState.MissingExternal, missing.State);
        Assert.Empty(missing.Candidates);

        var ambiguous = Assert.Single(catalog.Media, media => media.BankName == "source_ambiguous");
        Assert.Equal(MediaState.Ambiguous, ambiguous.State);
        Assert.Equal(2, ambiguous.Candidates.Count);

        var plugin = Assert.Single(catalog.Media, media => media.BankName == "source_plugin");
        Assert.Equal(MediaState.Unsupported, plugin.State);
        var pluginBank = Assert.Single(catalog.Banks, bank => bank.Key == plugin.BankKey);
        Assert.Contains(pluginBank.Diagnostics, diagnostic => diagnostic.Code == "NonWemSourcePlugin");
        Assert.DoesNotContain(pluginBank.Diagnostics, diagnostic => diagnostic.Code == "MissingExternalMedia");
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }
}
