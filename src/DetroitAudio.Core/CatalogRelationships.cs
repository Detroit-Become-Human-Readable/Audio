namespace DetroitAudio.Core;

public static class CatalogRelationships
{
    public static void Build(AudioCatalog catalog, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var graph = new CatalogGraph(catalog);
        var banks = catalog.Banks.ToDictionary(bank => bank.Key);
        var media = catalog.Media.Where(entry => entry.BankKey.Length > 0).GroupBy(entry => (entry.BankKey, entry.Id))
            .ToDictionary(group => group.Key, group =>
            {
                var complete = group.Where(MediaPolicy.IsComplete).ToArray();
                return complete.Length > 0 ? complete : group.ToArray();
            });
        var eventKeys = catalog.Events.Where(entry => !entry.IsDialogue).Select(entry => entry.Key).ToHashSet();
        var dialogueKeys = catalog.Events.Where(entry => entry.IsDialogue).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var linkedDialogue = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in catalog.Media)
        {
            token.ThrowIfCancellationRequested();
            foreach (var key in item.EventKeys.Where(dialogueKeys.Contains))
            {
                if (!linkedDialogue.TryGetValue(key, out var keys)) linkedDialogue[key] = keys = new(StringComparer.Ordinal);
                keys.Add(item.Key);
            }
        }
        foreach (var entry in catalog.Media) entry.EventKeys.RemoveAll(eventKeys.Contains);
        var playback = new List<(AudioEvent Event, GraphObject Action, GraphTraversal Traversal)>();
        foreach (var entry in catalog.Events)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDialogue)
            {
                entry.MediaKeys = linkedDialogue.TryGetValue(entry.Key, out var keys) ? keys.Order(StringComparer.Ordinal).ToList() : [];
                entry.MediaLinks = entry.MediaKeys.Select(key => new EventMediaLink(key, EventLinkKind.Direct)).ToList();
                entry.Behavior = EventBehavior.Playback;
                entry.LinkStatus = entry.MediaKeys.Count > 0 ? EventLinkStatus.Resolved : EventLinkStatus.Empty;
                continue;
            }
            entry.MediaKeys.Clear(); entry.MediaLinks.Clear(); entry.StateValues.Clear(); entry.RelatedPlayEventKeys.Clear(); entry.Diagnostics.Clear();
            if (!banks.TryGetValue(entry.BankKey, out var bank) || !bank.Objects.TryGetValue(entry.Id, out var eventObject))
            { entry.LinkStatus = EventLinkStatus.Missing; continue; }
            var actions = new List<GraphObject>();
            foreach (var id in eventObject.Actions)
            {
                var resolved = graph.Resolve(bank, id, 3);
                if (resolved.Unique is { } action) actions.Add(action);
                else entry.Diagnostics.Add(new(resolved.Ambiguous ? "AmbiguousAction" : "MissingAction", $"Action {id} could not be resolved.", entry.Key));
            }
            var play = actions.Where(item => (item.Object.ActionType >> 8) == 4).ToArray();
            foreach (var action in actions)
            {
                var item = action.Object;
                if ((item.ActionType >> 8) == 18 && item.StateGroupId is { } group && item.StateValueId is { } state)
                    entry.StateValues.Add(new(GameSyncKind.State, group, state));
                else if ((item.ActionType >> 8) == 25 && item.SwitchGroupId is { } switchGroup && item.DefaultSwitchId is { } value)
                    entry.StateValues.Add(new(GameSyncKind.Switch, switchGroup, value));
            }
            entry.Behavior = play.Length > 0 ? actions.Count > play.Length ? EventBehavior.Mixed : EventBehavior.Playback
                : entry.StateValues.Count > 0 ? EventBehavior.StateChange : actions.Count > 0 ? EventBehavior.Control : EventBehavior.Unknown;
            foreach (var action in play)
            {
                if (action.Object.TargetId is not { } target) { entry.Diagnostics.Add(new("MissingTarget", "The Play action has no target.", entry.Key)); continue; }
                var all = graph.Explore(action.Bank, target, bankId: action.Object.TargetBankId, token: token);
                playback.Add((entry, action, all));
                var selected = entry.StateValues.Count == 0 ? all : graph.Explore(action.Bank, target, entry.StateValues, action.Object.TargetBankId, token);
                AddLinks(entry, selected, EventLinkKind.Direct, null);
            }
            UpdateStatus(entry);
        }
        var byGroup = playback.SelectMany(play => play.Traversal.Groups.Select(group => (group, play)))
            .GroupBy(item => item.group).ToDictionary(group => group.Key, group => group.Select(item => item.play).ToArray());
        foreach (var entry in catalog.Events.Where(entry => entry.Behavior == EventBehavior.StateChange))
        {
            token.ThrowIfCancellationRequested();
            var candidates = entry.StateValues.SelectMany(value => byGroup.GetValueOrDefault((value.Kind, value.GroupId), []))
                .DistinctBy(item => (item.Event.Key, item.Action.Bank.Key, item.Action.Object.Id));
            foreach (var play in candidates)
            {
                entry.RelatedPlayEventKeys.Add(play.Event.Key);
                var traversal = graph.Explore(play.Action.Bank, play.Action.Object.TargetId!.Value, entry.StateValues, play.Action.Object.TargetBankId, token);
                AddLinks(entry, traversal, EventLinkKind.StateBranch, play.Event.Key);
            }
            entry.RelatedPlayEventKeys = entry.RelatedPlayEventKeys.Distinct().Order(StringComparer.Ordinal).ToList();
            UpdateStatus(entry);
        }
        foreach (var bank in catalog.Banks)
        foreach (var item in bank.Objects.Values.Where(item => item.Type == 11))
        {
            token.ThrowIfCancellationRequested();
            foreach (var id in item.Sources.Select(source => source.Id).Concat(item.Clips.Select(clip => clip.MediaId)))
                if (media.TryGetValue((bank.Key, id), out var entries)) foreach (var entry in entries) entry.Category = "Music";
        }
        foreach (var entry in catalog.Media)
        {
            if (banks.TryGetValue(entry.BankKey, out var bank)) entry.RawLanguageId = bank.LanguageId;
            if (entry.Category == "Unknown" && entry.BankKey.Length > 0) entry.Category = "Sound";
        }

        void AddLinks(AudioEvent entry, GraphTraversal traversal, EventLinkKind kind, string? playKey)
        {
            entry.Diagnostics.AddRange(traversal.Diagnostics.Select(diagnostic => diagnostic with { EntryKey = entry.Key }));
            foreach (var source in traversal.Sources)
            {
                if (!media.TryGetValue((source.BankKey, source.MediaId), out var entries))
                { entry.Diagnostics.Add(new("MissingMedia", $"Media {source.MediaId} is absent from its bank context.", entry.Key)); continue; }
                foreach (var item in entries)
                {
                    if (!entry.MediaKeys.Contains(item.Key)) entry.MediaKeys.Add(item.Key);
                    if (!item.EventKeys.Contains(entry.Key)) item.EventKeys.Add(entry.Key);
                    var link = new EventMediaLink(item.Key, kind, playKey);
                    if (!entry.MediaLinks.Contains(link)) entry.MediaLinks.Add(link);
                    if (source.Music) item.Category = "Music";
                    foreach (var graphMarker in source.Markers)
                    {
                        var marker = graphMarker.Marker;
                        if (!item.Aliases.Contains(marker.Text, StringComparer.OrdinalIgnoreCase)) item.Aliases.Add(marker.Text);
                        var evidence = new NameEvidence(marker.Text, "MusicMarker", marker.Id, NameKind.Stored,
                            $"{graphMarker.BankKey}:segment:{graphMarker.SegmentId}:marker", marker.SourceOffset);
                        if (!item.Names.Contains(evidence)) item.Names.Add(evidence);
                    }
                }
            }
        }
        static void UpdateStatus(AudioEvent entry)
        {
            entry.LinkStatus = entry.Diagnostics.Any(item => item.Code.StartsWith("Ambiguous", StringComparison.Ordinal)) ? EventLinkStatus.Ambiguous
                : entry.Diagnostics.Count > 0 ? EventLinkStatus.Missing : entry.MediaKeys.Count > 0 ? EventLinkStatus.Resolved : EventLinkStatus.Empty;
        }
    }
}
