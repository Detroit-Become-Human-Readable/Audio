using DetroitAudio.Core;
using DetroitAudio.Indexing;

namespace DetroitAudio.Desktop;

public sealed record SelectionSnapshot(CatalogQuery Query, int Generation, int[] Indices, IReadOnlyDictionary<int, string> Keys)
{
    public bool Grouped { get; init; }
    public IReadOnlySet<string> ExpandedGroups { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> PreservedBrowserKeys { get; init; } = new HashSet<string>();
    public static SelectionSnapshot Capture(CatalogQuery query, int generation, IEnumerable<VirtualMediaRow> rows) =>
        new(query, generation, rows.Select(row => row.Index).Distinct().Order().ToArray(),
            rows.Where(row => row.Media is not null).DistinctBy(row => row.Index).ToDictionary(row => row.Index, row => row.Media!.Key));
    public static SelectionSnapshot CaptureBrowser(CatalogQuery query, int generation, IEnumerable<VirtualMediaRow> rows,
        bool grouped, IReadOnlySet<string> expanded) => new(query, generation, rows.Select(row => row.Index).Distinct().Order().ToArray(),
            rows.Where(row => row.StableKey is not null).DistinctBy(row => row.Index).ToDictionary(row => row.Index, row => row.StableKey!))
        { Grouped = grouped, ExpandedGroups = new HashSet<string>(expanded) };

    public async Task<IReadOnlyList<MediaEntry>> ResolveBrowserEntriesAsync(CatalogStore store, CancellationToken token) => await Task.Run(() =>
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in PreservedBrowserKeys)
        {
            if (key.StartsWith("group:", StringComparison.Ordinal))
                foreach (var batch in store.EnumerateGroupMembers(Query, key[6..], token: token)) foreach (var member in batch) keys.Add(member.Key);
            else keys.Add(key);
        }
        foreach (var block in Indices.GroupBy(index => index / VirtualMediaSource.BlockSize))
        {
            token.ThrowIfCancellationRequested(); var offset = block.Key * VirtualMediaSource.BlockSize;
            var rows = store.ReadBrowserRows(Query, Grouped, ExpandedGroups, offset, VirtualMediaSource.BlockSize, token);
            foreach (var index in block)
            {
                var position = index - offset;
                if (position >= rows.Count || Keys.TryGetValue(index, out var expected) && expected != rows[position].Key)
                    throw new InvalidOperationException("The selected results have changed. Select the audio again.");
                var row = rows[position];
                if (row.Kind == BrowserRowKind.GroupHeader)
                    foreach (var batch in store.EnumerateGroupMembers(Query, row.GroupKey!, token: token))
                    foreach (var member in batch) keys.Add(member.Key);
                else if (row.Media is { } media) keys.Add(media.Key);
            }
        }
        return (IReadOnlyList<MediaEntry>)store.CreateOperationCatalog(mediaKeys: keys, token: token).Media;
    }, token);

    public async Task<HashSet<string>> ResolveBrowserKeysAsync(CatalogStore store, CancellationToken token) => await Task.Run(() =>
    {
        var keys = new HashSet<string>(PreservedBrowserKeys, StringComparer.Ordinal);
        foreach (var block in Indices.GroupBy(index => index / VirtualMediaSource.BlockSize))
        {
            var offset = block.Key * VirtualMediaSource.BlockSize;
            var rows = store.ReadBrowserRows(Query, Grouped, ExpandedGroups, offset, VirtualMediaSource.BlockSize, token);
            foreach (var index in block)
            {
                token.ThrowIfCancellationRequested(); var position = index - offset;
                if (position >= rows.Count || Keys.TryGetValue(index, out var expected) && expected != rows[position].Key) throw new InvalidOperationException("The selected results have changed.");
                keys.Add(rows[position].Key);
            }
        }
        return keys;
    }, token);

    public async Task<IReadOnlyList<MediaEntry>> ResolveSelectedEntriesAsync(
        Func<CatalogQuery, CancellationToken, IReadOnlyList<MediaEntry>> read, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            var result = new List<MediaEntry>(Indices.Length);
            foreach (var group in Indices.GroupBy(index => index / VirtualMediaSource.BlockSize))
            {
                token.ThrowIfCancellationRequested();
                var offset = group.Key * VirtualMediaSource.BlockSize;
                var entries = read(Query with { Offset = offset, Limit = VirtualMediaSource.BlockSize }, token);
                foreach (var index in group)
                {
                    var position = index - offset;
                    if (position >= entries.Count) throw new InvalidOperationException("The selected results have changed. Select the audio again.");
                    var media = entries[position];
                    if (Keys.TryGetValue(index, out var key) && key != media.Key) throw new InvalidOperationException("The selected results have changed. Select the audio again.");
                    result.Add(media);
                }
            }
            token.ThrowIfCancellationRequested();
            return (IReadOnlyList<MediaEntry>)result;
        }, token).ConfigureAwait(false);
    }
}
