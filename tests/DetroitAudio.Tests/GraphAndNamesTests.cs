using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Indexing;
using DetroitAudio.Formats;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class GraphAndNamesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DetroitAudio-tests", Guid.NewGuid().ToString("N"));
    private AudioCatalog MakeCatalog()
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "media.dat"); File.WriteAllBytes(path, [1, 2, 3, 4]);
        var bank = new BankInfo { Key = "bank", Name = "Music", Id = 300, Version = 120 };
        bank.Objects[100] = new() { Id = 100, Type = 4, Actions = [101] };
        bank.Objects[101] = new() { Id = 101, Type = 3, ActionType = 0x0403, TargetId = 102, TargetBankId = 300 };
        bank.Objects[102] = new() { Id = 102, Type = 12, SwitchGroupId = 7, SwitchGroupKind = GameSyncKind.State,
            DecisionPaths = [new([new(GameSyncKind.State, 7, 8)], 103), new([new(GameSyncKind.State, 7, 9)], 104), new([new(GameSyncKind.State, 7, 0)], 0)] };
        bank.Objects[103] = new() { Id = 103, Type = 10, Children = [105], Markers = [new(700, .75, "Music_Bridge", 1234)] };
        bank.Objects[104] = new() { Id = 104, Type = 11, Clips = [new(2, 0, 0, 0, 0, 3)] };
        bank.Objects[105] = new() { Id = 105, Type = 11, Clips = [new(1, 0, 0, 0, 0, 2)] };
        bank.Objects[200] = new() { Id = 200, Type = 4, Actions = [201] };
        bank.Objects[201] = new() { Id = 201, Type = 3, ActionType = 0x1204, StateGroupId = 7, StateValueId = 8 };
        bank.Objects[202] = new() { Id = 202, Type = 4, Actions = [203] };
        bank.Objects[203] = new() { Id = 203, Type = 3, ActionType = 0x0101, TargetId = 103 };
        var catalog = new AudioCatalog { Banks = [bank], Events = [new() { Key = "play", BankKey = "bank", Id = 100, Name = "Play_Music" }, new() { Key = "state", BankKey = "bank", Id = 200, Name = "Music_SetState_Chase" }, new() { Key = "stop", BankKey = "bank", Id = 202, Name = "Stop_Music" }] };
        for (uint id = 1; id <= 2; id++) catalog.Media.Add(new() { Key = "media" + id, BankKey = "bank", BankName = "Music", Id = id, Name = id.ToString(), State = MediaState.CompleteEmbedded, Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid, Availability = SourceAvailability.Available, Duration = 2, IsRiff = true, Slice = new(path, 0, 4) });
        return catalog;
    }

    [Fact] public void StateBranchBrowseLabelsAndPreviewUseSameSource()
    {
        var catalog = MakeCatalog(); CatalogRelationships.Build(catalog); NameRecovery.ApplyAutomatic(catalog);
        var state = catalog.Events[1]; Assert.Equal(EventBehavior.StateChange, state.Behavior); Assert.Equal(EventLinkStatus.Resolved, state.LinkStatus);
        Assert.Equal("media1", Assert.Single(state.MediaKeys)); Assert.Equal("play", Assert.Single(state.RelatedPlayEventKeys));
        Assert.Equal(EventLinkKind.StateBranch, Assert.Single(state.MediaLinks).Kind);
        Assert.Empty(catalog.Events[2].MediaKeys); Assert.Equal(EventBehavior.Control, catalog.Events[2].Behavior);
        Assert.Equal("Music_SetState_Chase", catalog.Media[0].DisplayName); Assert.Equal("1", catalog.Media[0].Name);
        Assert.Equal("Music", catalog.Media[0].Category); Assert.Equal("—", catalog.Media[0].LanguageDisplay); Assert.Equal(0u, catalog.Media[0].RawLanguageId);
        Assert.Contains(catalog.Media[0].Names, name => name.Kind == NameKind.Description && name.Namespace == "MediaLabel");
        Assert.Contains("Music_Bridge", catalog.Media[0].Aliases);
        Assert.Contains(catalog.Media[0].Names, name => name.Namespace == "MusicMarker" && name.Kind == NameKind.Stored && name.Offset == 1234);
        var plan = EventPlanner.Create(catalog, state, new EventOptions(Language: "ENG"));
        Assert.Equal("state", plan.EventKey); Assert.Equal(1u, Assert.Single(plan.Items).MediaId); Assert.InRange(plan.DurationSeconds, 1.99, 2.01);
    }

    [Fact] public void StatePreviewRequiresChoiceWhenMultiplePlayEventsApply()
    {
        var catalog = MakeCatalog(); var bank = catalog.Banks[0]; bank.Objects[300] = new() { Id = 300, Type = 4, Actions = [101] };
        catalog.Events.Add(new() { Key = "play2", BankKey = "bank", Id = 300, Name = "Play_Other" }); CatalogRelationships.Build(catalog);
        Assert.Equal(2, catalog.Events[1].RelatedPlayEventKeys.Count);
        Assert.Throws<AudioToolException>(() => EventPlanner.Create(catalog, catalog.Events[1]));
        Assert.Single(EventPlanner.Create(catalog, catalog.Events[1], new(RelatedPlayEventKey: "play2")).Items);
    }

    [Fact]
    public void MarkerContextDoesNotHideCyclesInMusicSegments()
    {
        var bank = new BankInfo { Key = "cycle-bank", Id = 1 };
        bank.Objects[10] = new() { Id = 10, Type = 10, Children = [11, 12], Markers = [new(1, 0, "First", 100)] };
        bank.Objects[11] = new() { Id = 11, Type = 10, Children = [10], Markers = [new(2, 0, "Second", 200)] };
        bank.Objects[12] = new() { Id = 12, Type = 11, Sources = [new(50, 0, 4, 0, 1)] };
        var result = new CatalogGraph(new() { Banks = [bank] }).Explore(bank, 10);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GraphCycle");
        Assert.Single(result.Sources);
    }

    [Fact]
    public void SegmentMarkersFollowTheirOwnMediaPathsAndSharedTracksKeepBothContexts()
    {
        var bank = new BankInfo { Key = "marker-bank", Id = 300, Name = "MarkerBank" };
        bank.Objects[1] = new() { Id = 1, Type = 4, Actions = [2] };
        bank.Objects[2] = new() { Id = 2, Type = 3, ActionType = 0x0403, TargetId = 3, TargetBankId = 300 };
        bank.Objects[3] = new() { Id = 3, Type = 12, Children = [10, 11] };
        bank.Objects[10] = new() { Id = 10, Type = 10, Children = [20, 21], Markers = [new(701, .5, "Segment_Alpha", 1100)] };
        bank.Objects[11] = new() { Id = 11, Type = 10, Children = [20, 22], Markers = [new(702, 1.5, "Segment_Beta", 2200)] };
        bank.Objects[20] = new() { Id = 20, Type = 11, Clips = [new(100, 20, 0, 0, 0, 3)] };
        bank.Objects[21] = new() { Id = 21, Type = 11, Clips = [new(101, 21, 0, 0, 0, 4)] };
        bank.Objects[22] = new() { Id = 22, Type = 11, Clips = [new(102, 22, 0, 0, 0, 5)] };
        var catalog = new AudioCatalog
        {
            Banks = [bank],
            Events = [new() { Key = "marker-play", Id = 1, Name = "Stored_Play", BankKey = bank.Key }],
            Media =
            [
                new() { Key = "media-shared-track", Id = 100, BankKey = bank.Key, Name = "SharedTrack.wav" },
                new() { Key = "media-alpha", Id = 101, BankKey = bank.Key, Name = "AlphaTrack.wav" },
                new() { Key = "media-beta", Id = 102, BankKey = bank.Key, Name = "BetaTrack.wav" }
            ]
        };

        CatalogRelationships.Build(catalog);
        NameRecovery.ApplyAutomatic(catalog);

        var shared = catalog.Media.Single(media => media.Key == "media-shared-track");
        var alpha = catalog.Media.Single(media => media.Key == "media-alpha");
        var beta = catalog.Media.Single(media => media.Key == "media-beta");
        Assert.Equal(new[] { "Segment_Alpha", "Segment_Beta" }, shared.Aliases.Where(alias => alias.StartsWith("Segment_", StringComparison.Ordinal)).Order().ToArray());
        Assert.Contains("Segment_Alpha", alpha.Aliases);
        Assert.DoesNotContain("Segment_Beta", alpha.Aliases);
        Assert.Contains("Segment_Beta", beta.Aliases);
        Assert.DoesNotContain("Segment_Alpha", beta.Aliases);
        Assert.Contains(shared.Names, name => name.Source == "marker-bank:segment:10:marker" && name.Offset == 1100);
        Assert.Contains(shared.Names, name => name.Source == "marker-bank:segment:11:marker" && name.Offset == 2200);
        Assert.Equal("SharedTrack.wav", shared.DisplayName);
        Assert.Equal("AlphaTrack.wav", alpha.DisplayName);
        Assert.Equal("BetaTrack.wav", beta.DisplayName);
        Assert.Equal("Stored_Play", catalog.Events.Single().Name);
    }

    [Fact] public void SilentBranchRemainsEmptyAndStoredFilenameWins()
    {
        var catalog = MakeCatalog(); catalog.Banks[0].Objects[201].StateValueId = 0; catalog.Media[0].Name = "Original.wav";
        CatalogRelationships.Build(catalog); NameRecovery.ApplyAutomatic(catalog);
        Assert.Empty(catalog.Events[1].MediaKeys); Assert.Equal(EventLinkStatus.Empty, catalog.Events[1].LinkStatus);
        Assert.Equal("Original.wav", catalog.Media[0].DisplayName);
    }

    [Fact] public void BankContextDisambiguatesObjectsAndPreservesOtherMatches()
    {
        var catalog = MakeCatalog(); var second = new BankInfo { Key = "second", Id = 400 }; var third = new BankInfo { Key = "third", Id = 500 };
        second.Objects[999] = new() { Id = 999, Type = 2 }; third.Objects[999] = new() { Id = 999, Type = 2 }; catalog.Banks.AddRange([second, third]);
        var graph = new CatalogGraph(catalog); Assert.True(graph.Resolve(catalog.Banks[0], 999).Ambiguous);
        Assert.Equal("second", graph.Resolve(catalog.Banks[0], 999, bankId: 400).Unique!.Bank.Key);
    }

    [Fact] public void DecisionDefaultAppliesWithinItsSelectedParentBranch()
    {
        var node = new BankObject { DecisionPaths = [new([new(GameSyncKind.State, 10, 1), new(GameSyncKind.Switch, 11, 7)], 100), new([new(GameSyncKind.State, 10, 2), new(GameSyncKind.Switch, 11, 0)], 200)] };
        Assert.Equal(200u, Assert.Single(CatalogGraph.SelectChildren(node, [new(GameSyncKind.State, 10, 2), new(GameSyncKind.Switch, 11, 7)])));
    }

    [Fact] public void AutomaticHashRecoveryUsesObservedNamespacesAndDoesNotHashMediaNames()
    {
        var catalog = MakeCatalog(); var id = NameRecovery.Hash("Stored_Event");
        catalog.Events.Add(new() { Key = "unnamed", Id = id, Name = id.ToString() });
        catalog.Localization.Add(new("Stored_Event", "ENG", "caption", "localization", 12));
        catalog.Media[1].Id = id; catalog.Media[1].Name = id.ToString();
        NameRecovery.ApplyAutomatic(catalog);
        Assert.Equal("Stored_Event", catalog.Events[^1].Name);
        Assert.DoesNotContain(catalog.Media[1].Names, name => name.Text == "Stored_Event");
        Assert.Contains(catalog.Events[^1].Names, name => name.Source == "localization" && name.HashMethod is not null);
    }

    [Fact] public void CollidingEventCandidatesStaySearchableWithoutChoosingOneAsLabel()
    {
        var catalog = MakeCatalog(); catalog.Events[1].Name = "200";
        var first = new NameEvidence("FirstCandidate", "WwiseEvent", 200, NameKind.Candidate, "metadata");
        var second = first with { Text = "SecondCandidate" };
        catalog.Events[1].Names.AddRange([first, second]); catalog.Names.AddRange([first, second]);
        CatalogRelationships.Build(catalog); NameRecovery.ApplyAutomatic(catalog);
        Assert.Equal("200", catalog.Events[1].Name);
        Assert.Contains("FirstCandidate", catalog.Media[0].Aliases); Assert.Contains("SecondCandidate", catalog.Media[0].Aliases);
        Assert.NotEqual("FirstCandidate", catalog.Media[0].DisplayName); Assert.NotEqual("SecondCandidate", catalog.Media[0].DisplayName);
        Assert.All(catalog.Media[0].Names.Where(name => name.Kind == NameKind.Candidate), name => Assert.NotNull(name.CollisionGroup));
    }

    [Fact] public void FullQueryEnumerationAndSearchCoverRowsBeyondFirstBlock()
    {
        using var store = new CatalogStore(directory);
        var media = Enumerable.Range(0, 1205).Select(index => new MediaEntry { Key = index.ToString("D5"), Id = (uint)index, Name = "Original" + index.ToString("D5"), DisplayLabel = "Label" + index.ToString("D5"), BankName = "Bank", Language = "SFX" }).ToList();
        media[^1].Aliases.Add("Final_Entry"); store.Save(new() { Fingerprint = "large", Media = media }, null, default);
        Assert.Equal(1205, store.Count(new())); Assert.Equal(500, store.ReadPage(new(Limit: 500)).Count);
        Assert.Equal(1205, store.Enumerate(new()).Select(entry => entry.Key).Distinct().Count());
        Assert.Equal(1204u, Assert.Single(store.ReadPage(new(Search: "Final_Entry"))).Id);
        Assert.Equal(1204u, Assert.Single(store.ReadPage(new(Search: "Original01204"))).Id);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => store.Count(new(), cancelled.Token));
    }

    [Fact] public void CancelledGraphAndNamingWorkStopsBeforePublishing()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CatalogRelationships.Build(new(), cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => NameRecovery.ApplyAutomatic(new(), cancellation.Token));
        var catalog = MakeCatalog(); Assert.ThrowsAny<OperationCanceledException>(() => new CatalogGraph(catalog).Explore(catalog.Banks[0], 102, token: cancellation.Token));
    }

    [Fact] public async Task BankStringMappingSuppliesNameAndPreservesLanguageId()
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "1.bnk");
        using (var file = File.Create(path))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("BKHD")); writer.Write(20u);
            writer.Write(120u); writer.Write(500u); writer.Write(12u); writer.Write(0u); writer.Write(1u);
            var name = System.Text.Encoding.UTF8.GetBytes("BankOne");
            writer.Write(System.Text.Encoding.ASCII.GetBytes("STID")); writer.Write((uint)(13 + name.Length));
            writer.Write(1u); writer.Write(1u); writer.Write(500u); writer.Write((byte)name.Length); writer.Write(name);
        }
        var catalog = await new DetroitCatalogReader().ReadAsync(path); NameRecovery.ApplyAutomatic(catalog);
        Assert.Equal("BankOne", Assert.Single(catalog.Banks).Name); Assert.Equal(12u, catalog.Banks[0].LanguageId);
        Assert.Contains(catalog.Names, name => name.Text == "BankOne" && name.Kind == NameKind.Stored && name.Offset > 0);
    }

    [Fact] public void OlderSchemaDoesNotLoadAsCurrentCatalog()
    {
        using var store = new CatalogStore(directory); store.Save(new() { Fingerprint = "old" }, null, default);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE metadata SET value='2' WHERE name='schema'"; command.ExecuteNonQuery();
        }
        Assert.Null(store.Load("old"));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
