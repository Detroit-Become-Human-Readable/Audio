using System.Buffers.Binary;
using DetroitAudio.Core;

namespace DetroitAudio.Formats;

/// <summary>
/// Bounded decoder for the HIRC structures used by Detroit's Wwise 118/120 banks.
/// The input is one object's body, excluding the HIRC type/size/ID wrapper.
/// </summary>
internal static class WwiseNodeReader
{
    private const int MaxListItems = 16_384;
    private const int MaxTreeDepth = 32;

    public static void Parse(BankObject item, ReadOnlySpan<byte> payload, uint version, bool hasFeedback = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Children.Clear();
        item.OwnedChildren.Clear();
        item.Actions.Clear();
        item.Sources.Clear();
        item.Clips.Clear();
        item.Markers.Clear();
        item.Branches.Clear();
        item.DecisionPaths.Clear();
        item.ParentId = null;
        item.TargetId = null;
        item.TargetFlags = 0;
        item.TargetBankId = null;
        item.StateGroupId = null;
        item.StateValueId = null;
        item.SwitchGroupId = null;
        item.SwitchGroupKind = GameSyncKind.Switch;
        item.DefaultSwitchId = null;
        item.ActionType = 0;
        item.VolumeDb = 0;
        item.PitchCents = 0;
        item.DelaySeconds = 0;
        item.Sequential = false;
        item.LoopCount = 1;
        item.Support = PlanSupport.Supported;
        item.Limitations.Clear();

        if (version is not (118 or 120))
        {
            Unsupported(item, $"The bounded HIRC schema is validated for Wwise bank versions 118 and 120; this bank is {version}.");
            return;
        }

        var cursor = new Cursor(payload);
        var valid = item.Type switch
        {
            2 => ParseSound(item, ref cursor, version, hasFeedback),
            3 => ParseAction(item, ref cursor, version),
            4 => ParseEvent(item, ref cursor),
            5 => ParseRandomSequence(item, ref cursor, version, hasFeedback),
            6 => ParseSwitchContainer(item, ref cursor, version, hasFeedback),
            7 => ParseActorMixer(item, ref cursor, version, hasFeedback),
            9 => ParseLayerContainer(item, ref cursor, version, hasFeedback),
            10 => ParseMusicSegment(item, ref cursor, version, hasFeedback),
            11 => ParseMusicTrack(item, ref cursor, version, hasFeedback),
            12 => ParseMusicSwitchContainer(item, ref cursor, version, hasFeedback),
            13 => ParseMusicPlaylist(item, ref cursor, version, hasFeedback),
            _ => false
        };

        if (!valid)
        {
            Unsupported(item, $"The Wwise {version} HIRC body for type {item.Type} is truncated, inconsistent, or outside parser limits.");
            return;
        }

        if (cursor.Remaining > 0)
            item.Limitations.Add($"{cursor.Remaining} trailing HIRC byte(s) were retained without interpretation.");
        if (item.Support == PlanSupport.Supported) item.Support = PlanSupport.Approximate;
        item.Limitations.Add("Wwise playback details outside decoded IDs, timing metadata and basic node parameters are not simulated.");
    }

    private static bool ParseEvent(BankObject item, ref Cursor cursor)
    {
        if (!cursor.TryReadUInt32(out var count) || count > MaxListItems || !cursor.CanRead(checked((int)count * 4))) return false;
        var actions = new List<uint>((int)count);
        for (var i = 0; i < count; i++)
        {
            if (!cursor.TryReadUInt32(out var actionId) || actionId == 0) return false;
            actions.Add(actionId);
        }
        item.Actions.AddRange(actions);
        return true;
    }

