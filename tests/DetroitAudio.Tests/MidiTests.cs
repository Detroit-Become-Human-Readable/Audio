using System.Buffers.Binary;
using System.Text;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Export;
using DetroitAudio.Formats;
using DetroitAudio.Indexing;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class MidiTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SegmentScanSkipsNestedMidiWrappersIncludingAcrossWindows(bool crossWindow)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); var inner = Wrap(MakeMidi(0, 1));
        using var data = new MemoryStream(); using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
        {
            writer.Write("MThd"u8); Write32(writer, 6); Write16(writer, 0); Write16(writer, 1); Write16(writer, 480);
            writer.Write("MTrk"u8); Write32(writer, inner.Length + 7);
            writer.Write(new byte[] { 0, 0xF0, (byte)inner.Length }); writer.Write(inner); writer.Write(new byte[] { 0, 0xFF, 0x2F, 0 });
        }
        var outer = Wrap(data.ToArray()); var nestedOffset = outer.AsSpan(1).IndexOf(MidiReader.WrapperMagic) + 1;
        var padding = new byte[crossWindow ? (1 << 20) + 32 - nestedOffset - 6 : 0];
        var file = Path.Combine(fixture.DirectoryPath, "nested.dat"); File.WriteAllBytes(file, [.. padding, .. outer, .. Wrap(MakeMidi(0, 1))]);
        var catalog = await new DetroitCatalogReader().ReadAsync(file);
        Assert.Equal(2, catalog.Media.Count); Assert.Equal(data.Length, catalog.Media[0].Slice.Length);
    }

    [Theory]
    [InlineData(".mid")] [InlineData(".midi")]
    public async Task StandaloneReaderDoesNotInventoryUnrelatedWems(string extension)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); var file = Path.Combine(fixture.DirectoryPath, "sequence" + extension); File.WriteAllBytes(file, MakeMidi(0, 1));
        var nested = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "nested")).FullName; File.WriteAllBytes(Path.Combine(nested, "123.wem"), new byte[16]);
        var inventory = (ExternalWemInventory)typeof(DetroitCatalogReader).GetMethod("ScanExternalInventory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [file, CancellationToken.None])!;
        Assert.Empty(inventory.Files); Assert.Single((await new DetroitCatalogReader().ReadAsync(file)).Media);
    }

    [Fact]
    public void ExtendedHeadersVlqNamesAndSmpteDivisionArePreserved()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        var name = Encoding.UTF8.GetBytes(new string('N', 130));
        var events = new List<byte> { 0, 0xFF, 3, 0x81, 2 }; events.AddRange(name); events.AddRange([0, 0xFF, 0x2F, 0]);
        using var bytes = new MemoryStream(); using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("MThd"u8); Write32(writer, 8); Write16(writer, 0); Write16(writer, 1); Write16(writer, 0xE728); writer.Write((ushort)0);
            writer.Write("MTrk"u8); Write32(writer, events.Count); writer.Write(events.ToArray());
        }
        var midi = bytes.ToArray(); var path = Path.Combine(fixture.DirectoryPath, "extended.mid"); File.WriteAllBytes(path, midi);
        var document = MidiReader.ReadStandard(new(path, 0, midi.Length));
        Assert.Equal(0xE728, document.Metadata.Division); Assert.Equal(130, Assert.Single(document.Names).Text.Length);
        Assert.Equal(midi.Length, document.Slice.Length);
    }

    [Theory]
    [InlineData("running")] [InlineData("vlq")] [InlineData("end")] [InlineData("division")]
    public void MalformedMidiEventsAreRejected(string kind)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); var midi = MakeMidi(0, 1);
        if (kind == "running") midi[23] = 1;
        if (kind == "vlq") Array.Fill(midi, (byte)0x80, 22, 4);
        if (kind == "end") midi[^2] = 1;
        if (kind == "division") BinaryPrimitives.WriteUInt16BigEndian(midi.AsSpan(12), 0xE100);
        var file = Path.Combine(fixture.DirectoryPath, "bad.mid"); File.WriteAllBytes(file, midi);
        Assert.Throws<InvalidDataException>(() => MidiReader.ReadStandard(new(file, 0, midi.Length)));
    }

    [Fact]
    public async Task CancelledMidiBatchPublishesNoIncompleteFiles()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); var file = Path.Combine(fixture.DirectoryPath, "sample.mid"); File.WriteAllBytes(file, MakeMidi(0, 1));
        var catalog = await new DetroitCatalogReader().ReadAsync(file);
        var exporter = new ExportService(new AudioService(new(null, null, null, null, null, null, null)));
        var plan = exporter.Plan(catalog, new(Path.Combine(fixture.DirectoryPath, "output"), ExportFormat.Original), catalog.Media);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var report = await exporter.RunAsync(catalog, plan, cancellationToken: cancelled.Token);
        Assert.True(report.Cancelled); Assert.Equal("Cancelled", Assert.Single(report.Results).Status);
        Assert.Empty(Directory.EnumerateFiles(plan.Options.Directory, "*.mid", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(plan.Options.Directory, "*.tmp*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 2)] [InlineData(2, 2)]
    public async Task IndexedMidiIsNamedSearchableAndExportedByteForByte(int format, int tracks)
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        using var destination = FormatsFixtureBuilder.CreateEmpty();
        var midi = MakeMidi(format, tracks);
        fixture.AddResource(65, 123, Wrap(midi)); fixture.AddResource(65, 123, Wrap(midi)); fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        Assert.Equal(2, catalog.Media.Count);
        Assert.All(catalog.Media, entry =>
        {
            Assert.Equal("MIDI", entry.Category); Assert.Equal("MIDI", entry.Codec); Assert.Equal("Cue / test", entry.DisplayName);
            Assert.Equal(midi.Length, entry.Slice.Length); Assert.True(MediaPolicy.IsComplete(entry));
            Assert.False(MediaPolicy.CanPreview(entry)); Assert.False(MediaPolicy.CanConvert(entry));
        });
        catalog.Fingerprint = "generated";
        using var store = new CatalogStore(Path.Combine(fixture.DirectoryPath, "catalog")); store.Save(catalog, null, default);
        Assert.Equal(2, store.Count(new(Category: "MIDI", Search: "Cue")));
        var exporter = new ExportService(new AudioService(new(null, null, null, null, null, null, null)));
        var plan = exporter.Plan(catalog, new(Path.Combine(destination.DirectoryPath, "output"), ExportFormat.Original), catalog.Media);
        Assert.Equal(2, plan.Items.Select(item => item.RelativePath).Distinct().Count());
        var report = await exporter.RunAsync(catalog, plan);
        Assert.All(report.Results, result =>
        {
            Assert.Equal("Complete", result.Status); Assert.EndsWith(".mid", result.File);
            Assert.Equal(midi, File.ReadAllBytes(Path.Combine(plan.Options.Directory, result.File)));
        });
        var converted = exporter.Plan(catalog, new(Path.Combine(destination.DirectoryPath, "converted"), ExportFormat.Wav, Scope: ExportScope.All));
        Assert.Empty(converted.Items); Assert.Equal(2, converted.Omitted!.Count);
    }

    [Fact]
    public async Task InvalidMidiResourcesDoNotHideValidNeighbours()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); var midi = MakeMidi(1, 2);
        var truncated = Wrap(midi); BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(29), (uint)midi.Length + 1);
        var oversized = midi.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(18), uint.MaxValue);
        fixture.AddResource(65, 1, truncated); fixture.AddResource(65, 2, Wrap(oversized)); fixture.AddResource(65, 3, Wrap(midi)); fixture.WriteIndex();
        var catalog = await new DetroitCatalogReader().ReadAsync(fixture.IndexPath);
        Assert.Equal(3u, Assert.Single(catalog.Media).Id);
        Assert.Equal(2, catalog.Diagnostics.Count(item => item.Code == "MalformedMidiResource"));
    }

    [Fact]
    public async Task StandaloneMidiAndSegmentScanFindExactBoundaries()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); var midi = MakeMidi(1, 2);
        var file = Path.Combine(fixture.DirectoryPath, "standalone.mid"); File.WriteAllBytes(file, midi);
        var direct = await new DetroitCatalogReader().ReadAsync(file); Assert.Equal(midi.Length, Assert.Single(direct.Media).Slice.Length);
        var segment = Path.Combine(fixture.DirectoryPath, "sample.dat");
        var padding = new byte[(1 << 20) - 9]; File.WriteAllBytes(segment, [.. padding, .. Wrap(midi), .. Wrap(midi)]);
        var scanned = await new DetroitCatalogReader().ReadAsync(segment);
        Assert.Equal(2, scanned.Media.Count); Assert.Equal(2, scanned.Media.Select(entry => entry.Key).Distinct().Count());
        Assert.All(scanned.Media, entry => Assert.Equal(midi.Length, entry.Slice.Length));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DetroitCatalogReader().ReadAsync(file, cancellationToken: cancel.Token));
    }

    internal static byte[] Wrap(byte[] midi)
    {
        var result = new byte[33 + midi.Length]; "QZIP\0RAW_FILE"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(13), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(17), (uint)midi.Length + 12);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(21), 4); "MIDI"u8.CopyTo(result.AsSpan(25));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(29), (uint)midi.Length); midi.CopyTo(result, 33); return result;
    }

    internal static byte[] MakeMidi(int format, int tracks)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("MThd"u8); Write32(writer, 6); Write16(writer, format); Write16(writer, tracks); Write16(writer, 480);
        for (var track = 0; track < tracks; track++)
        {
            var name = Encoding.UTF8.GetBytes(track == 0 ? "Cue / test" : "Layer");
            var events = new List<byte> { 0, 0xFF, 3, (byte)name.Length }; events.AddRange(name);
            events.AddRange([0, 0xC0, 0, 0, 1, 0, 0xF0, 6, 45, 45, 45, 45, 45, 45, 0, 0xFF, 0x2F, 0]);
            writer.Write("MTrk"u8); Write32(writer, events.Count); writer.Write(events.ToArray());
        }
        return stream.ToArray();
    }
    private static void Write32(BinaryWriter writer, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value); writer.Write(bytes); }
    private static void Write16(BinaryWriter writer, int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value); writer.Write(bytes); }
}
