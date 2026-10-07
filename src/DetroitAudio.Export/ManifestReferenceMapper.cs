using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DetroitAudio.Core;

namespace DetroitAudio.Export;

/// <summary>Converts machine-local source references into portable manifest references.</summary>
internal sealed class ManifestReferenceMapper
{
    private readonly string _sourceRoot;
    private readonly Regex _outputRootReference;
    private readonly Dictionary<string, string> _portablePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _portableKeys = new(StringComparer.Ordinal);

    public ManifestReferenceMapper(AudioCatalog catalog, ExportPlan plan)
    {
        var outputRoot = Path.GetFullPath(plan.Options.Directory).Replace('\\', '/').TrimEnd('/');
        _outputRootReference = new Regex(Regex.Escape(outputRoot).Replace("/", @"[\\/]", StringComparison.Ordinal) + @"(?=$|[\\/'""\s,.;:!?])(?:[\\/])?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var source = string.IsNullOrWhiteSpace(catalog.SourcePath) ? null : Path.GetFullPath(catalog.SourcePath);
        _sourceRoot = source is null ? string.Empty : Directory.Exists(source) ? source : Path.GetDirectoryName(source) ?? source;
        if (source is not null) AddPath(source);
        if (_sourceRoot.Length > 0) AddPath(_sourceRoot);
        foreach (var media in plan.Items.Where(item => item.Media is not null).Select(item => item.Media!).DistinctBy(media => media.Key))
        {
            AddPath(media.Slice.FilePath);
            foreach (var slice in media.Candidates) AddPath(slice.FilePath);
            foreach (var candidate in media.CandidateRejections) AddPath(candidate.Slice.FilePath);
            foreach (var group in media.EquivalenceEvidence)
                foreach (var slice in group.SourceLocations) AddPath(slice.FilePath);
        }
        foreach (var bank in plan.Items.Where(item => item.Bank is not null).Select(item => item.Bank!).DistinctBy(bank => bank.Key))
        {
            AddPath(bank.Slice.FilePath);
        }
        foreach (var item in plan.Items)
        {
            AddKey(item.Key);
            if (item.Media is { } media)
            {
                AddKey(media.Key); AddKey(media.BankKey);
                foreach (var key in media.EventKeys) AddKey(key);
                foreach (var sourceEvidence in media.SourceEvidence) AddKey(sourceEvidence.BankKey);
                foreach (var name in media.Names) AddKey(name.Source);
            }
            if (item.Event is { } audioEvent)
            {
                AddKey(audioEvent.Key); AddKey(audioEvent.BankKey);
                foreach (var key in audioEvent.MediaKeys.Concat(audioEvent.RelatedPlayEventKeys)) AddKey(key);
            }
        }
        foreach (var omission in plan.Omitted ?? []) AddKey(omission.Key);
        if (plan.Options.EventOptions?.RelatedPlayEventKey is { } related) AddKey(related);
        foreach (var key in plan.Options.EventOverrides?.Keys ?? []) AddKey(key);
        foreach (var options in plan.Options.EventOverrides?.Values ?? []) AddKey(options.RelatedPlayEventKey);

        foreach (var key in AllKeys(plan).Distinct(StringComparer.Ordinal))
        {
            var id = "ref-" + Hash(PortableKeyIdentity(key))[..20].ToLowerInvariant();
            _portableKeys[key] = id;
            _portableKeys[key.Replace('\\', '/')] = id;
            _portableKeys[key.Replace('/', '\\')] = id;
        }
    }

    public string SourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        AddPath(path);
        return _portablePaths[System.IO.Path.GetFullPath(path)];
    }

    public string Key(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "ref-" + Hash(string.Empty)[..20].ToLowerInvariant();
        return _portableKeys.TryGetValue(key, out var value) ? value : "ref-" + Hash(PortableKeyIdentity(key))[..20].ToLowerInvariant();
    }

    public string Reference(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (System.IO.Path.IsPathRooted(value)) return SourcePath(value);
        return Key(value);
    }

    public string NameSource(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (System.IO.Path.IsPathRooted(value)) return SourcePath(value);
        if (_portableKeys.ContainsKey(value)) return Key(value);
        return Text(value);
    }

    public string Error(string? value, params string?[] relatedReferences)
    {
        var portable = _outputRootReference.Replace(ReplaceReferences(value ?? string.Empty, relatedReferences),
            match => match.Value.EndsWith('\\') || match.Value.EndsWith('/') ? "output/" : "output");
        return Text(portable);
    }

