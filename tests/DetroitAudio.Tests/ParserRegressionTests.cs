using DetroitAudio.Core;
using DetroitAudio.Formats;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class ParserRegressionTests
{
    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public void ZeroTargetStateAndSwitchActionsRetainGameSyncValues(uint version)
    {
        var state = Parse(3, version, writer =>
        {
            writer.Write((ushort)0x1204);
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write((byte)0); // property count
            writer.Write((byte)0); // property-range count
            writer.Write(61u);
            writer.Write(62u);
        });
        Assert.NotEqual(PlanSupport.Unsupported, state.Support);
        Assert.Equal((ushort)0x1204, state.ActionType);
        Assert.Null(state.TargetId);
        Assert.Empty(state.Children);
        Assert.Equal(61u, state.StateGroupId);
        Assert.Equal(62u, state.StateValueId);

        var change = Parse(3, version, writer =>
        {
            writer.Write((ushort)0x1904);
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(71u);
            writer.Write(72u);
        });
        Assert.NotEqual(PlanSupport.Unsupported, change.Support);
        Assert.Equal(71u, change.SwitchGroupId);
        Assert.Equal(72u, change.DefaultSwitchId);
        Assert.Equal(GameSyncKind.Switch, change.SwitchGroupKind);
        Assert.Empty(change.Children);
    }

    [Fact]
    public void PlayActionPreservesFlagsAndBankContext()
    {
        var play = Parse(3, 120, writer =>
        {
            writer.Write((ushort)0x0403);
            writer.Write(41u);
            writer.Write((byte)0x81);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)4); // fade curve
            writer.Write(51u); // bank context
        });
        Assert.NotEqual(PlanSupport.Unsupported, play.Support);
        Assert.Equal(41u, play.TargetId);
        Assert.Equal((byte)0x81, play.TargetFlags);
        Assert.Equal(51u, play.TargetBankId);
        Assert.Equal(new uint[] { 41 }, play.Children);
    }

    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public void MusicSegmentConsumesOneFlagsByte(uint version)
    {
        var segment = Parse(10, version, writer =>
        {
            MusicNode(writer, 91);
            writer.Write(3400d); // segment duration in milliseconds
            writer.Write(1u);
            writer.Write(1u); // exit marker
            writer.Write(3400d);
            writer.Write(0u); // marker string size
        });
        Assert.NotEqual(PlanSupport.Unsupported, segment.Support);
        Assert.Equal(new uint[] { 91 }, segment.Children);
        Assert.DoesNotContain(segment.Limitations, value => value.Contains("trailing HIRC"));
    }

    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public void MusicSegmentRetainsMarkerTimeTextAndBodyLocation(uint version)
    {
        var segment = Parse(10, version, writer =>
        {
            MusicNode(writer, 92);
            writer.Write(5000d);
            writer.Write(1u);
            writer.Write(701u);
            writer.Write(2750d);
            var text = System.Text.Encoding.UTF8.GetBytes("Verse_Entry");
            writer.Write((uint)text.Length);
            writer.Write(text);
        }, bodyOffset: 4096);

        var marker = Assert.Single(segment.Markers);
        Assert.Equal(701u, marker.Id);
        Assert.Equal(2.75d, marker.PositionSeconds);
        Assert.Equal("Verse_Entry", marker.Text);
        Assert.True(marker.SourceOffset >= 4096);
    }

    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public void PlaylistAcceptsNoneSentinelForLeafAndPreservesLeafLinks(uint version)
    {
        var playlist = Parse(13, version, writer =>
        {
            MusicNode(writer);
            writer.Write(0u); // transition rules
            writer.Write(2u); // serialized playlist item count
            PlaylistNode(writer, 0, 1, 1);
            PlaylistNode(writer, 93, 0, uint.MaxValue);
        });
        Assert.NotEqual(PlanSupport.Unsupported, playlist.Support);
        Assert.True(playlist.Sequential);
        Assert.Equal(new uint[] { 93 }, playlist.Children);
    }

    [Fact]
    public void PlaylistKeepsOwnedSegmentsSeparateFromPlaybackLeaves()
    {
        var playlist = Parse(13, 120, writer =>
        {
            MusicNode(writer, 2001, 2002);
            writer.Write(0u); // transition rules
            writer.Write(2u); // root plus leaf
            PlaylistNode(writer, 0, 1, 0);
            PlaylistNode(writer, 2002, 0, uint.MaxValue);
        });
        Assert.NotEqual(PlanSupport.Unsupported, playlist.Support);
        Assert.Equal(new uint[] { 2002 }, playlist.Children);
        Assert.Equal(new uint[] { 2001, 2002 }, playlist.OwnedChildren);
    }

    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public void DecisionTreeReadsWeightsAndCompleteStateSwitchPaths(uint version)
    {
        var tree = Parse(12, version, writer =>
        {
            MusicNode(writer);
            writer.Write(0u); // transition rules
            writer.Write((byte)1); // continue playback
            writer.Write(2u); // depth
            writer.Write(11u); // State group
            writer.Write(12u); // Switch group
            writer.Write((byte)1); // State
            writer.Write((byte)0); // Switch
            writer.Write(5u * 12u);
            writer.Write((byte)0); // best-match mode
            DecisionNode(writer, 0, (1u << 16) | 1, 77, 88);
            DecisionNode(writer, 21, (3u << 16) | 2, 44, 55);
            DecisionNode(writer, 22, 1001, 15, 80);
            DecisionNode(writer, 23, 1002, 25, 90);
            DecisionNode(writer, 0, 0, 35, 100); // silent/default branch
        });
        Assert.NotEqual(PlanSupport.Unsupported, tree.Support);
        Assert.Equal(new uint[] { 1001, 1002 }, tree.Children);
        Assert.Equal(3, tree.DecisionPaths.Count);
        var first = tree.DecisionPaths[0];
        Assert.Equal(new[] { new GameSyncValue(GameSyncKind.State, 11, 21), new GameSyncValue(GameSyncKind.Switch, 12, 22) }, first.Values);
        Assert.Equal(1001u, first.TargetId);
        Assert.Equal((ushort)15, first.Weight);
        Assert.Equal((ushort)80, first.Probability);
        var silent = tree.DecisionPaths[2];
        Assert.Equal(0u, silent.TargetId);
        Assert.Equal(0u, silent.Values[1].ValueId);
        Assert.Equal((ushort)100, silent.Probability);
    }

    [Fact]
    public void SingleGroupDecisionTreeRetainsSilentDefaultAndStateKind()
    {
        var tree = Parse(12, 120, writer =>
        {
            MusicNode(writer);
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write(1u);
            writer.Write(31u);
            writer.Write((byte)1);
            writer.Write(36u);
            writer.Write((byte)0);
            DecisionNode(writer, 0, (2u << 16) | 1, 1, 100);
            DecisionNode(writer, 32, 1003, 2, 100);
            DecisionNode(writer, 0, 0, 3, 100);
        });
        Assert.NotEqual(PlanSupport.Unsupported, tree.Support);
        Assert.Equal(GameSyncKind.State, tree.SwitchGroupKind);
        Assert.Equal(31u, tree.SwitchGroupId);
        Assert.Contains(tree.Branches, branch => branch.ValueId == 0 && branch.Children.Count == 0);
        Assert.Equal(2, tree.DecisionPaths.Count);
    }

    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public void MusicTrackConvertsMillisecondsAndConsumesLookAhead(uint version)
    {
        var track = Parse(11, version, writer =>
        {
            TrackHeader(writer, 1);
            writer.Write(4u); // track ID
            writer.Write(1004u); // media ID
            writer.Write(1250d);
            writer.Write(500d);
            writer.Write(-250d);
            writer.Write(5000d);
            writer.Write(1u); // subtracks
            writer.Write(0u); // automations
            NodeBase(writer);
            writer.Write((byte)0);
            writer.Write(25); // look-ahead milliseconds
        });
        Assert.NotEqual(PlanSupport.Unsupported, track.Support);
        Assert.Equal(new MusicClip(1004, 4, 1.25, .5, -.25, 5), Assert.Single(track.Clips));
        Assert.DoesNotContain(track.Limitations, value => value.Contains("trailing HIRC"));
    }

    [Theory]
    [InlineData(118u)]
    [InlineData(120u)]
    public void MusicTracksRetainStorageDeclarationsAlongsidePlaybackClips(uint version)
    {
        var track = Parse(11, version, writer =>
        {
            writer.Write((byte)0);
            writer.Write(1u);
            writer.Write(1u); // plug-in
            writer.Write((byte)1); // prefetch
            writer.Write(140u);
            writer.Write(2048u);
            writer.Write((byte)3); // language-specific + prefetch
            writer.Write(1u); // clips
            writer.Write(1u); // subtrack ID
            writer.Write(140u);
            writer.Write(0d); writer.Write(0d); writer.Write(0d); writer.Write(7000d);
            writer.Write(1u); // subtracks
            writer.Write(0u); // automation
            NodeBase(writer);
            writer.Write((byte)0);
            writer.Write(0);
        });
        Assert.Equal(new MediaReference(140, 1, 2048, 3, 1), Assert.Single(track.Sources));
        Assert.Equal(140u, Assert.Single(track.Clips).MediaId);
        Assert.Equal(7d, Assert.Single(track.Clips).Duration);
    }

    [Fact]
    public void CompleteLinksSurviveUnsupportedTrailingPlaybackData()
    {
        var track = Parse(11, 120, writer =>
        {
            TrackHeader(writer, 1);
            writer.Write(1u);
            writer.Write(1005u);
            writer.Write(0d);
            writer.Write(0d);
            writer.Write(0d);
            writer.Write(1000d);
            writer.Write(1u);
            writer.Write(1u); // truncated automation record
            writer.Write(0u);
        });
        Assert.Equal(PlanSupport.Unsupported, track.Support);
        Assert.Equal(1005u, Assert.Single(track.Clips).MediaId);
        Assert.Empty(track.Sources);
    }

    [Fact]
    public void TruncatedTablesNeverPublishPartialLinks()
    {
        var eventNode = Parse(4, 120, writer =>
        {
            writer.Write(2u);
            writer.Write(7u);
            writer.Write(0u); // invalid second action
        });
        Assert.Equal(PlanSupport.Unsupported, eventNode.Support);
        Assert.Empty(eventNode.Actions);

        var segment = Parse(10, 120, writer =>
        {
            writer.Write((byte)0);
            NodeBase(writer);
            writer.Write(2u);
            writer.Write(8u);
            writer.Write(8u); // duplicate child
        });
        Assert.Equal(PlanSupport.Unsupported, segment.Support);
        Assert.Empty(segment.Children);

        var track = Parse(11, 120, writer =>
        {
            TrackHeader(writer, 2);
            writer.Write(1u);
            writer.Write(1006u);
            writer.Write(0d);
            writer.Write(0d);
            writer.Write(0d);
            writer.Write(1000d); // second clip missing
        });
        Assert.Equal(PlanSupport.Unsupported, track.Support);
        Assert.Empty(track.Clips);
    }

    [Fact]
    public void TruncatedPlaylistRetainsCompleteChildTableWithoutPartialPlaylistLinks()
    {
        var playlist = Parse(13, 120, writer =>
        {
            MusicNode(writer, 2001);
            writer.Write(0u);
            writer.Write(3u);
            PlaylistNode(writer, 0, 2, 0);
            PlaylistNode(writer, 2002, 0, uint.MaxValue);
            writer.Write(2003u); // incomplete second leaf
        });
        Assert.Equal(PlanSupport.Unsupported, playlist.Support);
        Assert.Equal(new uint[] { 2001 }, playlist.Children);
    }

    [Fact]
    public void OverlappingDecisionChildRangesDoNotPublishPaths()
    {
        var tree = Parse(12, 120, writer =>
        {
            MusicNode(writer, 2004);
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write(2u);
            writer.Write(41u);
            writer.Write(42u);
            writer.Write((byte)1);
            writer.Write((byte)0);
            writer.Write(4u * 12u);
            writer.Write((byte)0);
            DecisionNode(writer, 0, (2u << 16) | 1, 1, 100);
            DecisionNode(writer, 43, (1u << 16) | 3, 1, 100);
            DecisionNode(writer, 44, (1u << 16) | 3, 1, 100);
            DecisionNode(writer, 45, 2005, 1, 100);
        });
        Assert.Equal(PlanSupport.Unsupported, tree.Support);
        Assert.Empty(tree.DecisionPaths);
        Assert.Equal(new uint[] { 2004 }, tree.Children);
    }

    private static BankObject Parse(byte type, uint version, Action<BinaryWriter> write, long bodyOffset = 0)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) write(writer);
        var item = new BankObject { Type = type, Id = 101, BodySlice = new DataSlice("synthetic.bnk", bodyOffset, stream.Length) };
        WwiseNodeReader.Parse(item, stream.ToArray(), version);
        return item;
    }

    private static void NodeBase(BinaryWriter writer)
    {
        writer.Write((byte)0); // FX override
        writer.Write((byte)0); // FX count
        writer.Write((byte)0); // attachment override
        writer.Write(0u); // bus ID
        writer.Write(0u); // parent ID
        writer.Write((byte)0); // priority flags
        writer.Write((byte)0); // properties
        writer.Write((byte)0); // ranges
        writer.Write((byte)0); // positioning
        writer.Write((byte)0); // auxiliary sends
        writer.Write(new byte[6]); // advanced settings
        writer.Write(0u); // state groups
        writer.Write((ushort)0); // RTPCs
    }

    private static void MusicNode(BinaryWriter writer, params uint[] children)
    {
        writer.Write((byte)0); // music flags, exactly once
        NodeBase(writer);
        writer.Write((uint)children.Length);
        foreach (var child in children) writer.Write(child);
        writer.Write(500d); // grid period
        writer.Write(0d); // grid offset
        writer.Write(120f); // tempo
        writer.Write((byte)4); // time signature
        writer.Write((byte)4);
        writer.Write((byte)0); // meter override
        writer.Write(0u); // stingers
    }

    private static void PlaylistNode(BinaryWriter writer, uint segment, uint children, uint mode)
    {
        writer.Write(segment);
        writer.Write(0u); // playlist item ID
        writer.Write(children);
        writer.Write(mode);
        writer.Write((short)1); // loops
        writer.Write((short)0); // minimum loop modifier
        writer.Write((short)0); // maximum loop modifier
        writer.Write(100u); // weight
        writer.Write((ushort)0); // avoid repeat
        writer.Write((byte)0); // use weight
        writer.Write((byte)0); // shuffle
    }

    private static void DecisionNode(BinaryWriter writer, uint key, uint data, ushort weight, ushort probability)
    {
        writer.Write(key);
        writer.Write(data);
        writer.Write(weight);
        writer.Write(probability);
    }

    private static void TrackHeader(BinaryWriter writer, uint clips)
    {
        writer.Write((byte)0); // MIDI overrides
        writer.Write(0u); // source count
        writer.Write(clips);
    }
}
