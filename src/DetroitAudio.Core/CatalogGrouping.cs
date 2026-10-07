using System.Text;

namespace DetroitAudio.Core;

/// <summary>Stable grouping information stored alongside catalog rows.</summary>
public sealed record CatalogGroupAssignment(string MediaKey, string GroupKey, string Label);

public static class CatalogGrouping
{
    public static IReadOnlyList<CatalogGroupAssignment> BuildAssignments(AudioCatalog catalog, CancellationToken token = default)
    {
        var eventsByMedia = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Link(string mediaKey, string eventKey)
        {
            if (!eventsByMedia.TryGetValue(mediaKey, out var keys)) eventsByMedia[mediaKey] = keys = new(StringComparer.Ordinal);
            keys.Add(eventKey);
        }
        foreach (var media in catalog.Media)
            foreach (var key in media.EventKeys) Link(media.Key, key);
        foreach (var entry in catalog.Events)
        {
            token.ThrowIfCancellationRequested();
            foreach (var key in entry.MediaKeys) Link(key, entry.Key);
            foreach (var link in entry.MediaLinks)
            {
                Link(link.MediaKey, entry.Key);
                if (link.PlaybackEventKey is { Length: > 0 } playbackKey) Link(link.MediaKey, playbackKey);
            }
        }

        var eventByKey = catalog.Events.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var assignments = new List<CatalogGroupAssignment>(catalog.Media.Count);
        foreach (var media in catalog.Media)
        {
            token.ThrowIfCancellationRequested();
            AudioEvent? preferred = null;
            var preferredRank = int.MaxValue;
            if (eventsByMedia.TryGetValue(media.Key, out var linked))
            {
                foreach (var key in linked)
                {
                    if (!eventByKey.TryGetValue(key, out var candidate)) continue;
                    var named = IsMeaningfulName(candidate.DisplayName, media.BankName);
                    var rank = candidate.Behavior == EventBehavior.StateChange && named ? 0
                        : (candidate.Behavior is EventBehavior.Playback or EventBehavior.Mixed) && named ? 1 : int.MaxValue;
                    if (rank == int.MaxValue) continue;
                    if (preferred is null || rank < preferredRank || rank == preferredRank && CompareEvent(candidate, preferred) < 0)
                    { preferred = candidate; preferredRank = rank; }
                }
            }

            if (preferred is not null)
            {
                assignments.Add(new(media.Key, EventGroupKey(media.BankKey, preferred.Key), preferred.DisplayName));
                continue;
            }

            // Fallbacks deliberately use the stored name, never a display label, alias, or generated description.
            if (IsMeaningfulName(media.Name, media.BankName))
                assignments.Add(new(media.Key, NameGroupKey(media.BankKey, media.Name), media.Name));
        }
        return assignments;
    }

    public static string EventGroupKey(string bankKey, string eventKey) =>
        "event:" + Part(bankKey) + ":" + Part(eventKey);
    public static string NameGroupKey(string bankKey, string storedName) =>
        "name:" + Part(bankKey) + ":" + Part(storedName);

    private static string Part(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static int CompareEvent(AudioEvent left, AudioEvent right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Key, right.Key);
    }

    private static bool IsMeaningfulName(string? name, string? bankName)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var value = name.Trim();
        if (bankName is { Length: > 0 } && StringComparer.OrdinalIgnoreCase.Equals(value, bankName.Trim())) return false;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return value.Length > 2 && !value[2..].All(Uri.IsHexDigit);
        return !value.All(char.IsDigit);
    }
}
