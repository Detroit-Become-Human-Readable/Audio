using DetroitAudio.Indexing;
using DetroitAudio.Core;
using System.Text.Json;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class ExternalWemInventoryTests
{
    [Fact]
    public void RecursiveInventoryChangesFingerprintForNestedAddRemoveAndSameMetadataReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "DetroitAudioInventory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sidecar = Path.Combine(root, "cache", "external-wem-hashes.json");
        try
        {
            var sibling = Path.Combine(root, "sibling.wem");
            File.WriteAllBytes(sibling, [1, 2, 3, 4]);
            var first = ExternalWemInventoryBuilder.Build(root, sidecar);
            ExternalWemInventoryBuilder.SaveSidecar(first, sidecar);
            var firstFingerprint = IndexService.Fingerprint(root, first);

            var nested = Path.Combine(root, "nested", "deeper"); Directory.CreateDirectory(nested);
            var nestedWem = Path.Combine(nested, "nested.wem"); File.WriteAllBytes(nestedWem, [5, 6, 7]);
            var afterAdd = ExternalWemInventoryBuilder.Build(root, sidecar);
            var addedFingerprint = IndexService.Fingerprint(root, afterAdd);
            Assert.NotEqual(firstFingerprint, addedFingerprint);
            Assert.Contains(afterAdd.Files, file => file.RelativePath == "nested/deeper/nested.wem");

            ExternalWemInventoryBuilder.SaveSidecar(afterAdd, sidecar);
            File.Delete(nestedWem);
            var afterRemove = ExternalWemInventoryBuilder.Build(root, sidecar);
            Assert.NotEqual(addedFingerprint, IndexService.Fingerprint(root, afterRemove));

            ExternalWemInventoryBuilder.SaveSidecar(afterRemove, sidecar);
            var priorWrite = File.GetLastWriteTimeUtc(sibling);
            File.WriteAllBytes(sibling, [9, 8, 7, 6]);
            File.SetLastWriteTimeUtc(sibling, priorWrite);
            var replaced = ExternalWemInventoryBuilder.Build(root, sidecar);
            Assert.NotEqual(IndexService.Fingerprint(root, afterRemove), IndexService.Fingerprint(root, replaced));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void InventoryHonorsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "DetroitAudioInventory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => ExternalWemInventoryBuilder.Build(root, token: cancellation.Token));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void DuplicateSidecarPathsAreRejectedRehashedAndRepairedWithoutTempResidue()
    {
        var root = Path.Combine(Path.GetTempPath(), "DetroitAudioInventory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sidecar = Path.Combine(root, "cache", "external-wem-hashes.json");
        try
        {
            var wemPath = Path.Combine(root, "sample.wem");
            File.WriteAllBytes(wemPath, [3, 1, 4, 1, 5]);
            var info = new FileInfo(wemPath);
            var corrupt = new ExternalWemFile("sample.wem", wemPath, info.Length, info.LastWriteTimeUtc.Ticks, "fake", 0, new string('0', 64));
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.WriteAllText(sidecar, JsonSerializer.Serialize(new[] { corrupt, corrupt }));

            var inventory = ExternalWemInventoryBuilder.Build(root, sidecar);
            var file = Assert.Single(inventory.Files);
            Assert.NotEqual(new string('0', 64), file.Sha256);
            Assert.Contains(inventory.Diagnostics, diagnostic => diagnostic.Code == "ExternalWemHashCacheInvalid" && diagnostic.EntryKey == sidecar);
            ExternalWemInventoryBuilder.SaveSidecar(inventory, sidecar);

            var repaired = JsonSerializer.Deserialize<ExternalWemFile[]>(File.ReadAllText(sidecar));
            Assert.Single(repaired!);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(sidecar)!, "*.tmp"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
