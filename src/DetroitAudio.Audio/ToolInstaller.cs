using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;

namespace DetroitAudio.Audio;

/// <summary>
/// Installs and verifies the pinned audio tools in a per-user directory. Definitions and any
/// offline seed archives are read from application resources; downloaded archives are never
/// treated as sources of installation metadata.
/// </summary>
public sealed class ToolInstaller : IDisposable
{
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private const int MaxArchiveEntries = 100_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallationLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Assembly _resourceAssembly;
    private readonly IReadOnlyList<ToolComponentSpec>? _providedSpecs;
    private readonly Func<CancellationToken, Task<Stream>>? _seedStreamFactory;
    private IReadOnlyList<ToolComponentSpec>? _specs;
    private bool _disposed;

    public ToolInstaller(
        HttpClient? client = null,
        Assembly? resourceAssembly = null,
        IReadOnlyList<ToolComponentSpec>? specs = null,
        Func<CancellationToken, Task<Stream>>? seedStreamFactory = null)
    {
        _client = client ?? new HttpClient();
        _ownsClient = client is null;
        _resourceAssembly = resourceAssembly ?? typeof(ToolInstaller).Assembly;
        _providedSpecs = specs;
        _seedStreamFactory = seedStreamFactory;
    }

    public async Task<IReadOnlyList<ToolStatus>> GetStatusAsync(string toolsRoot, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        var specs = await GetSpecsAsync(cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(toolsRoot);
        var statuses = new List<ToolStatus>(specs.Count);
        foreach (var spec in specs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSpec(spec);
            var issue = await VerifyInstalledAsync(Path.Combine(root, spec.Name), spec, cancellationToken).ConfigureAwait(false);
            statuses.Add(new ToolStatus(spec.Name, GetDisplayName(spec), spec.Version, issue is null, issue));
        }
        return statuses;
    }

    public async Task<IReadOnlyList<ToolStatus>> EnsureAsync(
        string toolsRoot,
        IProgress<ToolSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        var specs = await GetSpecsAsync(cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(toolsRoot);
        Directory.CreateDirectory(root);
        var gate = InstallationLocks.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var spec in specs.OrderBy(spec => string.IsNullOrWhiteSpace(spec.SeedArchive) ? 1 : 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSpec(spec);
                var target = Path.Combine(root, spec.Name);
                var issue = await VerifyInstalledAsync(target, spec, cancellationToken).ConfigureAwait(false);
                if (issue is not null)
                {
                    progress?.Report(new ToolSetupProgress(spec.Name, GetDisplayName(spec), "Preparing repair"));
                    await InstallComponentAsync(root, target, spec, progress, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        return await GetStatusAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }

    private async Task InstallComponentAsync(
        string root,
        string target,
        ToolComponentSpec spec,
        IProgress<ToolSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(root, $".{spec.Name}.install-{token}");
        var backup = Path.Combine(root, $".{spec.Name}.backup-{token}");
        Directory.CreateDirectory(staging);
        var swapped = false;
        var backedUp = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(spec.SeedArchive))
            {
                progress?.Report(new ToolSetupProgress(spec.Name, GetDisplayName(spec), "Using bundled offline tools"));
                await using var archive = _seedStreamFactory is not null
                    ? await _seedStreamFactory(cancellationToken).ConfigureAwait(false)
                    : OpenResource(spec.SeedArchive);
                await VerifyArchiveStreamHashAsync(archive, spec.Sha256, cancellationToken).ConfigureAwait(false);
                archive.Position = 0;
                await InstallFromArchiveAsync(archive, staging, spec, progress, cancellationToken, spec.SeedPath).ConfigureAwait(false);
            }
            else if (spec.Archive.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(spec.Url)) throw new InvalidDataException($"{GetDisplayName(spec)} has no download source.");
                progress?.Report(new ToolSetupProgress(spec.Name, GetDisplayName(spec), "Downloading", 0, 0));
                await using var archive = await DownloadVerifiedArchiveAsync(spec, progress, cancellationToken).ConfigureAwait(false);
                await InstallFromArchiveAsync(archive, staging, spec, progress, cancellationToken).ConfigureAwait(false);
            }
            else if (spec.Archive.Equals("exe", StringComparison.OrdinalIgnoreCase))
            {
                if (spec.Files.Count != 1 || string.IsNullOrWhiteSpace(spec.Url))
                    throw new InvalidDataException($"{GetDisplayName(spec)} has an invalid standalone executable definition.");
                var file = spec.Files[0];
                var destination = SafeDestination(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                progress?.Report(new ToolSetupProgress(spec.Name, GetDisplayName(spec), "Downloading", 0, 0));
                await DownloadFileAsync(spec.Url, destination, progress, spec, cancellationToken).ConfigureAwait(false);
                await VerifyFileHashAsync(destination, file.Sha256, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidDataException($"Unsupported archive type '{spec.Archive}' for {GetDisplayName(spec)}.");
            }

            var stagedIssue = await VerifyInstalledAsync(staging, spec, cancellationToken).ConfigureAwait(false);
            if (stagedIssue is not null) throw new InvalidDataException($"Downloaded {GetDisplayName(spec)} failed integrity verification: {stagedIssue}");

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                backedUp = true;
            }
            Directory.Move(staging, target);
            swapped = true;
            progress?.Report(new ToolSetupProgress(spec.Name, GetDisplayName(spec), "Installed", spec.Files.Count, spec.Files.Count));
            if (backedUp) Directory.Delete(backup, recursive: true);
        }
        catch (OperationCanceledException)
        {
            RollBackSwap(target, staging, backup, swapped, backedUp);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
        {
            RollBackSwap(target, staging, backup, swapped, backedUp);
            throw new InvalidOperationException($"Could not install {GetDisplayName(spec)}: {exception.Message}", exception);
        }
        finally
        {
            DeleteOwnedDirectory(staging);
            if (!swapped && backedUp && !Directory.Exists(target) && Directory.Exists(backup))
            {
                try { Directory.Move(backup, target); } catch { }
            }
        }
    }

    private async Task<Stream> DownloadVerifiedArchiveAsync(ToolComponentSpec spec, IProgress<ToolSetupProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        try
        {
            await DownloadToStreamAsync(spec.Url!, buffer, progress, spec, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(buffer, cancellationToken).ConfigureAwait(false));
            if (!digest.Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive SHA-256 did not match the pinned value for {GetDisplayName(spec)}.");
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DownloadFileAsync(string url, string path, IProgress<ToolSetupProgress>? progress, ToolComponentSpec spec, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await DownloadToStreamAsync(url, output, progress, spec, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadToStreamAsync(string url, Stream output, IProgress<ToolSetupProgress>? progress, ToolComponentSpec spec, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        if (total > MaxArchiveBytes) throw new InvalidDataException("The download exceeds the permitted size.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        long count = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count > MaxArchiveBytes) throw new InvalidDataException("The download exceeds the permitted size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new ToolSetupProgress(spec.Name, GetDisplayName(spec), "Downloading", count, total));
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallFromArchiveAsync(Stream source, string staging, ToolComponentSpec spec, IProgress<ToolSetupProgress>? progress, CancellationToken cancellationToken, string? pathPrefix = null)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("The archive contains too many entries.");
        long expandedTotal = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchiveEntry(entry);
            expandedTotal = checked(expandedTotal + entry.Length);
            if (entry.Length > MaxEntryBytes || expandedTotal > MaxArchiveBytes)
                throw new InvalidDataException("The archive expands beyond the permitted size.");
        }

        foreach (var file in spec.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requiredPath = string.IsNullOrWhiteSpace(pathPrefix) ? file.Path : $"{pathPrefix.TrimEnd('/', '\\')}/{file.Path}";
            var matches = archive.Entries.Where(entry => IsSelectedEntry(entry, requiredPath, allowBasename: pathPrefix is null)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"The archive must contain exactly one required file named '{file.Path}'.");
            var entry = matches[0];
            if (entry.Length <= 0) throw new InvalidDataException($"Required file '{file.Path}' is empty.");
            var destination = SafeDestination(staging, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var sourceFile = entry.Open())
            await using (var targetFile = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await CopyBoundedAsync(sourceFile, targetFile, MaxEntryBytes, cancellationToken).ConfigureAwait(false);
            }
            await VerifyFileHashAsync(destination, file.Sha256, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ToolSetupProgress(spec.Name, GetDisplayName(spec), "Verifying", Array.IndexOf(spec.Files.ToArray(), file) + 1, spec.Files.Count));
        }
    }

    private async Task<IReadOnlyList<ToolComponentSpec>> GetSpecsAsync(CancellationToken cancellationToken)
    {
        if (_specs is not null) return _specs;
        if (_providedSpecs is not null) return _specs = _providedSpecs;
        await using var source = OpenResource("install-tools.json");
        using var document = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        var components = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : GetProperty(document.RootElement, "components");
        var specs = new List<ToolComponentSpec>();
        foreach (var component in components.EnumerateArray())
        {
            var name = GetString(component, "name") ?? throw new InvalidDataException("Tool component has no name.");
            var version = GetString(component, "version") ?? "unknown";
            var archive = GetString(component, "archive") ?? "zip";
            var hash = GetString(component, "sha256") ?? "";
            var url = GetString(component, "url");
            var seedArchive = GetString(component, "seedArchive");
            var seedPath = GetString(component, "seedPath");
            var displayName = GetString(component, "displayName");
            var filesElement = GetProperty(component, "files");
            var hashMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (TryGetProperty(component, "fileHashes", out var fileHashes) && fileHashes.ValueKind == JsonValueKind.Object)
                foreach (var hashEntry in fileHashes.EnumerateObject()) hashMap[hashEntry.Name] = hashEntry.Value.GetString() ?? "";
            var files = new List<ToolFileSpec>();
            foreach (var item in filesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var path = item.GetString() ?? "";
                    var fileHash = hashMap.GetValueOrDefault(path) ?? GetString(component, "sha256_" + Path.GetFileName(path));
                    if (fileHash is null) throw new InvalidDataException($"No pinned SHA-256 was provided for '{path}'.");
                    files.Add(new ToolFileSpec(path, fileHash));
                }
                else
                {
                    var path = GetString(item, "path") ?? "";
                    files.Add(new ToolFileSpec(path, GetString(item, "sha256") ?? ""));
                }
            }
            specs.Add(new ToolComponentSpec(name, version, archive, hash, files, url, seedArchive, seedPath, displayName));
        }
        _specs = specs;
        return specs;
    }

    private Stream OpenResource(string suffix)
    {
        var name = _resourceAssembly.GetManifestResourceNames().FirstOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return name is null ? throw new FileNotFoundException($"Embedded tool resource '{suffix}' was not found.") :
            _resourceAssembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException($"Embedded tool resource '{suffix}' could not be opened.");
    }

    private static async Task<string?> VerifyInstalledAsync(string directory, ToolComponentSpec spec, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return $"{GetDisplayName(spec)} is not installed.";
        foreach (var file in spec.Files)
        {
            var path = SafeDestination(directory, file.Path);
            if (!File.Exists(path)) return $"Required file '{file.Path}' is missing.";
            try { await VerifyFileHashAsync(path, file.Sha256, cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException) { return $"Required file '{file.Path}' failed its SHA-256 integrity check."; }
        }
        return null;
    }

    private static async Task VerifyFileHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"SHA-256 mismatch for '{Path.GetFileName(path)}'.");
    }

    private static async Task VerifyArchiveStreamHashAsync(Stream stream, string expected, CancellationToken cancellationToken)
    {
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!digest.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bundled tool seed failed its pinned SHA-256 check.");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long count = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count > maximum) throw new InvalidDataException("Stream exceeds the permitted size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry)
    {
        var path = NormalizeArchivePath(entry.FullName);
        if (path.Length == 0) return;
        if (entry.Length < 0) throw new InvalidDataException("Archive entry has an invalid size.");
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000) throw new InvalidDataException("Symbolic links are not permitted in tool archives.");
    }

    private static bool IsSelectedEntry(ZipArchiveEntry entry, string requiredPath, bool allowBasename)
    {
        if (string.IsNullOrEmpty(entry.Name)) return false;
        var normalized = NormalizeArchivePath(entry.FullName);
        return normalized.Equals(NormalizeArchivePath(requiredPath), StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/" + NormalizeArchivePath(requiredPath), StringComparison.OrdinalIgnoreCase) ||
               (allowBasename && Path.GetFileName(normalized).Equals(Path.GetFileName(requiredPath), StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeDestination(string root, string relativePath)
    {
        var normalized = NormalizeArchivePath(relativePath);
        if (normalized.Length == 0 || normalized.EndsWith('/')) throw new InvalidDataException("A required tool file has an invalid path.");
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A tool file path escapes its installation directory.");
        return target;
    }

    private static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Contains(':')) throw new InvalidDataException("Archive contains an invalid path.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized)) throw new InvalidDataException("Archive contains an absolute path.");
        var parts = normalized.Split('/');
        if (parts.Any(part => part == ".." || part == ".")) throw new InvalidDataException("Archive contains a traversal path.");
        return string.Join('/', parts.Where(part => part.Length > 0));
    }

    private static void ValidateSpec(ToolComponentSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || spec.Name is "." or "..")
            throw new InvalidDataException("Tool component has an unsafe name.");
        if (spec.Files.Count == 0) throw new InvalidDataException($"Tool component '{spec.Name}' has no required files.");
        if (!Sha256Pattern.IsMatch(spec.Sha256)) throw new InvalidDataException($"Tool component '{spec.Name}' has an invalid archive SHA-256.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in spec.Files)
        {
            _ = SafeDestination(Path.GetTempPath(), file.Path);
            if (!Sha256Pattern.IsMatch(file.Sha256)) throw new InvalidDataException($"Required file '{file.Path}' has an invalid SHA-256.");
            if (!paths.Add(NormalizeArchivePath(file.Path))) throw new InvalidDataException($"Tool component '{spec.Name}' lists a required file more than once.");
        }
        if (spec.SeedArchive is null && spec.Archive.Equals("zip", StringComparison.OrdinalIgnoreCase) && !Uri.TryCreate(spec.Url, UriKind.Absolute, out _))
            throw new InvalidDataException($"Tool component '{spec.Name}' has an invalid download URL.");
    }

    private static JsonElement GetProperty(JsonElement element, string property) => TryGetProperty(element, property, out var value)
        ? value : throw new InvalidDataException($"Tool manifest is missing '{property}'.");

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var candidate in element.EnumerateObject())
                if (candidate.Name.Equals(property, StringComparison.OrdinalIgnoreCase)) { value = candidate.Value; return true; }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string property) =>
        TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string GetDisplayName(ToolComponentSpec spec) => string.IsNullOrWhiteSpace(spec.DisplayName) ? spec.Name : spec.DisplayName;

    private static void RollBackSwap(string target, string staging, string backup, bool swapped, bool backedUp)
    {
        if (swapped && Directory.Exists(target)) DeleteOwnedDirectory(target);
        if (backedUp && Directory.Exists(backup) && !Directory.Exists(target))
        {
            try { Directory.Move(backup, target); } catch { }
        }
        DeleteOwnedDirectory(staging);
    }

    private static void DeleteOwnedDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
