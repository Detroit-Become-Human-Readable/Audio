namespace DetroitAudio.Core;

public sealed record GraphObject(BankInfo Bank, BankObject Object);
public sealed record ObjectResolution(IReadOnlyList<GraphObject> Matches)
{
    public GraphObject? Unique => Matches.Count == 1 ? Matches[0] : null;
    public bool Ambiguous => Matches.Count > 1;
}
public sealed record GraphMediaSource(string BankKey, uint MediaId, bool Music, IReadOnlyList<GraphMarker> Markers);
public sealed record GraphMarker(string BankKey, uint SegmentId, MusicMarker Marker);
public sealed class GraphTraversal
{
    public List<GraphMediaSource> Sources { get; } = [];
    public HashSet<(GameSyncKind Kind, uint GroupId)> Groups { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
}

/// <summary>Resolves object identities and structural media paths within their bank context.</summary>
public sealed class CatalogGraph
{
    private readonly Dictionary<uint, GraphObject[]> objects;
    private readonly Dictionary<uint, BankInfo[]> banks;
    public CatalogGraph(AudioCatalog catalog)
    {
        objects = catalog.Banks.SelectMany(bank => bank.Objects.Values.Select(item => new GraphObject(bank, item)))
            .GroupBy(item => item.Object.Id).ToDictionary(group => group.Key, group => group.ToArray());
        banks = catalog.Banks.GroupBy(bank => bank.Id).ToDictionary(group => group.Key, group => group.ToArray());
    }

    public ObjectResolution Resolve(BankInfo preferred, uint id, byte? type = null, uint? bankId = null)
    {
        bool Matches(BankObject item) => type is null || item.Type == type;
        if (bankId is > 0)
        {
            var owners = banks.GetValueOrDefault(bankId.Value, []);
            if (preferred.Id == bankId && preferred.Objects.TryGetValue(id, out var own) && Matches(own))
                return new([new(preferred, own)]);
            return new(owners.Where(bank => bank.Objects.TryGetValue(id, out var item) && Matches(item))
                .Select(bank => new GraphObject(bank, bank.Objects[id])).ToArray());
        }
        if (preferred.Objects.TryGetValue(id, out var local) && Matches(local)) return new([new(preferred, local)]);
        return new(objects.GetValueOrDefault(id, []).Where(item => Matches(item.Object)).ToArray());
    }

    public static IReadOnlyList<uint> SelectChildren(BankObject node, IReadOnlyList<GameSyncValue>? values = null)
    {
        values ??= [];
        if (node.DecisionPaths.Count > 0)
        {
            var selected = Follow(node.DecisionPaths, 0);
            return selected.Select(path => path.TargetId).Where(id => id != 0).Distinct().ToArray();

            IEnumerable<DecisionPath> Follow(IReadOnlyList<DecisionPath> paths, int depth)
            {
                foreach (var leaf in paths.Where(path => path.Values.Count <= depth)) yield return leaf;
                foreach (var group in paths.Where(path => path.Values.Count > depth).GroupBy(path => (path.Values[depth].Kind, path.Values[depth].GroupId)))
                {
                    var branches = group.GroupBy(path => path.Values[depth].ValueId).ToDictionary(branch => branch.Key, branch => branch.ToArray());
                    var choice = values.LastOrDefault(value => value.Kind == group.Key.Kind && value.GroupId == group.Key.GroupId);
                    IEnumerable<DecisionPath[]> branchesToFollow = choice is null ? branches.Values : branches.TryGetValue(choice.ValueId, out var exact) ? [exact]
                        : branches.TryGetValue(0, out var fallback) ? [fallback] : Array.Empty<DecisionPath[]>();
                    foreach (var branch in branchesToFollow)
                    foreach (var leaf in Follow(branch, depth + 1)) yield return leaf;
                }
            }
        }
        if (node.SwitchGroupId is { } group && node.Branches.Count > 0)
        {
            var choice = values.LastOrDefault(value => value.Kind == node.SwitchGroupKind && value.GroupId == group);
            if (choice is not null)
            {
                var branch = node.Branches.FirstOrDefault(item => item.ValueId == choice.ValueId)
                    ?? node.Branches.FirstOrDefault(item => item.ValueId == (node.DefaultSwitchId ?? 0));
                return branch?.Children ?? [];
            }
        }
        return node.Children;
    }

    public GraphTraversal Explore(BankInfo preferred, uint id, IReadOnlyList<GameSyncValue>? values = null, uint? bankId = null, CancellationToken token = default)
    {
        var result = new GraphTraversal();
        var visited = new HashSet<(string Bank, uint Id, bool Music, string MarkerContext)>();
        var activePath = new HashSet<(string Bank, uint Id)>();
        Walk(preferred, id, bankId, false, [], 0);
        return result;

        void Walk(BankInfo owner, uint objectId, uint? targetBank, bool music, IReadOnlyList<GraphMarker> markerContext, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 64) { result.Diagnostics.Add(new("GraphDepth", "The audio graph exceeds 64 levels.")); return; }
            var resolution = Resolve(owner, objectId, bankId: targetBank);
            if (resolution.Unique is not { } match)
            {
                result.Diagnostics.Add(new(resolution.Ambiguous ? "AmbiguousObject" : "MissingObject", $"Object {objectId} could not be resolved in bank {owner.Name}."));
                return;
            }
            var node = match.Object; music |= node.Type is 10 or 11 or 12 or 13;
            var identity = (match.Bank.Key, node.Id);
            if (activePath.Contains(identity)) { result.Diagnostics.Add(new("GraphCycle", $"The audio graph contains a cycle at object {node.Id}.")); return; }
            var currentMarkers = node.Type == 10
                ? markerContext.Concat(node.Markers.Where(marker => !string.IsNullOrWhiteSpace(marker.Text))
                    .Select(marker => new GraphMarker(match.Bank.Key, node.Id, marker))).ToArray()
                : markerContext;
            var markerKey = string.Join(';', currentMarkers.Select(marker => $"{marker.BankKey}:{marker.SegmentId}:{marker.Marker.Id}:{marker.Marker.SourceOffset}"));
            if (!visited.Add((match.Bank.Key, node.Id, music, markerKey))) return;
            activePath.Add(identity);
            try
            {
            if (node.Support == PlanSupport.Unsupported) result.Diagnostics.Add(new("PartialObject", $"Object {node.Id} has partially decoded metadata."));
            if (node.SwitchGroupId is { } group) result.Groups.Add((node.SwitchGroupKind, group));
            foreach (var condition in node.DecisionPaths.SelectMany(path => path.Values)) result.Groups.Add((condition.Kind, condition.GroupId));
            if (node.Type != 11 || node.Clips.Count == 0)
                foreach (var source in node.Sources) result.Sources.Add(new(match.Bank.Key, source.Id, music, currentMarkers));
            foreach (var clip in node.Clips) result.Sources.Add(new(match.Bank.Key, clip.MediaId, true, currentMarkers));
            foreach (var child in SelectChildren(node, values)) Walk(match.Bank, child, null, music, currentMarkers, depth + 1);
            }
            finally { activePath.Remove(identity); }
        }
    }
}
