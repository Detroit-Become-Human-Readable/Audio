using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DetroitAudio.Audio;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class ToolInstallerTests
{
    [Fact]
    public async Task EmbeddedManifestLoadsPinnedComponentsWithoutNetwork()
    {
        using var directory = new TemporaryDirectory();
        using var installer = new ToolInstaller();

        var status = await installer.GetStatusAsync(directory.Path);

        Assert.Equal(new[] { "vgmstream", "ffmpeg", "ww2ogg", "revorb" }, status.Select(component => component.Name));
        Assert.All(status, component => Assert.False(component.Ready));
    }

    [Fact]
    public async Task EnsureUsesOfflineInstalledBundleAndVerifiesEveryRequiredFile()
    {
        using var directory = new TemporaryDirectory();
        var payloads = new Dictionary<string, byte[]> { ["decoder.exe"] = [1, 2, 3], ["runtime.dll"] = [4, 5, 6] };
        var archive = MakeZip(payloads);
        var spec = MakeSpec("decoder", "https://example.invalid/tool.zip", archive, payloads);
        var component = Directory.CreateDirectory(Path.Combine(directory.Path, "decoder")).FullName;
        foreach (var (name, bytes) in payloads) await File.WriteAllBytesAsync(Path.Combine(component, name), bytes);
        var calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
        using var installer = new ToolInstaller(client, specs: [spec]);

        var installed = await installer.EnsureAsync(directory.Path);
        var ready = await installer.GetStatusAsync(directory.Path);

        Assert.True(installed.Single().Ready);
        Assert.True(ready.Single().Ready);
        Assert.Equal(0, calls);
        Assert.Equal(payloads["runtime.dll"], await File.ReadAllBytesAsync(Path.Combine(directory.Path, "decoder", "runtime.dll")));
    }

    [Fact]
    public async Task EnsureDownloadsVerifiesFilesAndIgnoresUnlistedArchiveFiles()
    {
        using var directory = new TemporaryDirectory();
        var payloads = new Dictionary<string, byte[]> { ["decoder.exe"] = [1, 2], ["runtime.dll"] = [3, 4], ["unlisted.txt"] = [9] };
        var archive = MakeZip(payloads);
        var required = payloads.Where(item => item.Key != "unlisted.txt").ToDictionary(item => item.Key, item => item.Value);
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) })));
        using var installer = new ToolInstaller(client, specs: [MakeSpec("decoder", "https://example.invalid/tool.zip", archive, required)]);

        var status = await installer.EnsureAsync(directory.Path);

        Assert.True(status.Single().Ready);
        Assert.False(File.Exists(Path.Combine(directory.Path, "decoder", "unlisted.txt")));
    }

    [Fact]
    public async Task EnsureCanInstallFromBundledSeedUsingPinnedComponentFiles()
    {
        using var directory = new TemporaryDirectory();
        var payloads = new Dictionary<string, byte[]> { ["bundle/decoder.exe"] = [2, 3], ["bundle/runtime.dll"] = [4, 5] };
        var seed = MakeZip(payloads);
        var required = payloads.ToDictionary(item => Path.GetFileName(item.Key), item => item.Value);
        var spec = new ToolComponentSpec("decoder", "1.0", "zip", Hash(seed),
            required.Select(item => new ToolFileSpec(item.Key, Hash(item.Value))).ToArray(), SeedArchive: "seed.zip", SeedPath: "bundle");
        var calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) => { calls++; throw new HttpRequestException("offline"); }));
        using var installer = new ToolInstaller(client, specs: [spec], seedStreamFactory: _ => Task.FromResult<Stream>(new MemoryStream(seed)));

        var status = await installer.EnsureAsync(directory.Path);

        Assert.True(status.Single().Ready);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BundledApplicationSeedRepairsPartialDecoderWithoutNetwork()
    {
        using var directory = new TemporaryDirectory();
        var component = Directory.CreateDirectory(Path.Combine(directory.Path, "vgmstream")).FullName;
        await File.WriteAllTextAsync(Path.Combine(component, "old-partial-file"), "old");
        var assembly = typeof(ToolInstaller).Assembly;
        await using var manifest = assembly.GetManifestResourceStream("DetroitAudio.Audio.Assets.install-tools.json")
            ?? throw new InvalidDataException("The embedded tool manifest is missing.");
        var specs = await JsonSerializer.DeserializeAsync<ToolComponentSpec[]>(manifest, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var spec = Assert.Single(specs!, component => component.Name == "vgmstream");
        Assert.NotNull(spec.SeedArchive); Assert.Equal("vgmstream", spec.SeedPath);
        var calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) => { calls++; throw new HttpRequestException("network must not be used"); }));
        using var installer = new ToolInstaller(client, assembly, [spec]);

        var status = await installer.EnsureAsync(directory.Path);

        Assert.True(status.Single().Ready);
        Assert.Equal(0, calls);
        Assert.False(File.Exists(Path.Combine(component, "old-partial-file")));
    }

    [Fact]
    public async Task BadArchiveHashOrMissingRequiredFileDoesNotReplaceExistingComponent()
    {
        using var directory = new TemporaryDirectory();
        var component = Directory.CreateDirectory(Path.Combine(directory.Path, "decoder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(component, "keep.txt"), "preserve");
        var payloads = new Dictionary<string, byte[]> { ["decoder.exe"] = [1, 2] };
        var archive = MakeZip(payloads);
        var spec = MakeSpec("decoder", "https://example.invalid/tool.zip", archive, new Dictionary<string, byte[]> { ["decoder.exe"] = [1, 2], ["runtime.dll"] = [3] })
            with { Sha256 = new string('0', 64) };
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) })));
        using var installer = new ToolInstaller(client, specs: [spec]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.EnsureAsync(directory.Path));

        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(component, "keep.txt")));
    }

    [Fact]
    public async Task MissingRequiredDllAfterVerifiedDownloadRollsBackExistingComponent()
    {
        using var directory = new TemporaryDirectory();
        var component = Directory.CreateDirectory(Path.Combine(directory.Path, "decoder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(component, "keep.txt"), "preserve");
        var actual = new Dictionary<string, byte[]> { ["decoder.exe"] = [1, 2] };
        var required = new Dictionary<string, byte[]> { ["decoder.exe"] = [1, 2], ["runtime.dll"] = [3] };
        var archive = MakeZip(actual);
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) })));
        using var installer = new ToolInstaller(client, specs: [MakeSpec("decoder", "https://example.invalid/tool.zip", archive, required)]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.EnsureAsync(directory.Path));

        Assert.Contains("runtime.dll", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(component, "keep.txt")));
    }

    [Fact]
    public async Task NetworkFailureIsReportedAndLeavesNoStagingFolder()
    {
        using var directory = new TemporaryDirectory();
        var payloads = new Dictionary<string, byte[]> { ["decoder.exe"] = [1] };
        var archive = MakeZip(payloads);
        using var client = new HttpClient(new DelegateHandler((_, _) => throw new HttpRequestException("network unavailable")));
        using var installer = new ToolInstaller(client, specs: [MakeSpec("decoder", "https://example.invalid/tool.zip", archive, payloads)]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.EnsureAsync(directory.Path));

        Assert.Contains("network unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(directory.Path));
    }

    [Fact]
    public async Task CancellationStopsDownloadAndCleansOnlyItsStagingDirectory()
    {
        using var directory = new TemporaryDirectory();
        var component = Directory.CreateDirectory(Path.Combine(directory.Path, "decoder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(component, "user-note.txt"), "keep");
        var payloads = new Dictionary<string, byte[]> { ["decoder.exe"] = [1] };
        var archive = MakeZip(payloads);
        using var client = new HttpClient(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var installer = new ToolInstaller(client, specs: [MakeSpec("decoder", "https://example.invalid/tool.zip", archive, payloads)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.EnsureAsync(directory.Path, cancellationToken: cancellation.Token));

        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(component, "user-note.txt")));
        Assert.Single(Directory.GetDirectories(directory.Path));
    }

    [Fact]
    public async Task ArchiveTraversalIsRejectedBeforeAnyFileIsInstalled()
    {
        using var directory = new TemporaryDirectory();
        var payloads = new Dictionary<string, byte[]> { ["decoder.exe"] = [1] };
        var archive = MakeZip(payloads, ("../escaped.txt", new byte[] { 8 }));
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) })));
        using var installer = new ToolInstaller(client, specs: [MakeSpec("decoder", "https://example.invalid/tool.zip", archive, payloads)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.EnsureAsync(directory.Path));

        Assert.False(File.Exists(Path.Combine(directory.Path, "escaped.txt")));
        Assert.Empty(Directory.GetDirectories(directory.Path));
    }

    [Fact]
    public async Task ExistingDirectoryWithMissingDllIsNotReportedReady()
    {
        using var directory = new TemporaryDirectory();
        var component = Directory.CreateDirectory(Path.Combine(directory.Path, "decoder")).FullName;
        var exe = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(Path.Combine(component, "decoder.exe"), exe);
        var all = new Dictionary<string, byte[]> { ["decoder.exe"] = exe, ["runtime.dll"] = [4, 5] };
        using var installer = new ToolInstaller(specs: [MakeSpec("decoder", "https://example.invalid/tool.zip", [1], all)]);

        var status = await installer.GetStatusAsync(directory.Path);

        Assert.False(status.Single().Ready);
        Assert.Contains("runtime.dll", status.Single().Issue);
    }

    private static ToolComponentSpec MakeSpec(string name, string url, byte[] archive, IReadOnlyDictionary<string, byte[]> files) =>
        new(name, "1.0", "zip", Hash(archive), files.Select(item => new ToolFileSpec(item.Key, Hash(item.Value))).ToArray(), url,
            DisplayName: "Audio decoder");

    private static byte[] MakeZip(IReadOnlyDictionary<string, byte[]> files, params (string Path, byte[] Bytes)[] additional)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files.Concat(additional.Select(item => new KeyValuePair<string, byte[]>(item.Path, item.Bytes))))
            {
                var entry = archive.CreateEntry(path);
                using var target = entry.Open();
                target.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "detroit-audio-tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