    private static bool ParseSound(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadSource(ref cursor, version, out var source)) return false;
        item.Sources.Add(source);
        return TryReadNodeBase(item, ref cursor, version, hasFeedback, out _);
    }

    private static bool TryReadSource(ref Cursor cursor, uint version, out MediaReference source)
    {
        source = new MediaReference(0, 0, 0, 0, 0);
        if (!cursor.TryReadUInt32(out var pluginId) || !cursor.TryReadByte(out var streamType) ||
            !cursor.TryReadUInt32(out var sourceId) || !cursor.TryReadUInt32(out var memorySize) ||
            !cursor.TryReadByte(out var flags) || sourceId == 0 || streamType > 2)
            return false;

        // Plugin parameter blobs follow only for source plug-ins that own data blocks.
        var pluginType = pluginId & 0x0F;
        if (pluginType is 2 or 5)
        {
            if (!cursor.TryReadUInt32(out var parameterSize) || parameterSize > MaxListItems * 16 ||
                !cursor.TrySkip(checked((int)parameterSize))) return false;
        }
        source = new MediaReference(sourceId, streamType, memorySize, flags, pluginId);
        return true;
    }

    private static bool ParseAction(BankObject item, ref Cursor cursor, uint version)
    {
        if (!cursor.TryReadUInt16(out var actionType) || !cursor.TryReadUInt32(out var targetId) ||
            !cursor.TryReadByte(out var idExtFlags))
            return false;

        item.ActionType = actionType;
        item.TargetId = targetId == 0 ? null : targetId;
        item.TargetFlags = idExtFlags;
        if (targetId != 0) item.Children.Add(targetId);
        if (!TryReadPropertyBundle(ref cursor, item, ranged: false) ||
            !TryReadPropertyBundle(ref cursor, item, ranged: true)) return false;
        if ((idExtFlags & 1) != 0)
            item.Limitations.Add("The action target is marked as an audio bus; bus routing is not simulated.");

        var actionKind = (byte)(actionType >> 8);
        if (actionKind == 4) // Play action
        {
            if (!cursor.TryReadByte(out _)) return false; // fade-curve bit vector
            if (!cursor.TryReadUInt32(out var bankId)) return false;
            item.TargetBankId = bankId == 0 ? null : bankId;
        }
        else if (actionKind == 18) // SetState
        {
            if (!cursor.TryReadUInt32(out var groupId) || !cursor.TryReadUInt32(out var stateId)) return false;
            item.StateGroupId = groupId;
            item.StateValueId = stateId;
        }
        else if (actionKind == 25) // SetSwitch in Wwise 118/120 action table
        {
            if (!cursor.TryReadUInt32(out var groupId) || !cursor.TryReadUInt32(out var switchId)) return false;
            item.SwitchGroupId = groupId;
            item.SwitchGroupKind = GameSyncKind.Switch;
            item.DefaultSwitchId = switchId;
        }
        else
        {
            item.Limitations.Add($"Action kind {actionKind} has target and properties decoded; action-specific effects are not interpreted.");
        }
        return true;
    }

    private static bool TryReadPropertyBundle(ref Cursor cursor, BankObject item, bool ranged)
    {
        if (!cursor.TryReadByte(out var count) || count > 128 || !cursor.CanRead(count)) return false;
        Span<byte> ids = stackalloc byte[count];
        for (var index = 0; index < count; index++)
            if (!cursor.TryReadByte(out ids[index])) return false;
        for (var index = 0; index < count; index++)
        {
            if (!cursor.TryReadUInt32(out var rawValue)) return false;
            if (ranged && !cursor.TryReadUInt32(out _)) return false;
            var property = ids[index];
            if (property is 0 or 2)
            {
                var value = BitConverter.Int32BitsToSingle(unchecked((int)rawValue));
                if (!float.IsFinite(value)) continue;
                if (property == 0) item.VolumeDb = value;
                else item.PitchCents = value;
            }
        }
        if (ranged && count > 0) item.Limitations.Add("Wwise property ranges/modifiers were parsed structurally but are not simulated.");
        return true;
    }

    private static bool ParseRandomSequence(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadNodeBase(item, ref cursor, version, hasFeedback, out _)) return false;
        if (!cursor.TryReadUInt16(out var loops) || !cursor.TryReadUInt16(out _) || !cursor.TryReadUInt16(out _) ||
            !cursor.TryReadSingle(out _) || !cursor.TryReadSingle(out _) || !cursor.TryReadSingle(out _) ||
            !cursor.TryReadUInt16(out _) || !cursor.TryReadByte(out _) || !cursor.TryReadByte(out _) ||
            !cursor.TryReadByte(out var mode) || !cursor.TryReadByte(out _)) return false;
        if (mode > 1) return false;
        item.LoopCount = loops;
        item.Sequential = mode == 1;
        if (!TryReadChildren(ref cursor, item.Children)) return false;
        if (!cursor.TryReadUInt16(out var playlistCount) || playlistCount > MaxListItems || !cursor.CanRead((long)playlistCount * 8)) return false;
        var playlist = new List<uint>(playlistCount);
        for (var index = 0; index < playlistCount; index++)
        {
            if (!cursor.TryReadUInt32(out var childId) || !cursor.TryReadInt32(out _)) return false;
            if (childId == 0) return false;
            playlist.Add(childId);
        }
        if (playlist.Count > 0)
        {
            if (item.Children.Count > 0 && !item.Children.SequenceEqual(playlist))
                item.Limitations.Add("Random/sequence playlist order differs from the parent child table; the typed playlist order is used.");
            item.Children.Clear();
            item.Children.AddRange(playlist);
        }
        return true;
    }

    private static bool ParseSwitchContainer(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadNodeBase(item, ref cursor, version, hasFeedback, out _)) return false;
        if (!cursor.TryReadByte(out var groupType) || groupType > 1 || !cursor.TryReadUInt32(out var groupId) ||
            !cursor.TryReadUInt32(out var defaultValue) || !cursor.TryReadByte(out _)) return false;
        item.SwitchGroupId = groupId;
        item.SwitchGroupKind = groupType == 1 ? GameSyncKind.State : GameSyncKind.Switch;
        item.DefaultSwitchId = defaultValue == 0 ? null : defaultValue;
        if (!TryReadChildren(ref cursor, item.Children)) return false;
        if (!cursor.TryReadUInt32(out var groupCount) || groupCount > MaxListItems) return false;
        var branches = new List<SwitchBranch>();
        for (var index = 0; index < groupCount; index++)
        {
            if (!cursor.TryReadUInt32(out var valueId) || !cursor.TryReadUInt32(out var count) || count > MaxListItems ||
                !cursor.CanRead((long)count * 4)) return false;
            var targets = new List<uint>((int)count);
            for (var child = 0; child < count; child++)
            {
                if (!cursor.TryReadUInt32(out var targetId) || targetId == 0) return false;
                targets.Add(targetId);
            }
            branches.Add(new SwitchBranch(valueId, targets));
        }
        item.Branches.AddRange(branches);
        if (!cursor.TryReadUInt32(out var parameterCount) || parameterCount > MaxListItems) return false;
        for (var index = 0; index < parameterCount; index++)
            if (!cursor.TrySkip(13)) return false; // node ID, two flag bytes (v118/120), fade-out and fade-in milliseconds
        return true;
    }

    private static bool ParseActorMixer(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        return TryReadNodeBase(item, ref cursor, version, hasFeedback, out _) && TryReadChildren(ref cursor, item.Children);
    }

    private static bool ParseLayerContainer(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadNodeBase(item, ref cursor, version, hasFeedback, out _) || !TryReadChildren(ref cursor, item.Children) ||
            !cursor.TryReadUInt32(out var layerCount) || layerCount > MaxListItems) return false;
        var associatedChildren = new HashSet<uint>();
        for (var layer = 0; layer < layerCount; layer++)
        {
            if (!cursor.TryReadUInt32(out _)) return false;
            if (!TrySkipRtpcSet(ref cursor, version) || !cursor.TryReadUInt32(out _) || !cursor.TryReadByte(out var rtpcType)) return false;
            if (rtpcType > 0) item.Limitations.Add("Layer-container crossfade RTPC parameters were parsed but are not simulated.");
            if (!cursor.TryReadUInt32(out var associationCount) || associationCount > MaxListItems) return false;
            for (var association = 0; association < associationCount; association++)
            {
                if (!cursor.TryReadUInt32(out var childId) || !cursor.TryReadUInt32(out var curveCount) || curveCount > MaxListItems ||
                    !cursor.TrySkip(checked((int)curveCount * 12))) return false;
                if (childId != 0) associatedChildren.Add(childId);
            }
        }
        if (version > 118 && !cursor.TryReadByte(out _)) return false;
        foreach (var childId in associatedChildren)
            if (!item.Children.Contains(childId)) item.Children.Add(childId);
        return true;
    }

    private static bool ParseMusicSegment(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadMusicNodeParams(item, ref cursor, version, hasFeedback)) return false;
        if (!cursor.TryReadDouble(out _) || !cursor.TryReadUInt32(out var markerCount) || markerCount > MaxListItems) return false;
        var markers = new List<MusicMarker>((int)markerCount);
        for (var marker = 0; marker < markerCount; marker++)
        {
            var sourceOffset = item.BodySlice?.Offset + cursor.Position ?? cursor.Position;
            if (!cursor.TryReadUInt32(out var id) || !cursor.TryReadDouble(out var positionMilliseconds) ||
                !double.IsFinite(positionMilliseconds) || !TryReadUtf8String(ref cursor, version, out var text)) return false;
            markers.Add(new MusicMarker(id, positionMilliseconds / 1000d, text ?? string.Empty, sourceOffset));
        }
        item.Markers.AddRange(markers);
        return true;
    }

    private static bool ParseMusicTrack(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!cursor.TryReadByte(out _)) return false; // MIDI override flags, v90-152
        if (!cursor.TryReadUInt32(out var sourceCount) || sourceCount > MaxListItems) return false;
        var sources = new List<MediaReference>();
        for (var sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            if (!TryReadSource(ref cursor, version, out var source)) return false;
            sources.Add(source);
        }
        item.Sources.AddRange(sources);
        if (!cursor.TryReadUInt32(out var clipCount) || clipCount > MaxListItems) return false;
        var clips = new List<MusicClip>();
        for (var clipIndex = 0; clipIndex < clipCount; clipIndex++)
        {
            if (!cursor.TryReadUInt32(out var trackId) || !cursor.TryReadUInt32(out var mediaId) ||
                !cursor.TryReadDouble(out var playAt) || !cursor.TryReadDouble(out var beginTrim) ||
                !cursor.TryReadDouble(out var endTrim) || !cursor.TryReadDouble(out var sourceDuration)) return false;
            if (!double.IsFinite(playAt) || !double.IsFinite(beginTrim) || !double.IsFinite(endTrim) ||
                !double.IsFinite(sourceDuration) || sourceDuration < 0) return false;
            if (mediaId != 0)
                clips.Add(new MusicClip(mediaId, trackId, playAt / 1000, beginTrim / 1000, endTrim / 1000, sourceDuration / 1000));
        }
        if (clipCount > 0 && !cursor.TryReadUInt32(out _)) return false; // number of subtracks
        item.Clips.AddRange(clips);
        // Source declarations describe stream storage; clips describe selected playback. Keep both.
        if (!cursor.TryReadUInt32(out var automationCount) || automationCount > MaxListItems) return false;
        for (var automation = 0; automation < automationCount; automation++)
        {
            if (!cursor.TryReadUInt32(out _) || !cursor.TryReadUInt32(out _) || !cursor.TryReadUInt32(out var pointCount) ||
                pointCount > MaxListItems || !cursor.TrySkip(checked((int)pointCount * 12))) return false;
        }
        if (!TryReadNodeBase(item, ref cursor, version, hasFeedback, out _)) return false;
        if (!cursor.TryReadByte(out var trackType) || trackType > 3) return false;
        if (trackType == 2) item.Sequential = true;
        if (trackType == 3)
        {
            if (!cursor.TryReadByte(out var groupType) || groupType > 1 || !cursor.TryReadUInt32(out var groupId) || !cursor.TryReadUInt32(out var defaultValue) ||
                !cursor.TryReadUInt32(out var associations) || associations > MaxListItems) return false;
            item.SwitchGroupId = groupId;
            item.SwitchGroupKind = groupType == 1 ? GameSyncKind.State : GameSyncKind.Switch;
            item.DefaultSwitchId = defaultValue == 0 ? null : defaultValue;
            for (var index = 0; index < associations; index++)
                if (!cursor.TryReadUInt32(out _)) return false;
            if (!SkipTrackTransition(ref cursor)) return false;
            item.Limitations.Add("Music-track switch associations and transition timing were decoded structurally but are not simulated.");
        }
        if (!cursor.TryReadInt32(out _)) return false; // look-ahead milliseconds
        if (item.Clips.Count == 0 && item.Sources.Count == 0)
            item.Limitations.Add("Music track contains no decoded clip source IDs.");
        return true;
    }

    private static bool ParseMusicSwitchContainer(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadMusicTransitionNode(item, ref cursor, version, hasFeedback)) return false;
        if (!cursor.TryReadByte(out _) || !cursor.TryReadUInt32(out var depth) || depth > MaxTreeDepth) return false;
        var groupIds = new uint[depth];
        for (var index = 0; index < depth; index++)
            if (!cursor.TryReadUInt32(out groupIds[index])) return false;
        var groupKinds = new GameSyncKind[depth];
        for (var index = 0; index < depth; index++)
        {
            if (!cursor.TryReadByte(out var groupType) || groupType > 1) return false;
            groupKinds[index] = groupType == 1 ? GameSyncKind.State : GameSyncKind.Switch;
        }
        if (!cursor.TryReadUInt32(out var treeSize) || treeSize > MaxListItems * 12 || !cursor.TryReadByte(out _)) return false;
        var treeData = cursor.ReadSlice((int)treeSize);
        if (treeData.IsEmpty && treeSize != 0) return false;
        if (treeSize % 12 != 0) return false;
        var leaves = new List<DecisionPath>();
        if (treeSize > 0 && !ParseDecisionTree(treeData, groupIds, groupKinds, leaves)) return false;
        item.DecisionPaths.AddRange(leaves);
        if (depth == 1 && groupIds.Length == 1)
        {
            item.SwitchGroupId = groupIds[0];
            item.SwitchGroupKind = groupKinds[0];
            foreach (var branch in leaves.GroupBy(leaf => leaf.Values[0].ValueId))
            {
                var targets = branch.Select(leaf => leaf.TargetId).Where(id => id != 0).Distinct().ToList();
                item.Branches.Add(new SwitchBranch(branch.Key, targets));
            }
        }
        foreach (var leaf in leaves)
            if (leaf.TargetId != 0 && !item.Children.Contains(leaf.TargetId)) item.Children.Add(leaf.TargetId);
        item.Limitations.Add("Music-switch transition rules and multi-group decision-tree combinations are not fully simulated.");
        if (depth > 1) item.Support = PlanSupport.Approximate;
        return true;
    }

    private static bool ParseMusicPlaylist(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadMusicTransitionNode(item, ref cursor, version, hasFeedback) || !cursor.TryReadUInt32(out var rootCount) || rootCount > MaxListItems) return false;
        // CAkMusicRanSeqCntr serializes one root playlist node; numPlaylistItems is retained as a
        // structural count, while nested nodes carry the actual segment edges recursively.
        var playlistChildren = new List<uint>();
        if (!TryReadPlaylistNode(ref cursor, playlistChildren, version, depth: 0, out var mode, out var loopCount)) return false;
        item.OwnedChildren.AddRange(item.Children);
        item.Children.Clear();
        foreach (var childId in playlistChildren)
            if (!item.Children.Contains(childId)) item.Children.Add(childId);
        if (rootCount == 0) item.Limitations.Add("Music-playlist reports zero root items but contains the serialized root node.");
        item.Sequential = mode is 0 or 1;
        item.LoopCount = loopCount;
        item.Limitations.Add("Music-playlist transitions, weights and avoid-repeat behavior are not simulated.");
        return true;
    }

    private static bool TryReadMusicNodeParams(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (version > 89 && !cursor.TryReadByte(out _)) return false;
        if (!TryReadNodeBase(item, ref cursor, version, hasFeedback, out _)) return false;
        if (!TryReadChildren(ref cursor, item.Children)) return false;
        if (!cursor.TryReadDouble(out _) || !cursor.TryReadDouble(out _) || !cursor.TryReadSingle(out _) ||
            !cursor.TryReadByte(out _) || !cursor.TryReadByte(out _) || !cursor.TryReadByte(out _) ||
            !cursor.TryReadUInt32(out var stingerCount) || stingerCount > MaxListItems) return false;
        for (var index = 0; index < stingerCount; index++)
            if (!cursor.TrySkip(24)) return false;
        return true;
    }

    private static bool TryReadMusicTransitionNode(BankObject item, ref Cursor cursor, uint version, bool hasFeedback)
    {
        if (!TryReadMusicNodeParams(item, ref cursor, version, hasFeedback) || !cursor.TryReadUInt32(out var ruleCount) || ruleCount > MaxListItems) return false;
        for (var rule = 0; rule < ruleCount; rule++)
        {
            if (!cursor.TryReadUInt32(out var sourceCount) || sourceCount > MaxListItems || !cursor.TrySkip(checked((int)sourceCount * 4)) ||
                !cursor.TryReadUInt32(out var destinationCount) || destinationCount > MaxListItems || !cursor.TrySkip(checked((int)destinationCount * 4))) return false;
            if (!cursor.TrySkip(21) || !cursor.TrySkip(24)) return false; // source and destination transition records, v118/120
            if (!cursor.TryReadByte(out var allocTransition) || allocTransition > 1) return false;
            if (allocTransition != 0 && !cursor.TrySkip(4 + 12 + 12 + 2)) return false;
        }
        return true;
    }

    private static bool TryReadNodeBase(BankObject item, ref Cursor cursor, uint version, bool hasFeedback, out uint parentId)
    {
        parentId = 0;
        if (!TryReadFxSlots(ref cursor, version) || !cursor.TryReadByte(out _) || // attachment-param override (90-145)
            !cursor.TryReadUInt32(out _) || !cursor.TryReadUInt32(out parentId) || !cursor.TryReadByte(out _)) return false;
        item.ParentId = parentId == 0 ? null : parentId;
        if (!TryReadPropertyBundle(ref cursor, item, ranged: false) || !TryReadPropertyBundle(ref cursor, item, ranged: true) ||
            !TryReadPositioning(ref cursor, version) || !TryReadAux(ref cursor, version) || !TryReadAdvSettings(ref cursor)) return false;
        if (!TryReadStateChunk(ref cursor) || !TryReadRtpcs(ref cursor, version)) return false;
        if (hasFeedback && !cursor.TryReadUInt32(out _)) return false; // feedback bus ID in v118/120
        return true;
    }

    private static bool TryReadFxSlots(ref Cursor cursor, uint version)
    {
        if (!cursor.TryReadByte(out _) || !cursor.TryReadByte(out var count) || count > 64) return false;
        if (count > 0)
        {
            if (!cursor.TrySkip(1)) return false; // bypass bit vector
            for (var index = 0; index < count; index++)
            {
                if (!cursor.TryReadByte(out _) || !cursor.TryReadUInt32(out _) || !cursor.TrySkip(2)) return false;
            }
        }
        return true;
    }

    private static bool TryReadPositioning(ref Cursor cursor, uint version)
    {
        if (!cursor.TryReadByte(out var positioningBits)) return false;
        var hasPositioning = (positioningBits & 1) != 0;
        var has3d = hasPositioning && (positioningBits & 0x08) != 0; // v118/120 bit vector
        if (!has3d) return true;
        if (!cursor.TryReadByte(out var bits3d) || !cursor.TryReadUInt32(out _)) return false; // 3D flags, attenuation ID
        var positionType = bits3d & 0x03;
        var hasAutomation = positionType != 1;
        if (!hasAutomation) return true;
        if (!cursor.TryReadByte(out _) || !cursor.TryReadInt32(out _) || !cursor.TryReadUInt32(out var vertexCount) || vertexCount > MaxListItems ||
            !cursor.TrySkip(checked((int)vertexCount * 16)) || !cursor.TryReadUInt32(out var playlistCount) || playlistCount > MaxListItems ||
            !cursor.TrySkip(checked((int)playlistCount * 8)) || !cursor.TrySkip(checked((int)playlistCount * 12))) return false;
        return true;
    }

    private static bool TryReadAux(ref Cursor cursor, uint version)
    {
        if (!cursor.TryReadByte(out var flags)) return false;
        return (flags & 0x08) == 0 || cursor.TrySkip(16); // four auxiliary bus IDs; v118/120 has no reflection ID
    }

    private static bool TryReadAdvSettings(ref Cursor cursor)
    {
        return cursor.TrySkip(6); // virtual-queue flags/type, max instances, below-threshold mode and HDR flags
    }

    private static bool TryReadStateChunk(ref Cursor cursor)
    {
        if (!cursor.TryReadUInt32(out var groupCount) || groupCount > MaxListItems) return false;
        for (var group = 0; group < groupCount; group++)
        {
            if (!cursor.TryReadUInt32(out _) || !cursor.TryReadByte(out _) || !cursor.TryReadUInt16(out var stateCount) ||
                stateCount > MaxListItems || !cursor.TrySkip(checked((int)stateCount * 8))) return false;
        }
        return true;
    }

    private static bool TryReadRtpcs(ref Cursor cursor, uint version)
    {
        if (!cursor.TryReadUInt16(out var rtpcCount) || rtpcCount > MaxListItems) return false;
        for (var rtpc = 0; rtpc < rtpcCount; rtpc++)
        {
            if (!cursor.TrySkip(4 + 1 + 1) || !cursor.TryReadVarUInt(out _) || !cursor.TrySkip(4 + 1) ||
                !cursor.TryReadUInt16(out var pointCount) || pointCount > MaxListItems ||
                !cursor.TrySkip(checked((int)pointCount * 12))) return false;
        }
        return true;
    }

    private static bool TrySkipRtpcSet(ref Cursor cursor, uint version)
    {
        if (!TryReadRtpcs(ref cursor, version)) return false;
        return cursor.TryReadUInt32(out _) && cursor.TryReadByte(out _) && cursor.TryReadUInt32(out var associationCount) &&
            associationCount <= MaxListItems && TrySkipLayerAssociations(ref cursor, (int)associationCount);
    }

    private static bool TrySkipLayerAssociations(ref Cursor cursor, int associationCount)
    {
        for (var index = 0; index < associationCount; index++)
        {
            if (!cursor.TrySkip(4) || !cursor.TryReadUInt32(out var points) || points > MaxListItems ||
                !cursor.TrySkip(checked((int)points * 12))) return false;
        }
        return true;
    }

    private static bool TryReadChildren(ref Cursor cursor, List<uint> children)
    {
        if (!cursor.TryReadUInt32(out var count) || count > MaxListItems || !cursor.CanRead((long)count * 4)) return false;
        var seen = new HashSet<uint>();
        var parsed = new List<uint>((int)count);
        for (var index = 0; index < count; index++)
        {
            if (!cursor.TryReadUInt32(out var childId) || childId == 0 || !seen.Add(childId)) return false;
            parsed.Add(childId);
        }
        children.AddRange(parsed);
        return true;
    }

    private static bool SkipTrackTransition(ref Cursor cursor)
    {
        return cursor.TrySkip(12 + 4 + 4 + 12); // source fade, sync/cue, destination fade
    }

    private static bool TryReadPlaylistNode(ref Cursor cursor, List<uint> children, uint version, int depth, out uint mode, out int loopCount)
    {
        mode = 0;
        loopCount = 1;
        if (depth > MaxTreeDepth || !cursor.TryReadUInt32(out var segmentId) || !cursor.TryReadUInt32(out _) ||
            !cursor.TryReadUInt32(out var childCount) || childCount > MaxListItems) return false;
        if (!cursor.TryReadUInt32(out mode)) return false; // RSType
        if ((mode > 3 && !(mode == uint.MaxValue && childCount == 0)) || !cursor.TryReadInt16(out var loops)) return false;
        if (version > 89 && (!cursor.TryReadInt16(out _) || !cursor.TryReadInt16(out _))) return false;
        if (!cursor.TryReadUInt32(out _) || !cursor.TryReadUInt16(out _) || !cursor.TryReadByte(out _) || !cursor.TryReadByte(out _)) return false;
        if (depth == 0) loopCount = loops <= 0 ? 0 : loops;
        if (segmentId != 0 && childCount == 0 && !children.Contains(segmentId)) children.Add(segmentId);
        for (var child = 0; child < childCount; child++)
            if (!TryReadPlaylistNode(ref cursor, children, version, depth + 1, out _, out _)) return false;
        return true;
    }

    private static bool ParseDecisionTree(ReadOnlySpan<byte> bytes, uint[] groupIds, GameSyncKind[] groupKinds,
        List<DecisionPath> leaves)
    {
        if (groupIds.Length > MaxTreeDepth || bytes.IsEmpty || bytes.Length % 12 != 0) return false;
        // Every v118/120 node contains key, target-or-packed-child-range, weight and probability.
        // The final two words are never another copy of the child range.
        var nodes = new TreeNode[bytes.Length / 12];
        var cursor = new Cursor(bytes);
        for (var index = 0; index < nodes.Length; index++)
        {
            if (!cursor.TryReadUInt32(out var key) || !cursor.TryReadUInt32(out var data) ||
                !cursor.TryReadUInt16(out var weight) || !cursor.TryReadUInt16(out var probability)) return false;
            nodes[index] = new TreeNode(key, data, weight, probability);
        }
        var visited = new HashSet<int>();
        var parsedLeaves = new List<DecisionPath>();
        if (!VisitDecisionNode(nodes, 0, 0, groupIds, groupKinds, [], visited, parsedLeaves) || visited.Count != nodes.Length)
            return false;
        leaves.AddRange(parsedLeaves);
        return true;
    }

    private static bool VisitDecisionNode(TreeNode[] nodes, int index, int depth, uint[] groupIds,
        GameSyncKind[] groupKinds, List<GameSyncValue> path, HashSet<int> visited, List<DecisionPath> leaves)
    {
        if (index < 0 || index >= nodes.Length || depth > groupIds.Length || !visited.Add(index)) return false;
        var node = nodes[index];
        var values = new List<GameSyncValue>(path);
        if (depth > 0) values.Add(new GameSyncValue(groupKinds[depth - 1], groupIds[depth - 1], node.Key));
        var childIndex = (int)(node.Data & 0xFFFF);
        var childCount = (int)(node.Data >> 16);
        // wwiser also recognizes early leaves when the packed value cannot represent a child range.
        var earlyLeaf = childIndex > nodes.Length || childCount > nodes.Length ||
            (long)index * 12 + (long)childCount * 12 > (long)nodes.Length * 12;
        if (depth == groupIds.Length || earlyLeaf)
        {
            // Target zero is a real silent/default branch and must retain its decision path.
            leaves.Add(new DecisionPath(values, node.Data, node.Weight, node.Probability));
            return true;
        }
        if (childIndex + (long)childCount > nodes.Length || (childCount > 0 && childIndex <= index)) return false;
        for (var child = 0; child < childCount; child++)
            if (!VisitDecisionNode(nodes, childIndex + child, depth + 1, groupIds, groupKinds, values, visited, leaves)) return false;
        return true;
    }

    private static bool TryReadUtf8String(ref Cursor cursor, uint version, out string? value)
    {
        value = null;
        if (version <= 136)
        {
            if (!cursor.TryReadUInt32(out var size) || size > 64 * 1024 || !cursor.TryReadBytes((int)size, out var bytes)) return false;
            value = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        if (!cursor.TryReadNullTerminatedUtf8(out value, 64 * 1024)) return false;
        return true;
    }

    private static void Unsupported(BankObject item, string reason)
    {
        item.Support = PlanSupport.Unsupported;
        item.Limitations.Add(reason);
    }

    private sealed record TreeNode(uint Key, uint Data, ushort Weight, ushort Probability);

    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        public readonly int Position => _position;
        public readonly int Remaining => _data.Length - _position;
        public readonly bool CanRead(long length) => length >= 0 && length <= Remaining;
        public ReadOnlySpan<byte> ReadSlice(int length)
        {
            if (!CanRead(length)) return ReadOnlySpan<byte>.Empty;
            var result = _data.Slice(_position, length);
            _position += length;
            return result;
        }
        public bool TrySkip(int length)
        {
            if (!CanRead(length)) return false;
            _position += length;
            return true;
        }
        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (!CanRead(1)) return false;
            value = _data[_position++];
            return true;
        }
        public bool TryReadUInt16(out ushort value)
        {
            value = 0;
            if (!CanRead(2)) return false;
            value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, 2));
            _position += 2;
            return true;
        }
        public bool TryReadInt16(out short value)
        {
            value = 0;
            if (!CanRead(2)) return false;
            value = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_position, 2));
            _position += 2;
            return true;
        }
        public bool TryReadUInt32(out uint value)
        {
            value = 0;
            if (!CanRead(4)) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
            _position += 4;
            return true;
        }
        public bool TryReadInt32(out int value)
        {
            value = 0;
            if (!CanRead(4)) return false;
            value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position, 4));
            _position += 4;
            return true;
        }
        public bool TryReadSingle(out float value)
        {
            value = 0;
            if (!TryReadInt32(out var raw)) return false;
            value = BitConverter.Int32BitsToSingle(raw);
            return true;
        }
        public bool TryReadDouble(out double value)
        {
            value = 0;
            if (!CanRead(8)) return false;
            value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_position, 8)));
            _position += 8;
            return true;
        }
        public bool TryReadVarUInt(out uint value)
        {
            value = 0;
            var shift = 0;
            for (var index = 0; index < 5; index++)
            {
                if (!TryReadByte(out var current)) return false;
                value |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }
        public bool TryReadBytes(int length, out ReadOnlySpan<byte> bytes)
        {
            bytes = ReadOnlySpan<byte>.Empty;
            if (!CanRead(length)) return false;
            bytes = _data.Slice(_position, length);
            _position += length;
            return true;
        }
        public bool TryReadNullTerminatedUtf8(out string? value, int maximumBytes)
        {
            value = null;
            var scan = _position;
            var limit = Math.Min(_data.Length, _position + maximumBytes);
            while (scan < limit && _data[scan] != 0) scan++;
            if (scan >= limit || scan >= _data.Length) return false;
            value = System.Text.Encoding.UTF8.GetString(_data.Slice(_position, scan - _position));
            _position = scan + 1;
            return true;
        }
    }
}
