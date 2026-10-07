using System.Globalization;
using System.Text;
using System.Text.Json;
using DetroitAudio.Core;

namespace DetroitAudio.Indexing;

public sealed record NameCandidate(string Text, string Namespace = "Event", uint? Id = null);

public static partial class NameRecovery
{
    public static uint Hash(string text)
    {
        uint hash = 2166136261;
        foreach (var value in Encoding.UTF8.GetBytes(text.ToLowerInvariant())) hash = unchecked(hash * 16777619) ^ value;
        return hash;
    }
    public static IReadOnlyList<NameCandidate> ReadDictionary(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("The name dictionary exceeds 16 MB.");
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) return JsonSerializer.Deserialize<List<NameCandidate>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var result = new List<NameCandidate>();
        foreach (var line in File.ReadLines(path))
        {
            var value = line.Trim(); if (value.Length == 0 || value.StartsWith('#')) continue;
            var parts = value.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length == 3) result.Add(new(parts[2], parts[0], ParseId(parts[1])));
            else { var separator = value.IndexOf('='); result.Add(separator > 0 ? new(value[(separator + 1)..].Trim(), Id: ParseId(value[..separator].Trim())) : new(value)); }
        }
        return result;
    }
    private static uint ParseId(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : uint.Parse(value, CultureInfo.InvariantCulture);
    public static int ApplyCandidates(AudioCatalog catalog, IEnumerable<NameCandidate> candidates, string source, CancellationToken token = default)
    {
        var events = catalog.Events.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.ToArray());
        var banks = catalog.Banks.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.ToArray());
        var media = catalog.Media.ToDictionary(m => m.Key);
        var groups = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Text) && c.Text.Length <= 1024)
            .Where(c => c.Namespace.Equals("Event", StringComparison.OrdinalIgnoreCase) || c.Namespace.Equals("Bank", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => (Namespace: c.Namespace.ToLowerInvariant(), Id: c.Id ?? Hash(c.Text)));
        var added = 0;
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested(); var names = group.DistinctBy(c => c.Text, StringComparer.OrdinalIgnoreCase).ToArray();
            var collision = names.Length > 1 ? $"{group.Key.Namespace}:{group.Key.Id:X8}" : null;
            foreach (var candidate in names)
            {
                var match = Hash(candidate.Text) == group.Key.Id;
                var evidence = new NameEvidence(candidate.Text, group.Key.Namespace == "event" ? "WwiseEvent" : "WwiseBank", group.Key.Id, NameKind.Candidate, source, Confidence: match ? "Medium" : "Low", HashMethod: match ? "FNV-1 lowercase UTF-8" : null, CollisionGroup: collision);
                if (catalog.Names.Contains(evidence)) continue;
                if (group.Key.Namespace == "event" && events.TryGetValue(group.Key.Id, out var entries))
                {
                    catalog.Names.Add(evidence); added++;
                    foreach (var entry in entries)
                    {
                        entry.Names.Add(evidence);
                        foreach (var key in entry.MediaKeys) if (media.TryGetValue(key, out var item)) { if (!item.Aliases.Contains(candidate.Text)) item.Aliases.Add(candidate.Text); item.Names.Add(evidence); }
                    }
                }
                else if (group.Key.Namespace == "bank" && banks.ContainsKey(group.Key.Id)) { catalog.Names.Add(evidence); added++; }
            }
        }
        return added;
    }
}
