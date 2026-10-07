using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using DetroitAudio.Core;

namespace DetroitAudio.Indexing;

/// <summary>Scans external WEMs once and incrementally hashes unchanged files from a cache-local sidecar.</summary>
public static class ExternalWemInventoryBuilder
{
    private const int MaximumFiles = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static ExternalWemInventory Build(string sourcePath, string? sidecarPath = null, CancellationToken token = default)
    {
        var sourceIsDirectory = Directory.Exists(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var standaloneWem = !sourceIsDirectory && Path.GetExtension(fullSourcePath).Equals(".wem", StringComparison.OrdinalIgnoreCase);
        var root = sourceIsDirectory ? fullSourcePath : Path.GetDirectoryName(fullSourcePath)!;
        var inventory = new ExternalWemInventory { RootPath = root };
        if (!sourceIsDirectory && Path.GetExtension(fullSourcePath).ToLowerInvariant() is ".mid" or ".midi") return inventory;
        var previous = ReadSidecar(sidecarPath, out var cacheDiagnostic);
        if (cacheDiagnostic is not null) inventory.Diagnostics.Add(cacheDiagnostic);
        var old = previous?.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push(root);
        var count = 0;
        while (directories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { inventory.Diagnostics.Add(new("ExternalWemScanAccess", "A directory could not be scanned.", directory)); continue; }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { inventory.Diagnostics.Add(new("ExternalWemEntryAccess", "A filesystem entry could not be inspected.", entry.FullName)); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!standaloneWem) directories.Push(entry.FullName);
                    continue;
                }
                if (standaloneWem && !Path.GetFullPath(entry.FullName).Equals(fullSourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Path.GetExtension(entry.Name).Equals(".wem", StringComparison.OrdinalIgnoreCase)) continue;
                if (++count > MaximumFiles) { inventory.Diagnostics.Add(new("ExternalWemScanLimit", "External media discovery reached its file limit.")); directories.Clear(); break; }
                try
                {
                    var info = new FileInfo(entry.FullName); info.Refresh();
                    var metadata = ReadIdentity(info.FullName);
                    var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
                    var length = info.Length; var writeTicks = info.LastWriteTimeUtc.Ticks;
                    var previousMatch = old.TryGetValue(relative, out var prior) && prior.Length == length &&
                        prior.LastWriteUtcTicks == writeTicks && prior.FileIdentity == metadata.Identity &&
                        prior.ChangeTimeUtcTicks == metadata.ChangeTicks && metadata.Identity.Length > 0 && metadata.HasChangeTime;
                    var sha = previousMatch ? prior!.Sha256 : HashFile(info.FullName, token);
                    inventory.Files.Add(new(relative, info.FullName, length, writeTicks, metadata.Identity, metadata.ChangeTicks, sha));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { inventory.Diagnostics.Add(new("ExternalWemReadError", "An external WEM could not be read.", entry.FullName)); }
            }
        }
        inventory.Files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return inventory;
    }

    public static void SaveSidecar(ExternalWemInventory inventory, string? sidecarPath, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(sidecarPath)) return;
        token.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(sidecarPath));
        if (directory is not null) Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory!, Path.GetFileName(sidecarPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, inventory.Files, JsonOptions);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, sidecarPath, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static List<ExternalWemFile>? ReadSidecar(string? path, out Diagnostic? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var entries = JsonSerializer.Deserialize<List<ExternalWemFile>>(File.ReadAllBytes(path));
            if (entries is null || entries.Any(entry => !IsValidSidecarEntry(entry)) ||
                entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            {
                diagnostic = new("ExternalWemHashCacheInvalid", "The external-WEM hash cache is corrupt or contains duplicate paths; hashes were recomputed.", path);
                return null;
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            diagnostic = new("ExternalWemHashCacheInvalid", "The external-WEM hash cache could not be read; hashes were recomputed.", path);
            return null;
        }
    }

    private static bool IsValidSidecarEntry(ExternalWemFile? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.RelativePath) || Path.IsPathRooted(entry.RelativePath) || entry.RelativePath.Contains('\\') ||
            entry.RelativePath.Split('/').Any(segment => segment is "" or "." or "..") || entry.Length < 0 ||
            string.IsNullOrEmpty(entry.Sha256) || entry.Sha256.Length != 64 || entry.Sha256.Any(character => !Uri.IsHexDigit(character))) return false;
        return true;
    }

    private static string HashFile(string path, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024]; int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (string Identity, long ChangeTicks, bool HasChangeTime) ReadIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) return ("", 0, false);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hasId = GetFileInformationByHandleEx(handle, 18, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>());
        var hasBasic = GetFileInformationByHandleEx(handle, 0, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>());
        var identity = hasId ? $"{id.VolumeSerialNumber:X16}:{id.FileIdHigh:X16}{id.FileIdLow:X16}" : "";
        const long fileTimeEpochTicks = 504911232000000000L;
        return (identity, hasBasic ? basic.ChangeTime + fileTimeEpochTicks : 0, hasBasic);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FileIdInfo lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FileBasicInfo lpFileInformation, uint dwBufferSize);

    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong VolumeSerialNumber; public ulong FileIdLow; public ulong FileIdHigh; }
    [StructLayout(LayoutKind.Sequential)] private struct FileBasicInfo { public long CreationTime; public long LastAccessTime; public long LastWriteTime; public long ChangeTime; public uint FileAttributes; }
}
