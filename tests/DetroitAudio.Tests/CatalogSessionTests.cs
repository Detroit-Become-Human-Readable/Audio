using DetroitAudio.Core;
using DetroitAudio.Indexing;
using DetroitAudio.Audio;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class CatalogSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "catalog-session-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void RowsDoNotHydrateEvidenceAndBankSortUsesVisibleNames()
    {
        using var store = new CatalogStore(directory);
        var catalog = new AudioCatalog { SourcePath = "sample.idx", Fingerprint = "test", Media =
        [Entry("first", 20, "z-key", "Alpha", "Sound_%_Bird"), Entry("second", 10, "a-key", "Zebra", "MusicRain")] };
        catalog.Media[0].Names.Add(new("a long alias", "MediaLabel", 20, NameKind.Description, "generated"));
        store.Save(catalog, null, default);
        Assert.Equal(2, store.Header.MediaCount); Assert.Equal(0, store.CachedGraphCount);
        var rows = store.ReadRows(new(Sort: "Bank")); Assert.Equal(new[] { "first", "second" }, rows.Select(row => row.Key));
        Assert.Empty(rows[0].ToSummary().Names); Assert.Single(store.GetMedia("first")!.Names);
        Assert.Equal("second", store.ReadRows(new(Sort: "ID"))[0].Key);
        Assert.Equal("first", store.ReadRows(new(Sort: "ID", Descending: true))[0].Key);
        Assert.Single(store.ReadRows(new(Search: "a long alias")));
        Assert.Single(store.ReadRows(new(Search: "%_"))); // wildcard characters remain literal
        Assert.Single(store.ReadRows(new(Search: "0x00000014")));
        Assert.Single(store.ReadRows(new(Search: "Bird")));
        Assert.Empty(store.ReadRows(new(Search: "not-present")));
        Assert.NotNull(store.Load("test")); Assert.Null(store.ReadHeader("another input"));
    }
    [Fact]
    public void LazyEventCatalogIncludesCrossBankTargetsAndRetainsAmbiguity()
    {
        using var store = new CatalogStore(directory); var catalog = new AudioCatalog { Fingerprint = "graph" };
        var first = new BankInfo { Key = "first", Id = 1, Name = "First" };
        first.Objects[10] = new() { Id = 10, Type = 4, Actions = [11] };
        first.Objects[11] = new() { Id = 11, Type = 3, ActionType = 0x0403, TargetBankId = 2, TargetId = 12 };
        var second = new BankInfo { Key = "second", Id = 2, Name = "Second" };
        second.Objects[12] = new() { Id = 12, Type = 2, Sources = [new(50, 0, 4, 0, 1)] };
        catalog.Banks.AddRange([first, second]);
        var path = Path.Combine(directory, "generated.dat"); Directory.CreateDirectory(directory); File.WriteAllBytes(path, [1, 2, 3, 4]);
        var media = Entry("media", 50, "second", "Second", "Clip"); media.Slice = new(path, 0, 4); media.Duration = 2;
        catalog.Media.Add(media); catalog.Events.Add(new() { Key = "event", Id = 10, BankKey = "first", MediaKeys = ["media"], Behavior = EventBehavior.Playback });
        store.Save(catalog, null, default);
        Assert.Empty(store.GetBanks().SelectMany(bank => bank.Objects)); Assert.Equal(0, store.CachedGraphCount);
        var operation = store.CreateOperationCatalog(eventKeys: ["event"]);
        Assert.Equal(2, operation.Banks.Count); Assert.Single(operation.Media);
        var plan = EventPlanner.Create(operation, store.GetEvent("event")!, new(Language: "SFX", FadeOutSeconds: 0));
        Assert.Equal("media", Assert.Single(plan.Items).MediaKey);
        Assert.Equal(2, store.CachedGraphCount);

        // An event action can share an ID with a local object of another HIRC type.
        // The typed resolver must still be able to see an action in a different bank.
        second.Objects[11] = first.Objects[11];
        first.Objects[11] = new() { Id = 11, Type = 2 };
        store.Save(catalog, null, default);
        operation = store.CreateOperationCatalog(eventKeys: ["event"]);
        var resolved = new CatalogGraph(operation).Resolve(operation.Banks.Single(bank => bank.Key == "first"), 11, 3);
        Assert.Equal("second", resolved.Unique!.Bank.Key);
    }
    [Fact]
    public void CancellationDuringDatabaseWritesPreservesThePreviousSnapshotAndRows()
    {
        using var store = new CatalogStore(directory);
        store.Save(new() { Fingerprint = "original", Media = [Entry("original", 7, "bank", "Bank", "Original")] }, null, default);
        using var cancellation = new CancellationTokenSource();
        var replacement = new AudioCatalog { Fingerprint = "replacement", Media = Enumerable.Range(1, 2000)
            .Select(index => Entry("new-" + index, (uint)index, "bank", "Bank", "Replacement")).ToList() };
        var progress = new InlineProgress(value => { if (value.Phase == "Catalog" && value.Completed >= 500) cancellation.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => store.Save(replacement, progress, cancellation.Token));
        Assert.Equal("original", store.ReadHeader()!.Fingerprint);
        Assert.Equal("original", Assert.Single(store.ReadRows(new())).Key);
        Assert.Equal("original", store.Load("original")!.Fingerprint);
    }

    private sealed class InlineProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }

    [Fact]
    public void GraphCacheIsBoundedAndCancelledRebuildLeavesOldHeader()
    {
        using var store = new CatalogStore(directory);
        var catalog = new AudioCatalog { Fingerprint = "original", Banks = Enumerable.Range(1, 7).Select(index => new BankInfo { Key = "bank-" + index, Id = (uint)index, Name = "Bank" + index }).ToList() };
        store.Save(catalog, null, default);
        foreach (var bank in catalog.Banks) Assert.NotNull(store.GetBankGraph(bank.Key));
        Assert.Equal(4, store.CachedGraphCount);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Save(new() { Fingerprint = "replacement" }, null, cancellation.Token));
        Assert.Equal("original", store.ReadHeader()!.Fingerprint);
        Assert.ThrowsAny<OperationCanceledException>(() => store.ReadRows(new(), cancellation.Token));
    }
    private static MediaEntry Entry(string key, uint id, string bank, string bankName, string name) => new()
    {
        Key = key, Id = id, BankKey = bank, BankName = bankName, Name = name, Slice = new("generated.dat", 0, 4),
        State = MediaState.CompleteEmbedded, Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid,
        Availability = SourceAvailability.Available, IsRiff = true, Codec = "PCM", Language = "SFX", SampleRate = 48000, Channels = 2
    };
    public void Dispose() { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { } }
}
