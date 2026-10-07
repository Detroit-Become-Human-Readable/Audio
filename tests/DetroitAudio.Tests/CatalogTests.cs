using DetroitAudio.Core;
using DetroitAudio.Indexing;
using DetroitAudio.Export;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DetroitAudio-tests", Guid.NewGuid().ToString("N"));
    [Fact] public void CatalogueQueriesKeepDuplicateIdsAndScopedRelationships()
    {
        using var store = new CatalogStore(directory);
        var catalog = new AudioCatalog { Fingerprint = "test-v1", Media = [Entry("a", "Ambient_Bird", "bank-a", "SFX"), Entry("b", "Ambient_Bird", "bank-b", "ENG"), Entry("c", "100%_wind", "bank-a", "SFX")] };
        catalog.Media[0].EventKeys.Add("event-a"); catalog.Media[1].Subtitle = "Hallo Welt";
        store.Save(catalog, null, default);
        Assert.Equal(3, store.Query(new()).Total);
        Assert.Equal(2, store.Query(new(Search: "Ambient")).Total);
        Assert.Single(store.Query(new(EventKey: "event-a")).Entries);
        Assert.Equal("b", Assert.Single(store.Query(new(Search: "HALLO", Language: "ENG")).Entries).Key);
        Assert.Equal("c", Assert.Single(store.Query(new(Search: "100%_")).Entries).Key);
        Assert.Null(store.Load("different-source")); Assert.NotNull(store.Load("test-v1"));
    }
    [Fact] public void CancelledSaveLeavesPreviousCatalogueReadable()
    {
        using var store = new CatalogStore(directory); store.Save(new() { Fingerprint = "first", Media = [Entry("first", "First", "bank", "SFX")] }, null, default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Save(new() { Fingerprint = "second", Media = [Entry("second", "Second", "bank", "SFX")] }, null, cancellation.Token));
        Assert.NotNull(store.Load("first")); Assert.Equal("first", Assert.Single(store.Query(new()).Entries).Key);
    }
    [Theory] [InlineData("CON", "_CON")] [InlineData("..", "Unnamed")] [InlineData("Bird:Song?", "BirdSong")] [InlineData("name. ", "name")]
    public void OutputNamesHandleWindowsNames(string value, string expected) => Assert.Equal(expected, OutputNaming.Segment(value));
    [Fact] public void OutputPathsRejectTraversal() => Assert.Throws<IOException>(() => OutputNaming.Resolve(directory, "../escaped.wem"));
    [Fact] public async Task SliceCopyUsesBoundsAndPreservesExactBytes()
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "sample.bin"); File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        using var output = new MemoryStream(); await SliceReader.CopyAsync(new(path, 1, 3), output); Assert.Equal(new byte[] { 2, 3, 4 }, output.ToArray());
        Assert.Throws<InvalidDataException>(() => SliceReader.Read(new(path, 4, 2)));
    }
    private static MediaEntry Entry(string key, string name, string bank, string language) => new() { Key = key, Id = 42, Name = name, BankKey = bank, BankName = bank, Language = language, Slice = new("sample.dat", 8, 64) };
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