    public string Text(string? value, params string?[] relatedReferences)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var result = ReplaceReferences(value, relatedReferences);
        if (_sourceRoot.Length > 0)
        {
            foreach (var spelling in Spellings(System.IO.Path.GetFullPath(_sourceRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)))
            {
                result = result.Replace(spelling + System.IO.Path.DirectorySeparatorChar, "source/", StringComparison.OrdinalIgnoreCase);
                result = result.Replace(spelling + System.IO.Path.AltDirectorySeparatorChar, "source/", StringComparison.OrdinalIgnoreCase);
                result = result.Replace(spelling, "source", StringComparison.OrdinalIgnoreCase);
            }
        }
        return result;
    }

    private string ReplaceReferences(string value, IEnumerable<string?> relatedReferences)
    {
        foreach (var reference in relatedReferences.SelectMany(ExpandReference).Distinct(StringComparer.Ordinal).OrderByDescending(reference => reference.Length))
        {
            if (!System.IO.Path.IsPathRooted(reference) && !_portableKeys.ContainsKey(reference)) continue;
            var replacement = Reference(reference);
            foreach (var spelling in Spellings(reference))
                value = value.Replace(spelling, replacement, StringComparison.OrdinalIgnoreCase);
        }
        return value;
    }

    private static IEnumerable<string> ExpandReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) yield break;
        yield return reference;
        var separator = reference.IndexOf(':');
        while (separator >= 0 && separator < reference.Length - 1)
        {
            var suffix = reference[(separator + 1)..];
            if (System.IO.Path.IsPathFullyQualified(suffix)) yield return suffix;
            separator = reference.IndexOf(':', separator + 1);
        }
    }

    private string PortableKeyIdentity(string key)
    {
        if (System.IO.Path.IsPathRooted(key)) return SourcePath(key);
        var separator = key.IndexOf(':');
        while (separator >= 0 && separator < key.Length - 1)
        {
            var path = key[(separator + 1)..];
            try
            {
                if (System.IO.Path.IsPathFullyQualified(path))
                {
                    return key[..(separator + 1)] + SourcePath(path);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
            separator = key.IndexOf(':', separator + 1);
        }
        return key.Replace('\\', '/');
    }

    private static IEnumerable<string> AllKeys(ExportPlan plan)
    {
        foreach (var media in plan.Items.Where(item => item.Media is not null).Select(item => item.Media!).DistinctBy(media => media.Key))
        {
            yield return media.Key; yield return media.BankKey;
            foreach (var key in media.EventKeys) yield return key;
            foreach (var evidence in media.SourceEvidence) yield return evidence.BankKey;
            foreach (var name in media.Names.Where(name => IsKey(name.Source))) yield return name.Source;
        }
        foreach (var bank in plan.Items.Where(item => item.Bank is not null).Select(item => item.Bank!).DistinctBy(bank => bank.Key)) yield return bank.Key;
        foreach (var audioEvent in plan.Items.Where(item => item.Event is not null).Select(item => item.Event!).DistinctBy(item => item.Key))
        {
            yield return audioEvent.Key; yield return audioEvent.BankKey;
            foreach (var key in audioEvent.MediaKeys.Concat(audioEvent.RelatedPlayEventKeys)) yield return key;
        }
        foreach (var item in plan.Items) yield return item.Key;
        foreach (var omission in plan.Omitted ?? []) yield return omission.Key;
        if (plan.Options.EventOptions?.RelatedPlayEventKey is { } related) yield return related;
        if (plan.Options.EventOverrides is not null)
            foreach (var pair in plan.Options.EventOverrides)
            {
                yield return pair.Key;
                if (pair.Value.RelatedPlayEventKey is { } value) yield return value;
            }
    }

    private static bool IsKey(string value)
    {
        var separator = value.IndexOf(':');
        return separator > 0 && !value[..separator].Any(char.IsWhiteSpace);
    }

    private void AddKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        foreach (var reference in ExpandReference(key))
            if (System.IO.Path.IsPathRooted(reference)) AddPath(reference);
    }

    private void AddPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return; }
        if (_portablePaths.ContainsKey(fullPath)) return;

        var relative = _sourceRoot.Length == 0 ? null : RelativeInside(_sourceRoot, fullPath);
        string portable;
        if (relative is not null)
        {
            portable = relative;
        }
        else
        {
            // External sources remain distinguishable without publishing their machine-local parent path.
            var leaf = System.IO.Path.GetFileName(fullPath);
            var identity = _sourceRoot.Length == 0 ? fullPath : System.IO.Path.GetRelativePath(_sourceRoot, fullPath);
            portable = "external/" + Hash(NormalizeIdentity(identity))[..16].ToLowerInvariant() + "/" + leaf;
        }
        _portablePaths.Add(fullPath, portable.Replace('\\', '/'));
    }

    private static string? RelativeInside(string root, string fullPath)
    {
        var fullRoot = System.IO.Path.GetFullPath(root);
        var relative = System.IO.Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".") return "source";
        if (System.IO.Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + System.IO.Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return relative.Replace('\\', '/');
    }

    private static IEnumerable<string> Spellings(string fullPath)
    {
        yield return fullPath;
        var slash = fullPath.Replace('\\', '/');
        if (!string.Equals(slash, fullPath, StringComparison.Ordinal)) yield return slash;
        var backslash = fullPath.Replace('/', '\\');
        if (!string.Equals(backslash, fullPath, StringComparison.Ordinal)) yield return backslash;
    }

    private static string NormalizeIdentity(string path) => path.Replace('\\', '/').Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
