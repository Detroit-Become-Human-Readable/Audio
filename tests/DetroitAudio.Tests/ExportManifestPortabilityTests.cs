using System.Security.Cryptography;
using System.Text.Json;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Export;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class ExportManifestPortabilityTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "DetroitAudio-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FailedExportKeepsLocalDiagnosticsButRemovesOutputPathsFromTheManifest()
    {
        var sourceRoot = Path.Combine(_temporaryRoot, "Source"); Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "sample.wem"); File.WriteAllBytes(source, new byte[16]);
        var media = new MediaEntry { Key = "generated-media", Name = "Clip", BankName = "Bank", Language = "SFX", IsRiff = true,
            Slice = new(source, 0, 16), Availability = SourceAvailability.Available, Completeness = MediaCompleteness.Complete };
        var catalog = new AudioCatalog { SourcePath = source, Media = [media] };
        var service = new ExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        var output = Path.Combine(_temporaryRoot, "Output [batch]");
        var plan = service.Plan(catalog, new(output, ExportFormat.Original), [media]);
        var blockedFolder = Path.GetDirectoryName(OutputNaming.Resolve(output, Assert.Single(plan.Items).RelativePath))!;
        Directory.CreateDirectory(Path.GetDirectoryName(blockedFolder)!);
        File.WriteAllText(blockedFolder, "Keep this file");

        var report = await service.RunAsync(catalog, plan);
        var result = Assert.Single(report.Results);
        Assert.Equal("Failed", result.Status);
        Assert.Contains(blockedFolder, result.Error); // Jobs receives the full local diagnostic.
        Assert.Equal("Keep this file", File.ReadAllText(blockedFolder));
        Assert.DoesNotContain(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories), path => path.Contains(".tmp", StringComparison.Ordinal));
        var json = File.ReadAllText(report.ManifestPath);
        AssertNoLocalRoot(json, _temporaryRoot);
        using var document = JsonDocument.Parse(json);
        var manifestError = document.RootElement.GetProperty("entries")[0].GetProperty("Error").GetString()!;
        Assert.Contains("output/", manifestError);
        Assert.Contains("SFX", manifestError);
    }

    [Fact]
    public async Task SourceErrorsKeepTheirSourceReferenceWhenTheNameStartsWithTheOutputDirectory()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var source = Path.Combine(_temporaryRoot, "Output [batch].wem"); File.WriteAllBytes(source, new byte[16]);
        var media = new MediaEntry { Key = "generated-media", Name = "Clip", BankName = "Bank", IsRiff = true,
            Slice = new(source, 0, 16), Availability = SourceAvailability.Available, Completeness = MediaCompleteness.Complete };
        var catalog = new AudioCatalog { SourcePath = source, Media = [media] };
        var service = new ExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        var plan = service.Plan(catalog, new(Path.Combine(_temporaryRoot, "Output [batch]"), ExportFormat.Original), [media]);
        File.Delete(source);
        var report = await service.RunAsync(catalog, plan);
        Assert.Equal("Failed", Assert.Single(report.Results).Status);
        var json = File.ReadAllText(report.ManifestPath); AssertNoLocalRoot(json, _temporaryRoot);
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("entries")[0].GetProperty("Error").GetString()!;
        Assert.Contains(Path.GetFileName(source), error);
        Assert.DoesNotContain("output.wem", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wem:external:")]
    [InlineData("wem:")]
    public async Task ExternalSourceEvidenceUsesPortablePathsAndOpaqueKeysAcrossRelocation(string keyPrefix)
    {
        var first = await CreateManifestAsync(Path.Combine(_temporaryRoot, "first"), keyPrefix);
        var relocated = await CreateManifestAsync(Path.Combine(_temporaryRoot, "relocated"), keyPrefix);

        using var document = JsonDocument.Parse(first.Json);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("version").GetInt32());
        var entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
        var equivalence = Assert.Single(entry.GetProperty("equivalenceEvidence").EnumerateArray());
        Assert.Equal(first.ExpectedSha256, equivalence.GetProperty("Sha256").GetString());
        var locations = equivalence.GetProperty("SourceLocations").EnumerateArray().ToArray();
        Assert.Equal(2, locations.Length);
        Assert.Equal(new long[] { 4, 4 }, locations.Select(location => location.GetProperty("Offset").GetInt64()).ToArray());
        Assert.All(locations, location => Assert.Equal(8, location.GetProperty("Length").GetInt64()));
        var portablePaths = locations.Select(location => location.GetProperty("FilePath").GetString()!).ToArray();
        Assert.Equal(2, portablePaths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(portablePaths, path => Assert.DoesNotContain("..", path, StringComparison.Ordinal));
        Assert.All(portablePaths, path => Assert.False(Path.IsPathRooted(path)));
        Assert.StartsWith("Media/", portablePaths[0], StringComparison.Ordinal);

        var manifestKey = entry.GetProperty("Key").GetString()!;
        Assert.StartsWith("ref-", manifestKey, StringComparison.Ordinal);
        Assert.DoesNotContain("wem:external", manifestKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(manifestKey, relocated.Key);
        Assert.Equal(portablePaths, relocated.EquivalencePaths);
        Assert.Equal(first.SourceBankKey, relocated.SourceBankKey);
        Assert.StartsWith("ref-", first.SourceBankKey, StringComparison.Ordinal);
        Assert.Equal(first.NameSource, relocated.NameSource);
        Assert.StartsWith("ref-", first.NameSource, StringComparison.Ordinal);
        Assert.Contains(manifestKey, entry.GetProperty("resolution").GetString());
        Assert.Equal("CSNDEVNT metadata", entry.GetProperty("names")[1].GetProperty("source").GetString());
        var rejectionPaths = entry.GetProperty("candidateRejections").EnumerateArray()
            .Select(rejection => rejection.GetProperty("file").GetString()!).ToArray();
        Assert.Contains(rejectionPaths, path => path.StartsWith("external/", StringComparison.Ordinal));
        Assert.All(rejectionPaths, path => Assert.DoesNotContain("..", path, StringComparison.Ordinal));
        using var relocatedDocument = JsonDocument.Parse(relocated.Json);
        var relocatedEntry = relocatedDocument.RootElement.GetProperty("entries")[0];
        Assert.Equal(rejectionPaths, relocatedEntry.GetProperty("candidateRejections").EnumerateArray().Select(value => value.GetProperty("file").GetString()).ToArray());
        Assert.Equal(root.GetProperty("omitted").EnumerateArray().Select(value => value.GetProperty("Key").GetString()).ToArray(),
            relocatedDocument.RootElement.GetProperty("omitted").EnumerateArray().Select(value => value.GetProperty("Key").GetString()).ToArray());

        AssertNoLocalRoot(first.Json, first.SourceRoot);
        Assert.Contains("sourceFile\": \"Media/first/123.wem\"", first.Json, StringComparison.Ordinal);
        Assert.All(root.GetProperty("omitted").EnumerateArray(), omission => Assert.StartsWith("ref-", omission.GetProperty("Key").GetString()));
    }

    private async Task<(string Json, string SourceRoot, string ExpectedSha256, string Key, string[] EquivalencePaths, string SourceBankKey, string NameSource)> CreateManifestAsync(string root, string keyPrefix)
    {
        var sourceRoot = Path.Combine(root, "Source");
        var mediaA = Path.Combine(sourceRoot, "Media", "first", "123.wem");
        var mediaB = Path.Combine(sourceRoot, "Media", "second", "123.wem");
        var outsideMedia = Path.Combine(root, "ExternalMedia", "123.wem");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(mediaB)!);
        Directory.CreateDirectory(Path.GetDirectoryName(outsideMedia)!);
        var payload = new byte[] { 3, 5, 8, 13, 21, 34, 55, 89 };
        File.WriteAllBytes(mediaA, [0, 0, 0, 0, .. payload, 0, 0, 0, 0]);
        File.WriteAllBytes(mediaB, [1, 1, 1, 1, .. payload, 1, 1, 1, 1]);
        File.WriteAllBytes(outsideMedia, [2, 2, 2, 2, .. payload, 2, 2, 2, 2]);
        var sourceIndex = Path.Combine(sourceRoot, "index.dat");
        File.WriteAllBytes(sourceIndex, [0]);
        var key = keyPrefix + mediaA;
        var media = new MediaEntry
        {
            Key = key, Id = 123, Name = "123", BankName = "Generated", Language = "SFX", IsRiff = true,
            Slice = new(mediaA, 4, payload.Length), Availability = SourceAvailability.Available,
            State = MediaState.CompleteExternal, Completeness = MediaCompleteness.Complete,
            EquivalenceEvidence = [new(Convert.ToHexString(SHA256.HashData(payload)), [new(mediaA, 4, payload.Length), new(mediaB, 4, payload.Length)], "exact byte comparison")],
            SourceEvidence = [new("bank-at:" + mediaB, 7, new MediaReference(123, 1, (uint)payload.Length, 0, 0), 19)],
            ResolutionDetails = "Selected " + key + "; equivalent copy at " + mediaB.Replace('\\', '/') + ".",
            CandidateRejections = [new(new(mediaB, 4, payload.Length), "Candidate was rejected at " + mediaB), new(new(outsideMedia, 4, payload.Length), "External candidate was rejected at " + outsideMedia)],
            Names = [new("Generated candidate", "ExternalMediaCandidate", 123, NameKind.Description, "wem:external:" + mediaB, 4),
                new("Generated control", "WwiseEvent", 7, NameKind.Description, "CSNDEVNT metadata")]
        };
        // Export selection can materialize a row after the header catalog was loaded.
        var catalog = new AudioCatalog { SourcePath = sourceIndex, Fingerprint = "generated" };
        var service = new ExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        var plan = service.Plan(catalog, new(Path.Combine(root, "Output"), ExportFormat.Original), [media]);
        var missing = Path.Combine(sourceRoot, "Media", "missing", "987.wem");
        plan = plan with { Omitted = [new ExportOmission("bank-at:" + mediaB, "Unavailable source " + mediaB),
            new ExportOmission("wem:external:" + missing, "Unavailable source " + missing)] };
        var report = await service.RunAsync(catalog, plan);
        using var json = JsonDocument.Parse(File.ReadAllText(report.ManifestPath));
        var entry = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        var portableKey = entry.GetProperty("Key").GetString()!;
        var locations = Assert.Single(entry.GetProperty("equivalenceEvidence").EnumerateArray())
            .GetProperty("SourceLocations").EnumerateArray().Select(location => location.GetProperty("FilePath").GetString()!).ToArray();
        Assert.Equal(key, Assert.Single(report.Results).Key); // UI retry/selection still receives its internal key.
        var evidence = Assert.Single(entry.GetProperty("sourceEvidence").EnumerateArray());
        var name = entry.GetProperty("names")[0];
        return (File.ReadAllText(report.ManifestPath), root, Convert.ToHexString(SHA256.HashData(payload)), portableKey, locations,
            evidence.GetProperty("BankKey").GetString()!, name.GetProperty("source").GetString()!);
    }

    private static void AssertNoLocalRoot(string json, string root)
    {
        var spellings = new[] { root, root.Replace('\\', '/'), root.Replace('/', '\\'), root.Replace("\\", "\\\\", StringComparison.Ordinal) };
        Assert.All(spellings, spelling => Assert.DoesNotContain(spelling, json, StringComparison.OrdinalIgnoreCase));
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement);

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                Assert.All(spellings, spelling => Assert.DoesNotContain(spelling, element.GetString()!, StringComparison.OrdinalIgnoreCase));
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child);
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) Visit(property.Value);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, true);
    }
}
