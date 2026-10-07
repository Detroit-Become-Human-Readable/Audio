using DetroitAudio.Core;

namespace DetroitAudio.Indexing;

public interface ICatalogSession : IDisposable
{
    CatalogHeader Header { get; }
    int Count(CatalogQuery query, CancellationToken token = default);
    IReadOnlyList<MediaRow> ReadRows(CatalogQuery query, CancellationToken token = default);
    int CountBrowserRows(CatalogQuery query, bool groupSimilar, IReadOnlySet<string>? expandedGroupKeys = null, CancellationToken token = default);
    IReadOnlyList<BrowserRow> ReadBrowserRows(CatalogQuery query, bool groupSimilar, IReadOnlySet<string>? expandedGroupKeys,
        int offset, int limit, CancellationToken token = default);
    IReadOnlyList<MediaRow> ResolveGroupMembers(CatalogQuery query, string groupKey, CancellationToken token = default);
    IEnumerable<IReadOnlyList<MediaRow>> EnumerateGroupMembers(CatalogQuery query, string groupKey, int batchSize = 500,
        CancellationToken token = default);
    MediaEntry? GetMedia(string key, CancellationToken token = default);
    AudioEvent? GetEvent(string key, CancellationToken token = default);
    IReadOnlyList<BankInfo> GetBanks(CancellationToken token = default);
    IReadOnlyList<AudioEvent> GetEvents(string? bankKey = null, CancellationToken token = default);
    BankInfo? GetBankGraph(string bankKey, CancellationToken token = default);
    AudioCatalog CreateOperationCatalog(IEnumerable<string>? mediaKeys = null, IEnumerable<string>? bankKeys = null,
        IEnumerable<string>? eventKeys = null, CancellationToken token = default);
}
