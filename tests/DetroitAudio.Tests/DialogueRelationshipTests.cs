using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class DialogueRelationshipTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dialogue-links-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReverseLinksPreserveLanguageVariantsAndExactEventKeysThroughLazyLoading()
    {
        Directory.CreateDirectory(root); var file = Path.Combine(root, "sample.dat"); File.WriteAllBytes(file, new byte[32]);
        var english = Clip("english", "ENG", file); var french = Clip("french", "FRE", file);
        english.EventKeys = ["event-a", "event-a"]; french.EventKeys = ["event-a"];
        var entry = new AudioEvent { Key = "event-a", Id = 10, IsDialogue = true };
        var other = new AudioEvent { Key = "event-b", Id = 10, IsDialogue = true };
        var catalog = new AudioCatalog { SourcePath = file, Fingerprint = "generated", Media = [english, french], Events = [entry, other] };

        CatalogRelationships.Build(catalog); CatalogRelationships.Build(catalog);
        Assert.Equal(new[] { "english", "french" }, entry.MediaKeys.Order().ToArray());
        Assert.Equal(EventLinkStatus.Resolved, entry.LinkStatus);
        Assert.Equal(2, entry.MediaLinks.Count); Assert.All(entry.MediaLinks, link => Assert.Equal(EventLinkKind.Direct, link.Kind));
        Assert.Empty(other.MediaKeys); Assert.Equal(EventLinkStatus.Empty, other.LinkStatus);
        using var store = new CatalogStore(Path.Combine(root, "catalog")); store.Save(catalog, null, default);
        var operation = store.CreateOperationCatalog(eventKeys: [entry.Key]);
        Assert.Equal(2, operation.Media.Count);
        Assert.Equal("french", Assert.Single(EventPlanner.Create(operation, Assert.Single(operation.Events), new(Language: "FRE")).Items).MediaKey);
        Assert.Equal("english", Assert.Single(EventPlanner.Create(operation, Assert.Single(operation.Events), new(Language: "ENG")).Items).MediaKey);

        french.EventKeys.Clear(); CatalogRelationships.Build(catalog);
        Assert.Equal("english", Assert.Single(entry.MediaKeys)); Assert.Single(entry.MediaLinks);
    }

    [Fact]
    public void CatalogsWithThePreviousDialogueLinkVersionAreRejected()
    {
        using var store = new CatalogStore(root); store.Save(new AudioCatalog { Fingerprint = "generated" }, null, default);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE metadata SET value='8' WHERE name='schema'"; command.ExecuteNonQuery();
        }
        Assert.Null(store.ReadHeader("generated"));
    }

    private static MediaEntry Clip(string key, string language, string file) => new()
    {
        Key = key, Id = 42, Name = "Line", Language = language, Category = "Dialogue", Slice = new(file, 0, 32),
        State = MediaState.CompleteEmbedded, Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid,
        Availability = SourceAvailability.Available, IsRiff = true, Codec = "PCM", Duration = 2, Channels = 1, SampleRate = 48000
    };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
