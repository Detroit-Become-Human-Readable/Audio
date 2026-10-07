using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DetroitAudio.Core;

namespace DetroitAudio.Audio;

/// <summary>Creates a deterministic, reviewable approximation of one Wwise event graph.</summary>
public static class EventPlanner
{
    private static readonly ConditionalWeakTable<AudioCatalog, CatalogLookup> LookupCache = new();
    private static readonly object LookupGate = new();
    private const byte Sound = 2, Action = 3, Event = 4, RandomSequence = 5, Switch = 6,
        ActorMixer = 7, LayerContainer = 9, MusicSegment = 10, MusicTrack = 11,
        MusicSwitch = 12, MusicRandomSequence = 13;

    public static PlaybackPlan Create(AudioCatalog catalog, AudioEvent audioEvent, EventOptions? options = null) => Schedule(Select(catalog, audioEvent, options));

    public static EventSelection Select(AudioCatalog catalog, AudioEvent audioEvent, EventOptions? options = null)
    {
        options ??= new EventOptions();
        var frozenOptions = options with
        {
            StateValues = options.StateValues is null ? null : new Dictionary<uint, uint>(options.StateValues),
            SwitchValues = options.SwitchValues is null ? null : new Dictionary<uint, uint>(options.SwitchValues)
        };
        var choices = new List<uint[]>();
        var provisional = CreateCore(catalog, audioEvent, frozenOptions, true, choices);
        var keys = provisional.Items.Select(item => item.MediaKey).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        return new EventSelection(catalog, audioEvent, frozenOptions, choices, catalog.Media.Where(media => keys.Contains(media.Key)).ToArray());
    }

    public static PlaybackPlan Schedule(EventSelection selection, IReadOnlyDictionary<string, AudioProbeResult>? measurements = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return CreateCore(selection.Catalog, selection.Event, selection.Options, false, selection.Choices, measurements);
    }

    private static PlaybackPlan CreateCore(AudioCatalog catalog, AudioEvent audioEvent, EventOptions? options,
        bool selecting, List<uint[]> choices, IReadOnlyDictionary<string, AudioProbeResult>? measurements = null)
    {
        var choiceIndex = 0;
        uint[] FreezeChoice(Func<uint[]> choose)
        {
            if (selecting) { var selected = choose(); choices.Add(selected); return selected; }
            if (choiceIndex >= choices.Count) throw new AudioToolException("The event graph changed during preparation.");
            return choices[choiceIndex++];
        }

        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(audioEvent);
        options ??= new EventOptions();
        if (options.DefaultLoopCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options), "Loop count must be between 1 and 100.");
        if (!double.IsFinite(options.FadeOutSeconds) || options.FadeOutSeconds < 0) throw new ArgumentOutOfRangeException(nameof(options), "Fade duration must be finite and non-negative.");
        if (options.MaximumDurationSeconds is { } max && (!double.IsFinite(max) || max <= 0)) throw new ArgumentOutOfRangeException(nameof(options), "Maximum duration must be finite and positive.");

