using System.Security.Cryptography;
using System.Text;
using DetroitAudio.Core;
using DetroitAudio.Formats;

namespace DetroitAudio.Indexing;

public sealed class IndexService
{
    private readonly ICatalogReader reader;
    public IndexService(ICatalogReader? reader = null) => this.reader = reader ?? new DetroitCatalogReader();

    public async Task<(AudioCatalog Catalog, CatalogStore Store)> OpenAsync(string path, string cacheRoot,
        IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default, bool rebuild = false)
    {
        var result = await OpenCoreAsync(path, cacheRoot, progress, cancellationToken, rebuild, true);
        return (result.Catalog!, result.Store);
    }

    public async Task<(CatalogHeader Header, CatalogStore Store)> OpenSessionAsync(string path, string cacheRoot,
        IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default, bool rebuild = false)
    {
        var result = await OpenCoreAsync(path, cacheRoot, progress, cancellationToken, rebuild, false);
        return (result.Store.Header, result.Store);
    }

    private async Task<(AudioCatalog? Catalog, CatalogStore Store)> OpenCoreAsync(string path, string cacheRoot,
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken, bool rebuild, bool full)
    {
        path = Path.GetFullPath(path);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
        var sourceCache = Path.Combine(cacheRoot, cacheKey);
        var sidecar = Path.Combine(sourceCache, "external-wem-hashes.json");
        var store = new CatalogStore(sourceCache);
        var inventory = await Task.Run(() => ExternalWemInventoryBuilder.Build(path, sidecar, cancellationToken), cancellationToken);
        var fingerprint = await Task.Run(() => Fingerprint(path, inventory, cancellationToken), cancellationToken);
        if (!rebuild)
        {
            progress?.Report(new("Opening", 0, 1));
            var cachedHeader = await Task.Run(() => store.ReadHeader(fingerprint, cancellationToken), cancellationToken);
            if (cachedHeader is not null)
            {
                if (inventory.Diagnostics.Any(diagnostic => diagnostic.Code == "ExternalWemHashCacheInvalid"))
                    await Task.Run(() => ExternalWemInventoryBuilder.SaveSidecar(inventory, sidecar, cancellationToken), cancellationToken);
                if (!full) return (null, store);
                var cached = await Task.Run(() => store.Load(fingerprint, cancellationToken), cancellationToken);
                if (cached is not null) return (cached, store);
            }
        }
        var catalog = reader is IExternalWemInventoryCatalogReader inventoryReader
            ? await inventoryReader.ReadAsync(path, inventory, progress, cancellationToken)
            : await reader.ReadAsync(path, progress, cancellationToken);
        progress?.Report(new("Reading names", 0, 1));
        await Task.Run(() => NameRecovery.ApplyExecutableDataCandidates(catalog, ExecutableInputs(path), cancellationToken), cancellationToken);
        await Task.Run(() => NameRecovery.ApplyAutomatic(catalog, cancellationToken), cancellationToken);
        var afterInventory = await Task.Run(() => ExternalWemInventoryBuilder.Build(path, sidecar, cancellationToken), cancellationToken);
        var afterRead = await Task.Run(() => Fingerprint(path, afterInventory, cancellationToken), cancellationToken);
        if (afterRead != fingerprint) throw new IOException("The game files changed during indexing. Reopen the source.");
        catalog.Fingerprint = fingerprint;
        await Task.Run(() => ExternalWemInventoryBuilder.SaveSidecar(afterInventory, sidecar, cancellationToken), cancellationToken);
        catalog.Diagnostics.RemoveAll(diagnostic => diagnostic.Code == "ExternalWemHashCacheInvalid");
        await Task.Run(() => store.Save(catalog, progress, cancellationToken), cancellationToken);
        return (full ? catalog : null, store);
    }

    public static string Fingerprint(string path, CancellationToken token = default) =>
        Fingerprint(path, ExternalWemInventoryBuilder.Build(path, token: token), token);

    public static string Fingerprint(string path, ExternalWemInventory inventory, CancellationToken token = default)
    {
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var selected = Directory.Exists(path) ? Directory.EnumerateFiles(root).Where(IsRelevant) : new[] { path }.Concat(Path.GetExtension(path).Equals(".idx", StringComparison.OrdinalIgnoreCase) ? Directory.EnumerateFiles(root).Where(IsRelevant) : []);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("DetroitAudio-parser-1-cache-" + CatalogStore.SchemaVersion));
        foreach (var file in selected.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(root, file).Replace('\\', '/').ToUpperInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"));
            using var stream = File.OpenRead(file);
            if (info.Extension.Equals(".idx", StringComparison.OrdinalIgnoreCase))
            {
                var buffer = new byte[128 * 1024]; int count;
                while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); }
            }
            else
            {
                var buffer = new byte[4096]; var count = stream.Read(buffer); hash.AppendData(buffer.AsSpan(0, count));
                if (info.Length > buffer.Length) { stream.Position = Math.Max(0, info.Length - buffer.Length); count = stream.Read(buffer); hash.AppendData(buffer.AsSpan(0, count)); }
            }
        }
        foreach (var file in ExecutableInputs(path))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file).ToUpperInvariant()));
            using var stream = File.OpenRead(file);
            var buffer = new byte[128 * 1024]; int count;
            while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); }
        }
        foreach (var file in inventory.Files)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes($"WEM:{file.RelativePath.ToUpperInvariant()}:{file.Length}:{file.LastWriteUtcTicks}:{file.FileIdentity}:{file.ChangeTimeUtcTicks}:{file.Sha256}"));
        }
        foreach (var diagnostic in inventory.Diagnostics.Where(value => value.Code != "ExternalWemHashCacheInvalid")
                     .OrderBy(value => value.Code, StringComparer.Ordinal).ThenBy(value => value.EntryKey, StringComparer.Ordinal))
        {
            var entryKey = diagnostic.EntryKey;
            if (entryKey is not null && Path.IsPathRooted(entryKey)) entryKey = Path.GetRelativePath(inventory.RootPath, entryKey).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes($"DIAG:{diagnostic.Code}:{entryKey}"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static IReadOnlyList<string> ExecutableInputs(string path)
    {
        if (!Directory.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".wem" or ".mid" or ".midi") return [];
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null || !File.Exists(Path.Combine(directory, "BigFile_PC.idx"))) return [];
        return Directory.EnumerateFiles(directory, "*.exe").Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsRelevant(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".idx" or ".dat" or ".dep" or ".bnk" || ext.Length == 4 && ext[1] == 'd' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]);
    }
}
