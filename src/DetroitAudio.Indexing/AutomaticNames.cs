using System.Text.RegularExpressions;
using DetroitAudio.Core;

namespace DetroitAudio.Indexing;

public static partial class NameRecovery
{
    /// <summary>Adds hash-matched PE data strings as reviewable candidates in observed Wwise namespaces.</summary>
    public static int ApplyExecutableDataCandidates(AudioCatalog catalog, IEnumerable<string> paths, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var targets = ObservedTargets(catalog);
        var namespacesById = targets.GroupBy(target => target.Id).ToDictionary(group => group.Key,
            group => group.Select(target => target.Namespace).Distinct(StringComparer.Ordinal).ToArray());
        var added = 0;
        var unique = new HashSet<(string Namespace, uint Id, string Text, string Sha256, long Offset)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<ExecutableString> strings;
            try { strings = ExecutableStringScanner.Read(path, token); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var candidate in strings)
            {
                token.ThrowIfCancellationRequested();
                var id = Hash(candidate.Text);
                foreach (var space in namespacesById.GetValueOrDefault(id, []))
                {
                    if (!unique.Add((space, id, candidate.Text, candidate.InputSha256, candidate.Offset))) continue;
                    var evidence = new NameEvidence(candidate.Text, space, id, NameKind.Candidate,
                        $"{Path.GetFullPath(path)} [{candidate.Section}]", candidate.Offset,
                        "Medium", "FNV-1 lowercase UTF-8", InputSha256: candidate.InputSha256);
                    catalog.Names.Add(evidence);
                    added++;
                }
            }
        }
        return added;
    }

    public static void ApplyAutomatic(AudioCatalog catalog, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var targets = ObservedTargets(catalog).ToDictionary(target => target, _ => new List<NameEvidence>());
        var namespaces = targets.Keys.Select(key => key.Namespace).Distinct().ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strings = catalog.Names.Select(name => (name.Text, name.Source, name.Offset, name.InputSha256))
            .Concat(catalog.Localization.Select(text => (Text: text.Key, text.Source, text.Offset, InputSha256: (string?)null)))
            .Concat(catalog.Localization.Select(text => (text.Text, text.Source, text.Offset, InputSha256: (string?)null)));
        foreach (var source in strings)
        {
            token.ThrowIfCancellationRequested();
            if (seen.Count >= 500_000) break;
            foreach (Match match in Identifier().Matches(source.Text))
            {
                var value = match.Value;
                var variants = new List<string> { value };
                for (var index = value.IndexOf('_'); index >= 0 && variants.Count < 32; index = value.IndexOf('_', index + 1))
                    if (value.Length - index > 2) variants.Add(value[(index + 1)..]);
                foreach (var candidate in variants)
                {
                    if (candidate.Length > 256 || !seen.Add(candidate)) continue;
                    var id = Hash(candidate);
                    foreach (var space in namespaces)
                        if (targets.TryGetValue((space, id), out var names)) names.Add(new(candidate, space, id, NameKind.Candidate,
                            source.Source, source.Offset, "Medium", "FNV-1 lowercase UTF-8", InputSha256: source.InputSha256));
                }
            }
        }
        var existing = catalog.Names.Select(name => (name.Namespace, name.Id, name.Text)).ToHashSet();
        foreach (var names in targets.Values)
            foreach (var name in names) if (existing.Add((name.Namespace, name.Id, name.Text))) catalog.Names.Add(name);
        foreach (var entry in catalog.Events)
        {
            if (!targets.TryGetValue(("WwiseEvent", entry.Id), out var matches)) continue;
            entry.Names.AddRange(matches.Where(name => !entry.Names.Any(known => known.Text.Equals(name.Text, StringComparison.OrdinalIgnoreCase))));
            var unique = entry.Names.Select(name => name.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (IsNumeric(entry.Name) && unique.Length == 1) entry.Name = unique[0];
        }
        foreach (var bank in catalog.Banks)
        {
            bank.Names.AddRange(catalog.Names.Where(name => name.Namespace == "WwiseBank" && name.Id == bank.Id));
            var unique = bank.Names.Select(name => name.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (IsNumeric(bank.Name) && unique.Length == 1) bank.Name = unique[0];
        }
        MarkCollisions(catalog);
        var banks = catalog.Banks.ToDictionary(bank => bank.Key);
        var events = catalog.Events.ToDictionary(entry => entry.Key);
        foreach (var media in catalog.Media)
        {
            token.ThrowIfCancellationRequested();
            if (banks.TryGetValue(media.BankKey, out var bank)) { media.BankName = bank.Name; media.RawLanguageId = bank.LanguageId; media.Language = BankLanguage(bank.LanguageId); }
            var labels = new List<(string Text, NameEvidence Evidence, int Rank)>();
            foreach (var key in media.EventKeys)
            {
                if (!events.TryGetValue(key, out var entry)) continue;
                foreach (var alternate in entry.Names.Select(name => name.Text).Where(text => !IsNumeric(text)))
                    if (!media.Aliases.Contains(alternate)) media.Aliases.Add(alternate);
                foreach (var candidate in entry.Names.Where(name => name.Kind == NameKind.Candidate))
                    if (!media.Names.Contains(candidate)) media.Names.Add(candidate);
                if (IsNumeric(entry.Name)) continue;
                if (!media.Aliases.Contains(entry.Name)) media.Aliases.Add(entry.Name);
                var source = entry.Names.FirstOrDefault(name => name.Text == entry.Name);
                var evidence = new NameEvidence(entry.Name, "MediaLabel", media.Id, NameKind.Description, source?.Source ?? entry.Key, source?.Offset ?? 0,
                    source?.Kind is NameKind.Stored or NameKind.Verified ? "High" : "Medium");
                if (!media.Names.Contains(evidence)) media.Names.Add(evidence);
                labels.Add((entry.Name, evidence, entry.Behavior == EventBehavior.StateChange ? 0 : 1));
            }
            if (!IsNumeric(media.Name)) media.DisplayLabel = media.Name;
            else if (labels.Count > 0)
            {
                var chosen = labels.OrderBy(label => label.Rank).ThenBy(label => label.Text, StringComparer.OrdinalIgnoreCase).First();
                media.DisplayLabel = chosen.Text; media.NameKind = NameKind.Description;
            }
            else
            {
                media.DisplayLabel = media.BankName.Length > 0 ? $"{media.BankName} · {media.Id}" : media.Id.ToString();
                if (bank is not null)
                {
                    var source = bank.Names.FirstOrDefault();
                    media.Names.Add(new(media.DisplayLabel, "BankContextLabel", media.Id, NameKind.Description, source?.Source ?? bank.Key, source?.Offset ?? bank.Slice.Offset));
                    media.NameKind = NameKind.Description;
                }
            }
        }
    }

    private static bool IsNumeric(string text) => string.IsNullOrWhiteSpace(text) || uint.TryParse(text, out _);

    private static HashSet<(string Namespace, uint Id)> ObservedTargets(AudioCatalog catalog)
    {
        var targets = new HashSet<(string Namespace, uint Id)>();
        foreach (var entry in catalog.Events) targets.Add(("WwiseEvent", entry.Id));
        foreach (var bank in catalog.Banks)
        {
            targets.Add(("WwiseBank", bank.Id));
            foreach (var item in bank.Objects.Values)
            {
                if (item.StateGroupId is { } group) targets.Add(("WwiseStateGroup", group));
                if (item.StateValueId is { } value) targets.Add(("WwiseState", value));
                if (item.SwitchGroupId is { } switchGroup) targets.Add((item.SwitchGroupKind == GameSyncKind.State ? "WwiseStateGroup" : "WwiseSwitchGroup", switchGroup));
                if (item.DefaultSwitchId is { } defaultValue) targets.Add((item.SwitchGroupKind == GameSyncKind.State ? "WwiseState" : "WwiseSwitch", defaultValue));
                foreach (var path in item.DecisionPaths)
                foreach (var sync in path.Values)
                { targets.Add((sync.Kind == GameSyncKind.State ? "WwiseStateGroup" : "WwiseSwitchGroup", sync.GroupId)); targets.Add((sync.Kind == GameSyncKind.State ? "WwiseState" : "WwiseSwitch", sync.ValueId)); }
                foreach (var branch in item.Branches) targets.Add((item.SwitchGroupKind == GameSyncKind.State ? "WwiseState" : "WwiseSwitch", branch.ValueId));
            }
        }
        return targets;
    }
    private static string BankLanguage(uint id) => id switch
    {
        0 => "SFX", 1 => "ARA", 12 or 9 or 10 or 11 => "ENG", 15 => "FRE", 14 => "FRE-CA", 16 => "GER", 21 => "ITA", 22 => "JPN", 26 => "POL", 27 => "BRA", 28 => "POR", 30 => "RUS", 32 => "MEX", 33 or 34 => "SPA",
        _ => $"Language {id}"
    };
    private static void MarkCollisions(AudioCatalog catalog)
    {
        var collisions = catalog.Names.GroupBy(name => (name.Namespace, name.Id))
            .Where(group => group.Select(name => name.Text).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .ToDictionary(group => group.Key, group => $"{group.Key.Namespace}:{group.Key.Id:X8}");
        NameEvidence Update(NameEvidence name) => collisions.TryGetValue((name.Namespace, name.Id), out var group) ? name with { CollisionGroup = group } : name;
        catalog.Names = catalog.Names.Select(Update).ToList();
        foreach (var bank in catalog.Banks) bank.Names = bank.Names.Select(Update).ToList();
        foreach (var entry in catalog.Events) entry.Names = entry.Names.Select(Update).ToList();
        foreach (var media in catalog.Media) media.Names = media.Names.Select(Update).ToList();
    }
    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_\-.]{1,255}")]
    private static partial Regex Identifier();
}
