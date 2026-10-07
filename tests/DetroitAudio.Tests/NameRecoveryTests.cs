using DetroitAudio.Core;
using DetroitAudio.Indexing;
using Xunit;
namespace DetroitAudio.Tests;
public sealed class NameRecoveryTests
{
    [Fact] public void HashUsesLowercaseFnv1RatherThanFnv1a()
    { Assert.Equal(0xB6FA7167u, NameRecovery.Hash("Hello")); Assert.Equal(NameRecovery.Hash("hello"), NameRecovery.Hash("HELLO")); Assert.NotEqual(0x4F9F2CABu, NameRecovery.Hash("hello")); }
    [Fact] public void DictionaryCandidatesDoNotReplaceStoredNamesOrHashMediaIds()
    {
        var id = NameRecovery.Hash("Play_Ambient"); var media = new MediaEntry { Key = "media", Id = id, Name = "Original" };
        var catalog = new AudioCatalog { Media = [media], Events = [new() { Key = "event", Id = id, Name = "Stored", MediaKeys = [media.Key] }] };
        Assert.Equal(1, NameRecovery.ApplyCandidates(catalog, [new("Play_Ambient"), new("Filename", "Media", id)], "dictionary.txt"));
        Assert.Equal("Original", media.Name); Assert.Equal("Stored", catalog.Events[0].Name); Assert.Equal(NameKind.Candidate, Assert.Single(media.Names).Kind); Assert.Contains("Play_Ambient", media.Aliases);
    }
    [Fact] public void ExplicitIdCollisionsRetainEveryCandidate()
    {
        var catalog = new AudioCatalog { Events = [new() { Key = "event", Id = 42 }] };
        Assert.Equal(2, NameRecovery.ApplyCandidates(catalog, [new("First", Id: 42), new("Second", Id: 42)], "names.txt"));
        Assert.Equal(2, catalog.Events[0].Names.Count); Assert.All(catalog.Events[0].Names, n => Assert.Equal("event:0000002A", n.CollisionGroup));
    }

    [Fact]
    public void ExecutableScannerHonorsDataSectionBoundsAndSkipsCode()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("Data_Name\0")
            .Concat(System.Text.Encoding.Unicode.GetBytes("Utf16_Name\0"))
            .Concat(System.Text.Encoding.ASCII.GetBytes("Unterminated"))
            .ToArray();
        var bytes = PeImage(".rdata", data);
        var code = PeImage(".text", System.Text.Encoding.ASCII.GetBytes("Code_Name\0"), executable: true);
        var dataPath = Path.GetTempFileName();
        var codePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(dataPath, bytes);
            File.WriteAllBytes(codePath, code);
            var strings = ExecutableStringScanner.Read(dataPath);
            var codeStrings = ExecutableStringScanner.Read(codePath);

            Assert.Contains(strings, item => item.Text == "Data_Name" && item.Section == ".rdata" && item.Offset >= 0x200);
            Assert.Contains(strings, item => item.Text == "Utf16_Name" && item.Section == ".rdata" && item.Offset == 0x200 + "Data_Name\0".Length);
            Assert.DoesNotContain(strings, item => item.Text.StartsWith("eUtf16_", StringComparison.Ordinal));
            Assert.DoesNotContain(strings, item => item.Text == "Unterminated");
            Assert.Empty(codeStrings);
        }
        finally { File.Delete(dataPath); File.Delete(codePath); }
    }

    [Fact]
    public void ExecutableScannerSkipsOversizedAndUnterminatedRunsWithoutUsingTheirTails()
    {
        var content = System.Text.Encoding.ASCII.GetBytes(new string('A', 1024) + "\0")
            .Concat(System.Text.Encoding.Unicode.GetBytes(new string('B', 1024) + "\0"))
            .Concat(System.Text.Encoding.Unicode.GetBytes("Final_Name\0"))
            .Concat(System.Text.Encoding.Unicode.GetBytes(new string('C', 64 * 1024)))
            .ToArray();
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, PeImage(".rdata", content));
            var strings = ExecutableStringScanner.Read(path);
            Assert.Contains(strings, item => item.Text == "Final_Name");
            Assert.DoesNotContain(strings, item => item.Text.All(character => character is 'A' or 'B' or 'C'));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExecutableHashCollisionsRemainCandidatesWithInputProvenance()
    {
        const string first = "Candidate_000179bb";
        const string second = "Candidate_00054848";
        Assert.Equal(NameRecovery.Hash(first), NameRecovery.Hash(second));
        var content = System.Text.Encoding.ASCII.GetBytes(first + "\0" + second + "\0");
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, PeImage(".rdata", content));
            var id = NameRecovery.Hash(first);
            var catalog = new AudioCatalog { Events = [new() { Key = "synthetic-event", Id = id, Name = id.ToString() }] };
            Assert.Equal(2, NameRecovery.ApplyExecutableDataCandidates(catalog, [path]));
            NameRecovery.ApplyAutomatic(catalog);

            Assert.Equal(id.ToString(), catalog.Events[0].Name);
            Assert.Equal(2, catalog.Events[0].Names.Count);
            Assert.All(catalog.Events[0].Names, name =>
            {
                Assert.Equal(NameKind.Candidate, name.Kind);
                Assert.Equal("FNV-1 lowercase UTF-8", name.HashMethod);
                Assert.Matches("^[0-9a-f]{64}$", name.InputSha256!);
                Assert.Equal("WwiseEvent", name.Namespace);
                Assert.NotNull(name.CollisionGroup);
                Assert.True(name.Offset >= 0x200);
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SwitchOnlyDefaultValueIsAnObservedHashTarget()
    {
        const string name = "Gameplay_Mode_Default";
        var valueId = NameRecovery.Hash(name);
        var bank = new BankInfo { Key = "synthetic-bank", Id = 1, Objects =
        {
            [2] = new BankObject { Id = 2, Type = 3, ActionType = 0x1903, SwitchGroupId = 31, DefaultSwitchId = valueId }
        } };
        var catalog = new AudioCatalog { Banks = [bank], Names =
        {
            new NameEvidence(name, "SourceText", 0, NameKind.Stored, "synthetic")
        } };

        NameRecovery.ApplyAutomatic(catalog);

        Assert.Contains(catalog.Names, evidence => evidence.Id == valueId && evidence.Namespace == "WwiseSwitch" && evidence.Text == name);
    }

    private static byte[] PeImage(string sectionName, byte[] content, bool executable = false)
    {
        const int peOffset = 0x80;
        const int optionalSize = 0xF0;
        const int rawOffset = 0x200;
        var bytes = new byte[rawOffset + content.Length];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, 4), peOffset);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(peOffset, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 6, 2), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 20, 2), optionalSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 24, 2), 0x20B);
        var section = bytes.AsSpan(peOffset + 24 + optionalSize, 40);
        System.Text.Encoding.ASCII.GetBytes(sectionName).CopyTo(section);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(16, 4), (uint)content.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(20, 4), rawOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(36, 4), executable ? 0x60000020u : 0x40000040u);
        content.CopyTo(bytes.AsSpan(rawOffset));
        return bytes;
    }
}