        if (audioEvent.Behavior == EventBehavior.StateChange)
        {
            var playKey = options.RelatedPlayEventKey;
            if (playKey is null && audioEvent.RelatedPlayEventKeys.Count == 1) playKey = audioEvent.RelatedPlayEventKeys[0];
            if (playKey is null) throw new AudioToolException(audioEvent.RelatedPlayEventKeys.Count == 0 ? "No audio assigned to this state." : "Choose a playback event for this state.");
            if (!audioEvent.RelatedPlayEventKeys.Contains(playKey)) throw new AudioToolException("The selected playback event is not linked to this state.");
            var related = catalog.Events.SingleOrDefault(entry => entry.Key == playKey) ?? throw new AudioToolException("The linked playback event is missing.");
            var states = new Dictionary<uint, uint>(options.StateValues ?? new Dictionary<uint, uint>());
            var switches = new Dictionary<uint, uint>(options.SwitchValues ?? new Dictionary<uint, uint>());
            foreach (var value in audioEvent.StateValues) (value.Kind == GameSyncKind.State ? states : switches)[value.GroupId] = value.ValueId;
            var plan = CreateCore(catalog, related, options with { RelatedPlayEventKey = null, StateValues = states, SwitchValues = switches }, selecting, choices, measurements);
            return plan with { EventKey = audioEvent.Key, EventId = audioEvent.Id, EventName = audioEvent.DisplayName };
        }
        var lookup = GetLookup(catalog);
        var bank = lookup.BanksByKey.GetValueOrDefault(audioEvent.BankKey);
        if (bank is null && (!audioEvent.IsDialogue || audioEvent.MediaKeys.Count == 0))
            throw new AudioToolException($"Bank '{audioEvent.BankKey}' for event '{audioEvent.DisplayName}' is unavailable.");
        if (bank is null || !bank.Objects.TryGetValue(audioEvent.Id, out var eventNode) || eventNode.Type != Event)
        {
            if (!audioEvent.IsDialogue || audioEvent.MediaKeys.Count == 0)
                throw new AudioToolException($"Event '{audioEvent.DisplayName}' has no unambiguous event object in bank '{bank?.Name ?? audioEvent.BankKey}'.");
            var direct = audioEvent.MediaKeys.SelectMany(key => lookup.MediaByKey.GetValueOrDefault(key, []))
                .Where(media => string.Equals(media.Language, options.Language, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (direct.Length != 1) throw new AudioToolException($"Dialogue event '{audioEvent.DisplayName}' has {direct.Length} media entries for language '{options.Language}'.");
            if (direct[0].State is MediaState.MissingExternal or MediaState.Ambiguous or MediaState.Malformed or MediaState.Unsupported)
                throw new AudioToolException($"Media '{direct[0].DisplayName}' cannot be resolved ({direct[0].State}).");
            if (!options.AllowPartialMedia && !MediaPolicy.CanConvert(direct[0]))
                throw new AudioToolException($"Dialogue media '{direct[0].DisplayName}' has no verified complete stream for event export.");
            if (direct[0].Slice.Length <= 0 || string.IsNullOrWhiteSpace(direct[0].Slice.FilePath) || !File.Exists(direct[0].Slice.FilePath))
                throw new AudioToolException($"Dialogue media '{direct[0].DisplayName}' has no available data slice.");
            if (!MediaPolicy.CanPreview(direct[0])) throw new AudioToolException(MediaPolicy.GetUnavailableReason(direct[0]));
            var directMeasured = measurements?.GetValueOrDefault(direct[0].Key)?.Duration ?? direct[0].MeasuredDuration;
            var directDuration = direct[0].Completeness == MediaCompleteness.Fragment
                ? directMeasured
                : measurements?.GetValueOrDefault(direct[0].Key)?.Duration ?? direct[0].Duration;
            if (direct[0].Completeness == MediaCompleteness.Fragment && directMeasured is > 0 && directDuration is > 0)
                directDuration = Math.Min(directDuration.Value, directMeasured.Value);
            var scheduledDirectDuration = directDuration is > 0 ? directDuration.Value : 0;
            if (!selecting && options.MaximumDurationSeconds is { } directMaximum) scheduledDirectDuration = Math.Min(scheduledDirectDuration, directMaximum);
            var directItem = new PlaybackItem(direct[0].Key, direct[0].DisplayName, direct[0].Id, 0, scheduledDirectDuration, 1, 0, 0, [], SourceDurationSeconds: directDuration ?? 0);
            var directLimitations = new SortedSet<string>(StringComparer.Ordinal) { "Dialogue playback is resolved directly from selected-language media because no HIRC event object was decoded." };
            if (direct[0].State == MediaState.PrefetchOnly) directLimitations.Add("This event uses prefetch-only audio data; the preview is partial.");
            if (directDuration is not > 0) directLimitations.Add("Dialogue duration will be measured from decoded audio during render.");
            var directItems = new[] { directItem };
            return new PlaybackPlan(audioEvent.Key, audioEvent.Id, audioEvent.DisplayName, options.RandomSeed,
                directItems, directLimitations.ToArray(), directItem.DurationSeconds, BuildTxpt(directItems, bank ?? new BankInfo(), options, directLimitations), options.FadeOutSeconds, options.AllowPartialMedia, options.MaximumDurationSeconds);
        }
        if (eventNode.Actions.Count == 0) throw new AudioToolException($"Event '{audioEvent.DisplayName}' contains no playable actions.");

        var limitations = new SortedSet<string>(StringComparer.Ordinal);
        var items = new List<PlaybackItem>();
        var eventMedia = audioEvent.MediaKeys.ToHashSet(StringComparer.Ordinal);
        var actionSwitchValues = new Dictionary<uint, uint>();
        var actionStateValues = new Dictionary<uint, uint>();
        var random = new StableRandom(unchecked((uint)options.RandomSeed));
        var graphPath = new HashSet<(string BankKey, uint Id)>();

        foreach (var actionId in eventNode.Actions)
        {
            var actionMatch = lookup.Graph.Resolve(bank, actionId, Action).Unique
                ?? throw new AudioToolException($"Event action {actionId} is missing or ambiguous.");
            var action = actionMatch.Object;
            var actionKind = (byte)(action.ActionType >> 8);
            if (actionKind == 18 && action.StateGroupId is { } stateGroup && action.StateValueId is { } stateValue)
            {
                actionStateValues[stateGroup] = stateValue;
                continue;
            }
            if (actionKind == 25 && action.SwitchGroupId is { } setGroup && action.DefaultSwitchId is { } setValue)
            {
                actionSwitchValues[setGroup] = setValue;
                limitations.Add($"Event action {actionId} sets switch group {setGroup} to {setValue}.");
                continue;
            }
            if (!IsPlayAction(action.ActionType))
            {
                limitations.Add($"Action {actionId} type 0x{action.ActionType:X4} was skipped; only Play actions are rendered.");
                continue;
            }
            if (action.TargetId is not { } target)
                throw new AudioToolException($"Event action {actionId} has no resolved target object.");
            var targetMatch = lookup.Graph.Resolve(actionMatch.Bank, target, bankId: action.TargetBankId).Unique
                ?? throw new AudioToolException($"Action target {target} is missing or ambiguous in its bank context.");
            var actionDelay = Math.Max(0, action.DelaySeconds);
            Walk(target, targetMatch.Bank, actionDelay, action.VolumeDb, action.PitchCents, ApplyLoops(1, action.LoopCount, options.DefaultLoopCount), 0);
        }

        if (items.Count == 0) throw new AudioToolException($"Event '{audioEvent.DisplayName}' produced no playable media.");
        items.Sort((a, b) => a.StartSeconds != b.StartSeconds ? a.StartSeconds.CompareTo(b.StartSeconds) : a.MediaId.CompareTo(b.MediaId));
        var duration = items.Max(i => i.StartSeconds + i.DurationSeconds);
        if (!selecting && options.MaximumDurationSeconds is { } maximum && duration > maximum)
        {
            var limited = new List<PlaybackItem>();
            foreach (var item in items)
            {
                if (item.StartSeconds >= maximum) continue;
                limited.Add(item with { DurationSeconds = Math.Min(item.DurationSeconds, maximum - item.StartSeconds) });
            }
            items = limited;
            duration = maximum;
            limitations.Add($"Render is capped at {maximum.ToString("0.###", CultureInfo.InvariantCulture)} seconds.");
        }
        if (options.FadeOutSeconds > 0) limitations.Add($"A {options.FadeOutSeconds.ToString("0.###", CultureInfo.InvariantCulture)} second fade is applied at the render end.");

        var txpt = BuildTxpt(items, bank, options, limitations);
        limitations.UnionWith(items.SelectMany(i => i.Limitations));
        return new PlaybackPlan(audioEvent.Key, audioEvent.Id, audioEvent.DisplayName, options.RandomSeed,
            items.ToArray(), limitations.ToArray(), duration, txpt, options.FadeOutSeconds, options.AllowPartialMedia, options.MaximumDurationSeconds);

        void Walk(uint objectId, BankInfo preferredBank, double start, float gainDb, float pitchCents, int inheritedLoops, int depth)
        {
            if (depth > 64) throw new AudioToolException("The event graph exceeds the supported depth limit.");
            var (ownerBank, node) = ResolveObject(objectId, preferredBank);
            var identity = (ownerBank.Key, objectId);
            if (!graphPath.Add(identity)) throw new AudioToolException($"The event graph contains a cycle at object {objectId}.");
            try
            {
                if (node.Support == PlanSupport.Unsupported)
                    throw new AudioToolException($"Bank object {objectId} is marked unsupported by the parser.", string.Join(Environment.NewLine, node.Limitations));
                switch (node.Type)
                {
                    case Sound:
                        AddSources(node, ownerBank, start, gainDb + node.VolumeDb, pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount));
                        break;
                    case RandomSequence:
                    case MusicRandomSequence:
                        if (node.Children.Count == 0) throw new AudioToolException($"Container {objectId} has no children.");
                        if (node.Sequential)
                        {
                            var cursor = start;
                            foreach (var childId in node.Children)
                            {
                                var before = items.Count;
                                Walk(childId, ownerBank, cursor, gainDb + node.VolumeDb, pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount), depth + 1);
                                var produced = items.Skip(before).ToArray();
                                if (!selecting && produced.Any(item => item.DurationSeconds <= 0) && childId != node.Children[^1])
                                    throw new AudioToolException($"Sequential container {objectId} needs a decoded media duration before scheduling its next child.");
                                cursor = Math.Max(cursor, produced.Select(i => i.StartSeconds + i.DurationSeconds).DefaultIfEmpty(cursor).Max());
                            }
                        }
                        else
                        {
                            var selected = FreezeChoice(() => [node.Children[random.Next(node.Children.Count)]])[0];
                            limitations.Add($"Random container {objectId} was resolved with seed {options.RandomSeed}.");
                            Walk(selected, ownerBank, start, gainDb + node.VolumeDb, pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount), depth + 1);
                        }
                        break;
                    case Switch:
                    case MusicSwitch:
                        WalkSwitch(node, ownerBank, start, gainDb, pitchCents, inheritedLoops, depth);
                        break;
                    case MusicSegment:
                        if (node.Children.Count == 0) throw new AudioToolException($"Music segment {objectId} has no decoded tracks.");
                        foreach (var trackId in node.Children)
                            Walk(trackId, ownerBank, start + Math.Max(0, node.DelaySeconds), gainDb + node.VolumeDb,
                                pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount), depth + 1);
                        break;
                    case MusicTrack:
                        if (node.Clips.Count > 0)
                        {
                            foreach (var clip in node.Clips.OrderBy(c => c.PlayAt).ThenBy(c => c.TrackId))
                            {
                                var media = ResolveMedia(clip.MediaId, ownerBank);
                                var clipSpeed = Math.Pow(2, (pitchCents + node.PitchCents) / 1200d);
                                var clipStart = start + clip.PlayAt / clipSpeed;
                                var beginTrim = clip.BeginTrim + Math.Max(0, -clipStart) * clipSpeed;
                                AddMedia(media, Math.Max(0, clipStart), gainDb + node.VolumeDb, pitchCents + node.PitchCents,
                                    ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount), clip.Duration > 0 ? clip.Duration : null,
                                    beginTrim, clip.EndTrim, ownerBank);
                            }
                        }
                        else if (node.Sources.Count > 0)
                            AddSources(node, ownerBank, start, gainDb + node.VolumeDb, pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount));
                        else throw new AudioToolException($"Music object {objectId} has no resolved clips or children.");
                        break;
                    case ActorMixer:
                    case LayerContainer:
                        if (node.Children.Count == 0) throw new AudioToolException($"Layer container {objectId} has no children.");
                        foreach (var childId in node.Children)
                            Walk(childId, ownerBank, start, gainDb + node.VolumeDb, pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount), depth + 1);
                        break;
                    default:
                        if (node.Support == PlanSupport.Unsupported || node.Support == PlanSupport.Approximate)
                            throw new AudioToolException($"Bank object {objectId} (type {node.Type}) is not supported for event rendering.", string.Join(Environment.NewLine, node.Limitations));
                        if (node.Children.Count > 0) WalkChildren(node, ownerBank, start, gainDb, pitchCents, inheritedLoops, depth);
                        else if (node.Sources.Count > 0) AddSources(node, ownerBank, start, gainDb, pitchCents, inheritedLoops);
                        else throw new AudioToolException($"Bank object {objectId} (type {node.Type}) has no supported playback data.");
                        break;
                }
                if (node.Support != PlanSupport.Supported)
                {
                    var notes = node.Limitations.Count == 0 ? [$"Object {objectId} is marked {node.Support} by the parser."] : node.Limitations;
                    foreach (var note in notes) limitations.Add(note);
                }
            }
            finally { graphPath.Remove(identity); }
        }

        void WalkSwitch(BankObject node, BankInfo ownerBank, double start, float gainDb, float pitchCents, int inheritedLoops, int depth)
        {
            var groups = node.DecisionPaths.SelectMany(path => path.Values).Select(value => (value.Kind, value.GroupId)).Distinct().ToList();
            if (node.SwitchGroupId is { } singleGroup && !groups.Contains((node.SwitchGroupKind, singleGroup))) groups.Add((node.SwitchGroupKind, singleGroup));
            if (groups.Count == 0 && node.Branches.Count > 0) groups.Add((GameSyncKind.Switch, 0));
            if (groups.Count == 0) throw new AudioToolException($"Container {node.Id} has no decoded state or switch groups.");
            var values = new List<GameSyncValue>();
            foreach (var (kind, group) in groups)
            {
                uint? requested = null;
                var configured = kind == GameSyncKind.State ? options.StateValues ?? options.SwitchValues : options.SwitchValues;
                var actions = kind == GameSyncKind.State ? actionStateValues : actionSwitchValues;
                if (configured is not null && configured.TryGetValue(group, out var explicitValue)) requested = explicitValue;
                else if (actions.TryGetValue(group, out var actionValue)) requested = actionValue;
                requested ??= options.SwitchValueId ?? node.DefaultSwitchId;
                if (requested is null && node.DecisionPaths.Any(path => path.Values.Any(value => value.Kind == kind && value.GroupId == group && value.ValueId == 0))) requested = 0;
                if (requested is null) throw new AudioToolException(kind == GameSyncKind.Switch ? $"Switch container {node.Id} requires an explicit switch value because no default is decoded." : $"Choose a value for state group {group}.");
                values.Add(new(kind, group, requested.Value));
            }
            var children = FreezeChoice(() => CatalogGraph.SelectChildren(node, values).ToArray());
            if (children.Length == 0) throw new AudioToolException("No audio assigned to this state or switch.");
            foreach (var childId in children)
                Walk(childId, ownerBank, start + Math.Max(0, node.DelaySeconds), gainDb + node.VolumeDb, pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount), depth + 1);
        }

        void WalkChildren(BankObject node, BankInfo ownerBank, double start, float gainDb, float pitchCents, int inheritedLoops, int depth)
        {
            var cursor = start + Math.Max(0, node.DelaySeconds);
            foreach (var childId in node.Children)
            {
                var before = items.Count;
                Walk(childId, ownerBank, cursor, gainDb + node.VolumeDb, pitchCents + node.PitchCents, ApplyLoops(inheritedLoops, node.LoopCount, options.DefaultLoopCount), depth + 1);
                var produced = items.Skip(before).ToArray();
                if (!selecting && produced.Any(item => item.DurationSeconds <= 0) && childId != node.Children[^1])
                    throw new AudioToolException($"Sequential container {node.Id} needs a decoded media duration before scheduling its next child.");
                cursor = Math.Max(cursor, produced.Select(i => i.StartSeconds + i.DurationSeconds).DefaultIfEmpty(cursor).Max());
            }
        }

        void AddSources(BankObject node, BankInfo ownerBank, double start, float gainDb, float pitchCents, int loops)
        {
            if (node.Sources.Count == 0) throw new AudioToolException($"Sound object {node.Id} has no media source.");
            if (node.Sources.Count != 1) throw new AudioToolException($"Sound object {node.Id} has {node.Sources.Count} media sources and cannot be resolved unambiguously.");
            AddMedia(ResolveMedia(node.Sources[0].Id, ownerBank), start, gainDb, pitchCents, loops, null, 0, 0, ownerBank);
        }

        void AddMedia(MediaEntry media, double start, float gainDb, float pitchCents, int loops, double? clipDuration, double trimBegin, double trimEnd, BankInfo ownerBank)
        {
            if (eventMedia.Count > 0 && !eventMedia.Contains(media.Key))
                throw new AudioToolException($"Media {media.Id} is absent from the event's decoded media references.");
            if (media.State is MediaState.MissingExternal or MediaState.Ambiguous or MediaState.Malformed or MediaState.Unsupported)
                throw new AudioToolException($"Media '{media.DisplayName}' cannot be resolved ({media.State}).");
            if (!options.AllowPartialMedia && !MediaPolicy.CanConvert(media))
                throw new AudioToolException($"Media '{media.DisplayName}' has no verified complete stream for event export.");
            if (media.Completeness == MediaCompleteness.Fragment)
                limitations.Add($"Media '{media.DisplayName}' is prefetch-only; event playback contains partial audio data.");
            if (!MediaPolicy.CanPreview(media)) throw new AudioToolException(MediaPolicy.GetUnavailableReason(media));
            if (media.Slice.Length <= 0 || string.IsNullOrWhiteSpace(media.Slice.FilePath) || !File.Exists(media.Slice.FilePath))
                throw new AudioToolException($"Media '{media.DisplayName}' has no available data slice.");
            var measuredDuration = measurements?.GetValueOrDefault(media.Key)?.Duration ?? media.MeasuredDuration;
            var available = media.Completeness == MediaCompleteness.Fragment
                ? measuredDuration is > 0 ? Math.Min(clipDuration ?? measuredDuration.Value, measuredDuration.Value) : null
                : clipDuration ?? measurements?.GetValueOrDefault(media.Key)?.Duration ?? media.Duration;
            if (available is < 0 || (available is { } knownDuration && !double.IsFinite(knownDuration)))
                throw new AudioToolException($"Media '{media.DisplayName}' has invalid duration metadata.");
            // Wwise fEndTrimOffset is signed (normally <= 0); the encoded source duration
            // is trimmed by subtracting the beginning and adding the signed end offset.
            var cleanDuration = available is > 0
                ? Math.Max(0, available.Value - Math.Max(0, trimBegin) + Math.Min(0, trimEnd))
                : 0;
            if (available is > 0 && cleanDuration <= 0) throw new AudioToolException($"Media '{media.DisplayName}' is fully trimmed by its clip metadata.");
            var iterationCount = Math.Clamp(loops, 1, 100);
            var playbackSpeed = Math.Pow(2, pitchCents / 1200d);
            if (!double.IsFinite(playbackSpeed) || playbackSpeed <= 0) throw new AudioToolException("The event pitch is invalid.");
            var itemDuration = cleanDuration * iterationCount / playbackSpeed;
            if (itemDuration <= 0) limitations.Add($"Duration for '{media.DisplayName}' will be measured from decoded audio during render.");
            items.Add(new PlaybackItem(media.Key, media.DisplayName, media.Id, Math.Max(0, start), itemDuration, iterationCount,
                gainDb, pitchCents, [], Math.Max(0, trimBegin), trimEnd, cleanDuration, playbackSpeed));
        }

        MediaEntry ResolveMedia(uint id, BankInfo ownerBank)
        {
            var candidates = lookup.MediaByBankAndId.GetValueOrDefault((ownerBank.Key, id), []);
            if (!string.IsNullOrWhiteSpace(options.Language))
                candidates = candidates.Where(m => (m.Language == "SFX" || string.Equals(m.Language, options.Language, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (eventMedia.Count > 0) candidates = candidates.Where(m => eventMedia.Contains(m.Key)).ToArray();
            if (candidates.Length == 0)
            {
                candidates = string.IsNullOrWhiteSpace(options.Language)
                    ? lookup.MediaById.GetValueOrDefault(id, [])
                    : lookup.MediaByIdAndLanguage.GetValueOrDefault((id, options.Language.ToUpperInvariant()), []);
                if (eventMedia.Count > 0) candidates = candidates.Where(m => eventMedia.Contains(m.Key)).ToArray();
                if (candidates.Length == 0) throw new AudioToolException($"Media id {id} was not found for language '{options.Language}'.");
            }
            if (candidates.Length > 1) throw new AudioToolException($"Media id {id} resolves to multiple files for language '{options.Language}'.");
            return candidates[0];
        }

        (BankInfo Bank, BankObject Node) ResolveObject(uint id, BankInfo preferredBank)
        {
            var resolved = lookup.Graph.Resolve(preferredBank, id).Unique
                ?? throw new AudioToolException($"Bank object {id} is missing or cross-bank ambiguous.");
            return (resolved.Bank, resolved.Object);
        }
    }

    private static CatalogLookup GetLookup(AudioCatalog catalog)
    {
        lock (LookupGate)
        {
            if (LookupCache.TryGetValue(catalog, out var existing) && existing.Matches(catalog)) return existing;
            LookupCache.Remove(catalog);
            var rebuilt = new CatalogLookup(catalog);
            LookupCache.Add(catalog, rebuilt);
            return rebuilt;
        }
    }

    private sealed class CatalogLookup
    {
        private readonly int _mediaCount;
        private readonly int _bankCount;
        private readonly int _objectCount;
        public CatalogGraph Graph { get; }
        public Dictionary<string, MediaEntry[]> MediaByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string BankKey, uint Id), MediaEntry[]> MediaByBankAndId { get; } = [];
        public Dictionary<uint, MediaEntry[]> MediaById { get; } = [];
        public Dictionary<(uint Id, string Language), MediaEntry[]> MediaByIdAndLanguage { get; } = [];
        public Dictionary<string, BankInfo> BanksByKey { get; } = new(StringComparer.Ordinal);

        public CatalogLookup(AudioCatalog catalog)
        {
            Graph = new(catalog);
            _mediaCount = catalog.Media.Count;
            _bankCount = catalog.Banks.Count;
            _objectCount = catalog.Banks.Sum(bank => bank.Objects.Count);
            MediaByKey = catalog.Media.GroupBy(media => media.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var bank in catalog.Banks) BanksByKey.TryAdd(bank.Key, bank);
            MediaByBankAndId = catalog.Media.GroupBy(media => (media.BankKey, media.Id))
                .ToDictionary(group => group.Key, group => group.ToArray());
            MediaById = catalog.Media.GroupBy(media => media.Id).ToDictionary(group => group.Key, group => group.ToArray());
            MediaByIdAndLanguage = catalog.Media.GroupBy(media => (media.Id, media.Language.ToUpperInvariant()))
                .ToDictionary(group => group.Key, group => group.ToArray());
        }

        public bool Matches(AudioCatalog catalog) => _mediaCount == catalog.Media.Count &&
            _bankCount == catalog.Banks.Count && _objectCount == catalog.Banks.Sum(bank => bank.Objects.Count);
    }

    private static int ApplyLoops(int inherited, int requested, int defaultLoopCount) => requested switch
    {
        0 => Math.Clamp(defaultLoopCount, 1, 100),
        > 1 => Math.Clamp(requested, 1, 100),
        _ => inherited
    };

    private static bool IsPlayAction(ushort value) => value == 0x0004 || (value & 0xFF00) == 0x0400;

    private static string BuildTxpt(IReadOnlyList<PlaybackItem> items, BankInfo bank, EventOptions options, ISet<string> limitations)
    {
        var lines = new List<string> { $"# Event: {items.Count} entries; language={options.Language}; seed={options.RandomSeed}" };
        var endsAt = 0d;
        var hasOverlap = false;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var safeName = $"media-{item.MediaId:X8}-{index:D3}.wem";
            var gap = Math.Max(0, item.StartSeconds - endsAt);
            if (item.StartSeconds < endsAt - 0.001) hasOverlap = true;
            var settings = item.LoopCount > 1 ? $"#l{item.LoopCount}" : "#i";
            var padding = gap > 0 ? $" #p{gap.ToString("0.###", CultureInfo.InvariantCulture)}" : "";
            if (options.FadeOutSeconds > 0 && (index == items.Count - 1 || items.All(i => Math.Abs(i.StartSeconds) < 0.001)))
            {
                if (item.DurationSeconds > options.FadeOutSeconds)
                    settings += $" #b{(item.DurationSeconds - options.FadeOutSeconds).ToString("0.###", CultureInfo.InvariantCulture)} #f{options.FadeOutSeconds.ToString("0.###", CultureInfo.InvariantCulture)}";
                else limitations.Add("The TXTP subset cannot express a fade spanning more than the last segment; full event rendering still applies it at the final end.");
            }
            lines.Add(safeName + " " + settings + padding);
            endsAt = Math.Max(endsAt, item.StartSeconds + item.DurationSeconds);
        }
        if (hasOverlap)
        {
            if (items.All(i => Math.Abs(i.StartSeconds) < 0.001)) lines.Add("mode = layers");
            else
            {
                limitations.Add("The TXTP preview is limited to non-overlapping segments; render uses the full planned timing and mix.");
                lines.Add("# Overlap requires mixed groups; this planner leaves it to the FFmpeg render path.");
                lines.Add("mode = segments");
            }
        }
        else lines.Add("mode = segments");
        if (options.FadeOutSeconds > 0) lines.Add($"# Render applies a {options.FadeOutSeconds.ToString("0.###", CultureInfo.InvariantCulture)} second fade.");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed class StableRandom(uint seed)
    {
        private uint _state = seed == 0 ? 0x9E3779B9u : seed;
        public int Next(int maximum)
        {
            _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5;
            return (int)(_state % (uint)maximum);
        }
    }
}

public sealed class EventSelection
{
    internal AudioCatalog Catalog { get; }
    internal AudioEvent Event { get; }
    internal EventOptions Options { get; }
    internal List<uint[]> Choices { get; }
    public IReadOnlyList<MediaEntry> SelectedMedia { get; }
    internal EventSelection(AudioCatalog catalog, AudioEvent audioEvent, EventOptions options, List<uint[]> choices,
        IReadOnlyList<MediaEntry> selectedMedia)
    {
        Catalog = catalog; Event = audioEvent; Options = options; Choices = choices; SelectedMedia = selectedMedia;
    }
}
