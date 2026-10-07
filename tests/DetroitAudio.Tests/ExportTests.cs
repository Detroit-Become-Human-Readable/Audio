using System.Security.Cryptography;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Export;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DetroitAudio-tests", Guid.NewGuid().ToString("N"));
    [Fact] public async Task BatchPreservesBytesSkipsExistingAndKeepsManifestOrder()
    {
        Directory.CreateDirectory(directory); var source = Path.Combine(directory, "source.dat"); var bytes = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray(); File.WriteAllBytes(source, bytes);
        var catalog = new AudioCatalog { Media = Enumerable.Range(0, 8).Select(i => new MediaEntry { Key = "entry-" + i, Id = (uint)(i % 2), Name = "Sample", BankName = "Bank", Language = "SFX", Slice = new(source, i * 256, 128), IsRiff = true, Availability = SourceAvailability.Available }).ToList() };
        var service = new ExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        var plan = service.Plan(catalog, new(Path.Combine(directory, "output"), ExportFormat.Original), catalog.Media);
        var report = await service.RunAsync(catalog, plan);
        Assert.All(report.Results, r => Assert.Equal("Complete", r.Status)); Assert.Equal(catalog.Media.Select(m => m.Key), report.Results.Select(r => r.Key));
        for (var i = 0; i < plan.Items.Count; i++) Assert.Equal(SHA256.HashData(bytes.AsSpan(i * 256, 128)), SHA256.HashData(File.ReadAllBytes(Path.Combine(plan.Options.Directory, plan.Items[i].RelativePath))));
        var repeated = await service.RunAsync(catalog, plan); Assert.All(repeated.Results, r => Assert.Equal("Skipped", r.Status));
        var manifest = File.ReadAllText(report.ManifestPath); Assert.DoesNotContain(directory.Replace("\\", "\\\\"), manifest);
    }
    [Fact] public async Task CancelledBatchDoesNotPublishTemporaryFiles()
    {
        Directory.CreateDirectory(directory); var source = Path.Combine(directory, "source.dat"); File.WriteAllBytes(source, new byte[1024]);
        var entry = new MediaEntry { Key = "entry", Name = "Sample", Availability = SourceAvailability.Available, Slice = new(source, 0, 1024) };
        var catalog = new AudioCatalog { Media = [entry] }; var service = new ExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        var plan = service.Plan(catalog, new(Path.Combine(directory, "output"), ExportFormat.Original), [entry]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var report = await service.RunAsync(catalog, plan, cancellationToken: cancellation.Token);
        Assert.True(report.Cancelled); Assert.All(report.Results, r => Assert.Equal("Cancelled", r.Status)); Assert.DoesNotContain(Directory.EnumerateFiles(plan.Options.Directory, "*", SearchOption.AllDirectories), p => p.Contains(".tmp"));
    }
    [Fact] public void InstallationFolderCannotBeAnExportTarget()
    {
        Directory.CreateDirectory(directory); var source = Path.Combine(directory, "game.idx"); File.WriteAllText(source, "index");
        var catalog = new AudioCatalog { SourcePath = source, Resources = [new()] };
        var service = new ExportService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        Assert.Throws<IOException>(() => service.Plan(catalog, new(Path.Combine(directory, "export"), ExportFormat.Original), []));
    }
    [Fact] public async Task CancellationAfterWorkStartsPreservesExistingFilesAndRemovesTemporaryOutputs()
    {
        Directory.CreateDirectory(directory); var source = Path.Combine(directory, "source.dat"); File.WriteAllBytes(source, new byte[8192]);
        var media = Enumerable.Range(0, 8).Select(index => new MediaEntry { Key = "item-" + index, Name = "Clip", Id = (uint)index,
            Slice = new(source, 0, 8192), Availability = SourceAvailability.Available }).ToList();
        var catalog = new AudioCatalog { Media = media }; var service = new ExportService(new AudioService(new(null, null, null, null, null, null, null)));
        var plan = service.Plan(catalog, new(Path.Combine(directory, "output"), ExportFormat.Original, Overwrite: true), media);
        var existing = Path.Combine(plan.Options.Directory, plan.Items[0].RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!); File.WriteAllBytes(existing, [9, 8, 7]);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.Status == "Exporting") cancellation.Cancel(); });
        var report = await service.RunAsync(catalog, plan, progress, cancellation.Token);
        Assert.True(report.Cancelled); Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(existing));
        Assert.All(report.Results, result => Assert.Equal("Cancelled", result.Status));
        Assert.DoesNotContain(Directory.EnumerateFiles(plan.Options.Directory, "*", SearchOption.AllDirectories), file => file.Contains(".tmp", StringComparison.Ordinal));
    }
    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => report(value); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
