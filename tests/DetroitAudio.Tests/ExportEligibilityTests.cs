using System.Text.Json;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Export;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class ExportEligibilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DetroitAudio-tests", Guid.NewGuid().ToString("N"));
    private AudioCatalog Catalog()
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "input.dat"); File.WriteAllBytes(path, Enumerable.Range(0, 64).Select(index => (byte)index).ToArray());
        MediaEntry Entry(string key, MediaCompleteness completeness) => new() { Key = key, Id = 1, Name = key, IsRiff = true, Slice = new(path, 0, 64), Availability = SourceAvailability.Available, ContainerValidity = ContainerValidity.Valid, Completeness = completeness, State = completeness == MediaCompleteness.Fragment ? MediaState.PrefetchOnly : MediaState.CompleteEmbedded };
        return new() { Media = [Entry("complete", MediaCompleteness.Complete), Entry("fragment", MediaCompleteness.Fragment), Entry("unverified", MediaCompleteness.Unverified), new() { Key = "missing", State = MediaState.MissingExternal, Availability = SourceAvailability.Missing }] };
    }
    private static ExportService Service() => new(new(new AudioToolPaths(null, null, null, null, null, null, null)));

    [Theory] [InlineData(ExportFormat.Ogg)] [InlineData(ExportFormat.Wav)]
    public void AllConversionsIncludeAvailableAudioWithoutPromotingItsCompleteness(ExportFormat format)
    {
        var catalog = Catalog(); var plan = Service().Plan(catalog, new(Path.Combine(directory, "output"), format, Scope: ExportScope.All), [catalog.Media[1]]);
        Assert.Equal(new[] { "complete", "unverified" }, plan.Items.Select(item => item.Key)); Assert.Equal(new ExportCounts(1, 1, 1, 1), plan.Counts); Assert.Equal(2, plan.Omitted!.Count);
    }
    [Fact] public async Task AllOriginalsPreserveFragmentsAndUnverifiedBytesWithManifestEvidence()
    {
        var catalog = Catalog(); var service = Service(); var plan = service.Plan(catalog, new(Path.Combine(directory, "output"), ExportFormat.Original, Scope: ExportScope.All));
        Assert.Equal(3, plan.Items.Count); Assert.Contains(plan.Items, item => item.Key == "fragment" && item.RelativePath.StartsWith("Fragments") && item.RelativePath.EndsWith("__fragment.wem"));
        Assert.Contains(plan.Items, item => item.Key == "unverified" && item.RelativePath.StartsWith("Unverified"));
        var report = await service.RunAsync(catalog, plan); Assert.All(report.Results, result => Assert.Equal("Complete", result.Status));
        foreach (var item in plan.Items) Assert.Equal(File.ReadAllBytes(item.Media!.Slice.FilePath), File.ReadAllBytes(Path.Combine(plan.Options.Directory, item.RelativePath)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(report.ManifestPath));
        Assert.Contains(manifest.RootElement.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("completeness").GetString() == "Fragment");
    }
    [Fact] public async Task ConversionRunnerRejectsFragmentsEvenWhenCallerBypassesPlan()
    {
        var catalog = Catalog(); var fragment = catalog.Media[1];
        var plan = new ExportPlan(new(Path.Combine(directory, "output"), ExportFormat.Wav), [new(fragment.Key, "fragment.wav", Media: fragment)], 64);
        var report = await Service().RunAsync(catalog, plan); Assert.Equal("Failed", Assert.Single(report.Results).Status);
        Assert.False(File.Exists(Path.Combine(plan.Options.Directory, "fragment.wav")));
    }
    [Fact] public void EmptyCompleteBatchStillExposesCountsAndCancellationStopsPreparation()
    {
        var catalog = Catalog(); catalog.Media.RemoveAll(media => media.Completeness != MediaCompleteness.Fragment && media.Availability == SourceAvailability.Available);
        var plan = Service().Plan(catalog, new(Path.Combine(directory, "output"), ExportFormat.Wav, Scope: ExportScope.All));
        Assert.Empty(plan.Items); Assert.Equal(0, plan.Counts!.Complete);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Service().Plan(catalog, new(directory, ExportFormat.Original, Scope: ExportScope.All), cancellationToken: cancellation.Token));
    }
    [Fact] public async Task PartialPreviewRequiresExplicitIntentAndNeverChangesAssetDuration()
    {
        var media = Catalog().Media[1]; media.Duration = 80;
        var preview = new PreviewService(new AudioService(new AudioToolPaths(null, null, null, null, null, null, null)));
        var error = await Assert.ThrowsAsync<AudioToolException>(() => preview.CreateAsync(new(), media));
        Assert.Contains("Partial preview", error.Message); Assert.Equal(80, media.Duration);
    }
    [Fact] public async Task DecodablePartialPreviewReturnsItsOwnDurationWithoutChangingTheAsset()
    {
        var tools = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR");
        if (string.IsNullOrWhiteSpace(tools)) return;
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "prefix.wav");
        using (var file = File.Create(path))
        using (var writer = new BinaryWriter(file))
        {
            const int frames = 12000;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
            for (var index = 0; index < frames; index++) writer.Write((short)(Math.Sin(index * Math.PI * 2 * 440 / 48000) * 5000));
        }
        var media = new MediaEntry { Key = "fragment", IsRiff = true, State = MediaState.PrefetchOnly, Completeness = MediaCompleteness.Fragment, ContainerValidity = ContainerValidity.Valid, Availability = SourceAvailability.Available, Duration = 80, Slice = new(path, 0, new FileInfo(path).Length) };
        var service = new PreviewService(new AudioService(AudioToolPaths.Discover(tools)));
        await using var preview = await service.CreateAsync(new(), media, partialPreview: true);
        Assert.Equal(PreviewKind.Partial, preview.Kind); Assert.InRange(preview.Result.Duration.TotalSeconds, .249, .251);
        Assert.Equal(80, media.Duration); Assert.Equal(MediaCompleteness.Fragment, media.Completeness);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
