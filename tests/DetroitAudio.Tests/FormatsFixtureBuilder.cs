using System.Buffers.Binary;
using System.Text;

namespace DetroitAudio.Tests;

internal sealed class FormatsFixtureBuilder : IDisposable
{
    private static readonly byte[] IndexMagic = Encoding.ASCII.GetBytes("QUANTICDREAMTABINDEX");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DetroitAudio-format-fixtures", Guid.NewGuid().ToString("N"));
    private readonly List<(uint Type, uint Id, byte[] Data)> resources = [];
    private readonly List<byte> package = [];

    public string DirectoryPath => directory;
    public string IndexPath => Path.Combine(directory, "BigFile_PC.idx");

    internal static FormatsFixtureBuilder CreateEmpty()
    {
        var fixture = new FormatsFixtureBuilder();
        Directory.CreateDirectory(fixture.directory);
        return fixture;
    }

    public static FormatsFixtureBuilder CreateAudioCatalog()
    {
        var fixture = new FormatsFixtureBuilder();
        Directory.CreateDirectory(fixture.directory);

        var bankMediaId = 0x8A7B6C5Du;
        var bankName = "fixture_bank";
        var bankId = Fnv1(bankName);
        var bankWem = MakeWem([1, 2, 3, 4]);
        const uint soundEventId = 0x3456789A;
        var bank = MakeBank(bankId, bankMediaId, bankWem, soundEventId);
        const uint containerResourceId = 700;
        fixture.AddResource(29, containerResourceId, MakeDataContainer(bank));
        fixture.AddResource(1022, 1, MakeBankDescriptor(containerResourceId, bankName, bankId));

        const uint dialogueEventResourceId = 900;
        const uint dialogueEventWwiseId = 0x23456789;
        const uint dialogueAudioResourceId = 901;
        var dialogueName = new string('D', 64) + "_ENG";
        var dialogueWem = MakeWem([5, 6, 7, 8, 9, 10], 60000);
        fixture.AddResource(1031, dialogueEventResourceId, MakeEvent(dialogueEventWwiseId, "Play_Dialogue_Line"));
        fixture.AddResource(1033, dialogueAudioResourceId, MakeDialogueMedia(dialogueName, dialogueWem));
        fixture.AddResource(4091, 902, MakeComContainer([(1033u, dialogueAudioResourceId), (1031u, dialogueEventResourceId)]));
        var dialogueKey = dialogueName[..dialogueName.LastIndexOf('_')];
        fixture.AddResource(1016, 910, MakeLocalization(6, "ENG", dialogueKey, "Synthetic subtitle"));
        fixture.AddResource(1016, 911, MakeLocalization(5, "FRE", dialogueKey, "Sous-titre synthétique"));
        fixture.AddResource(1016, 912, MakePointerLocalization());

        fixture.AddResource(1023, 903, MakeWrappedEvent(soundEventId, "Play_Fixture_Sound"));
        fixture.WriteIndex();
        return fixture;
    }

    public static FormatsFixtureBuilder CreateMediaResolutionCatalog()
    {
        var fixture = new FormatsFixtureBuilder();
        Directory.CreateDirectory(fixture.directory);
        var uniqueId = 0x10203040u;
        var prefetchId = 0x20304050u;
        var missingId = 0x30405060u;
        var ambiguousId = 0x40506070u;

        fixture.AddBankFixture(1, "candidate_unique", MakeBankForSources(Fnv1("candidate_unique"),
            [(uniqueId, MakeWem([1, 2, 3]))], [(uniqueId, (byte)0, 0u, 0x00040001u)]));
        fixture.AddBankFixture(2, "source_unique", MakeBankForSources(Fnv1("source_unique"), [], [(uniqueId, (byte)2, 0u, 0x00040001u)]));

        var fragment = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 1, 2, 3, 4, 5, 6, 7, 8 };
        fixture.AddBankFixture(3, "source_prefetch", MakeBankForSources(Fnv1("source_prefetch"),
            [(prefetchId, fragment)], [(prefetchId, (byte)1, (uint)fragment.Length, 0x00040001u)]));
        fixture.AddBankFixture(4, "source_missing", MakeBankForSources(Fnv1("source_missing"), [], [(missingId, (byte)2, 0u, 0x00040001u)]));

        fixture.AddBankFixture(5, "candidate_ambiguous_a", MakeBankForSources(Fnv1("candidate_ambiguous_a"),
            [(ambiguousId, MakeWem([9, 8, 7]))], [(ambiguousId, (byte)0, 0u, 0x00040001u)]));
        fixture.AddBankFixture(6, "candidate_ambiguous_b", MakeBankForSources(Fnv1("candidate_ambiguous_b"),
            [(ambiguousId, MakeWem([6, 5, 4]))], [(ambiguousId, (byte)0, 0u, 0x00040001u)]));
        fixture.AddBankFixture(7, "source_ambiguous", MakeBankForSources(Fnv1("source_ambiguous"), [], [(ambiguousId, (byte)2, 0u, 0x00040001u)]));

        fixture.AddBankFixture(8, "source_plugin", MakeBankForSources(Fnv1("source_plugin"), [], [(0x50607080u, (byte)2, 0u, 2u)]));
        fixture.WriteIndex();
        return fixture;
    }

    internal void AddBankFixture(uint resourceId, string name, byte[] bank)
    {
        var containerId = 1000 + resourceId;
        AddResource(29, containerId, MakeDataContainer(bank));
        AddResource(1022, resourceId, MakeBankDescriptor(containerId, name, Fnv1(name)));
    }

    internal void AddResource(uint type, uint id, byte[] data) => resources.Add((type, id, data));

    internal void WriteIndex()
    {
        foreach (var resource in resources)
        {
            var offset = checked((uint)package.Count);
            package.AddRange(resource.Data);
            var record = new byte[28];
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0, 4), resource.Type);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(8, 4), resource.Id);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(12, 4), offset);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(16, 4), checked((uint)resource.Data.Length));
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(20, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(24, 4), 0);
            indexRows.Add(record);
        }
        File.WriteAllBytes(Path.Combine(directory, "BigFile_PC.dat"), package.ToArray());
        using var stream = File.Create(IndexPath);
        var header = new byte[105];
        IndexMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 18);
        stream.Write(header);
        foreach (var row in indexRows) stream.Write(row);
    }

    private readonly List<byte[]> indexRows = [];

    private static byte[] MakeBankDescriptor(uint containerId, string name, uint bankId)
    {
        using var tail = new MemoryStream();
        using (var writer = new BinaryWriter(tail, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("CSNDBNK_"));
            writer.Write(2u);
            writer.Write(checked((uint)(4 + bankId.ToString().Length + 4 + name.Length)));
            WriteSizedAscii(writer, bankId.ToString());
            WriteSizedAscii(writer, name);
        }
        return MakeComContainer([(29u, containerId)], tail.ToArray());
    }

    private static byte[] MakeDataContainer(byte[] bank)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("QZIP\0DC_INFO "));
        WriteUInt32(stream, 3);
        WriteUInt32(stream, 16);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, 1032);
        WriteUInt32(stream, 3);
        stream.Write(Encoding.ASCII.GetBytes("DC_DATA "));
        using var payload = new MemoryStream();
        payload.Write(Encoding.ASCII.GetBytes("CSNDBKDT"));
        WriteUInt32(payload, 0);
        WriteUInt32(payload, checked((uint)bank.Length));
        payload.Write(bank);
        using var dcData = new MemoryStream();
        WriteUInt32(dcData, checked((uint)payload.Length));
        WriteUInt32(dcData, 0);
        WriteUInt32(dcData, 0);
        WriteUInt32(dcData, 1);
        payload.Position = 0;
        payload.CopyTo(dcData);
        WriteUInt32(stream, 23);
        WriteUInt32(stream, checked((uint)dcData.Length));
        dcData.Position = 0;
        dcData.CopyTo(stream);
        return stream.ToArray();
    }

    private static byte[] MakeBank(uint bankId, uint mediaId, byte[] wem, uint eventId)
    {
        using var bank = new MemoryStream();
        WriteChunk(bank, "BKHD", BuildPayload(payload =>
        {
            WriteUInt32(payload, 120);
            WriteUInt32(payload, bankId);
            WriteUInt32(payload, 0); // language ID
            WriteUInt32(payload, 0); // feedback flag
        }));
        WriteChunk(bank, "DIDX", BuildPayload(payload =>
        {
            WriteUInt32(payload, mediaId);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, checked((uint)wem.Length));
        }));
        WriteChunk(bank, "DATA", wem);
        WriteChunk(bank, "HIRC", MakeHirc(eventId, mediaId, checked((uint)wem.Length)));
        return bank.ToArray();
    }

    internal static byte[] MakeBankForSources(uint bankId,
        IReadOnlyList<(uint Id, byte[] Data)> media,
        IReadOnlyList<(uint Id, byte StreamType, uint InMemorySize, uint PluginId)> sources, byte sourceFlags = 0, uint languageId = 0,
        uint bankVersion = 120)
    {
        using var bank = new MemoryStream();
        WriteChunk(bank, "BKHD", BuildPayload(payload =>
        {
            WriteUInt32(payload, bankVersion);
            WriteUInt32(payload, bankId);
            WriteUInt32(payload, languageId);
            WriteUInt32(payload, 0);
        }));

        var data = new MemoryStream();
        if (media.Count > 0)
        {
            var rows = new MemoryStream();
            uint offset = 0;
            foreach (var (id, bytes) in media)
            {
                WriteUInt32(rows, id);
                WriteUInt32(rows, offset);
                WriteUInt32(rows, checked((uint)bytes.Length));
                offset = checked(offset + (uint)bytes.Length);
                data.Write(bytes);
            }
            WriteChunk(bank, "DIDX", rows.ToArray());
            WriteChunk(bank, "DATA", data.ToArray());
        }

        using var hirc = new MemoryStream();
        WriteUInt32(hirc, checked((uint)sources.Count));
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var objectId = 0x60000000u + checked((uint)index);
            WriteHircObject(hirc, 2, objectId, MakeSoundPayload(source.Id, source.StreamType, source.InMemorySize, source.PluginId, sourceFlags));
        }
        WriteChunk(bank, "HIRC", hirc.ToArray());
        return bank.ToArray();
    }

    private static byte[] MakeSoundPayload(uint mediaId, byte streamType, uint inMemorySize, uint pluginId, byte sourceFlags = 0)
    {
        using var payload = new MemoryStream();
        WriteUInt32(payload, pluginId);
        payload.WriteByte(streamType);
        WriteUInt32(payload, mediaId);
        WriteUInt32(payload, inMemorySize);
        payload.WriteByte(sourceFlags);
        if ((pluginId & 0x0F) is 2 or 5) WriteUInt32(payload, 0); // empty source-plugin parameter block
        payload.WriteByte(0); // FX override
        payload.WriteByte(0); // FX count
        payload.WriteByte(0); // attachment parameter override
        WriteUInt32(payload, 0); // output bus ID
        WriteUInt32(payload, 0); // parent ID
        payload.WriteByte(0); // parameter override
        payload.WriteByte(0); // initial properties
        payload.WriteByte(0); // ranged properties
        payload.WriteByte(0); // positioning flags
        payload.WriteByte(0); // auxiliary flags
        payload.Write(new byte[6]); // advanced settings
        WriteUInt32(payload, 0); // state groups
        WriteUInt16(payload, 0); // RTPCs
        return payload.ToArray();
    }

    private static byte[] MakeHirc(uint eventId, uint mediaId, uint mediaSize)
    {
        const uint actionId = 0x11110001;
        const uint soundId = 0x22220002;
        using var hirc = new MemoryStream();
        WriteUInt32(hirc, 3);
        WriteHircObject(hirc, 4, eventId, BuildPayload(payload =>
        {
            WriteUInt32(payload, 1);
            WriteUInt32(payload, actionId);
        }));
        WriteHircObject(hirc, 3, actionId, BuildPayload(payload =>
        {
            WriteUInt16(payload, 0x0403); // Play action with game-object scope
            WriteUInt32(payload, soundId);
            payload.WriteByte(0); // target is an object, not an audio bus
            payload.WriteByte(0); // no action properties
            payload.WriteByte(0); // no ranged action properties
            payload.WriteByte(0); // fade-curve vector
            WriteUInt32(payload, 0); // bank/file context
        }));
        WriteHircObject(hirc, 2, soundId, BuildPayload(payload =>
        {
        WriteUInt32(payload, 0x00040001); // Wwise Vorbis source plug-in
            payload.WriteByte(0); // embedded stream mode
            WriteUInt32(payload, mediaId);
            WriteUInt32(payload, mediaSize);
            payload.WriteByte(0); // source flags
            payload.WriteByte(0); // FX override
            payload.WriteByte(0); // FX count
            payload.WriteByte(0); // attachment parameter override
            WriteUInt32(payload, 0); // output bus ID
            WriteUInt32(payload, 0); // parent ID
            payload.WriteByte(0); // parameter override
            payload.WriteByte(0); // initial properties
            payload.WriteByte(0); // ranged properties
            payload.WriteByte(0); // positioning flags
            payload.WriteByte(0); // auxiliary flags
            payload.Write(new byte[6]); // advanced settings
            WriteUInt32(payload, 0); // state groups
            WriteUInt16(payload, 0); // RTPCs
        }));
        return hirc.ToArray();
    }

    private static void WriteHircObject(Stream stream, byte type, uint id, byte[] payload)
    {
        stream.WriteByte(type);
        WriteUInt32(stream, checked((uint)(payload.Length + 4)));
        WriteUInt32(stream, id);
        stream.Write(payload);
    }

    internal static byte[] MakeDialogueMedia(string name, byte[] wem)
    {
        const int nameOffset = 49;
        using var stream = new MemoryStream();
        stream.Write(new byte[nameOffset]);
        var bytes = stream.GetBuffer();
        Encoding.ASCII.GetBytes("QZIP\0CSNDDATA").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(13, 4), 0);
        // Fill the fixed-size metadata area up to the name length field.
        stream.Position = nameOffset;
        WriteUInt32(stream, checked((uint)name.Length));
        stream.Write(Encoding.UTF8.GetBytes(name));
        WriteUInt32(stream, checked((uint)wem.Length));
        stream.Write(wem);
        stream.Write(new byte[40]); // enclosing resource trailer, outside the WEM length
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(17, 4), checked((uint)(result.Length - 21)));
        return result;
    }

    private static byte[] MakeEvent(uint id, string name)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
        {
            WriteSizedAscii(writer, id.ToString());
            WriteSizedAscii(writer, name);
        }
        using var result = new MemoryStream();
        result.Write(Encoding.ASCII.GetBytes("QZIP\0CSNDEVNT"));
        WriteUInt32(result, 1);
        WriteUInt32(result, checked((uint)payload.Length));
        payload.Position = 0;
        payload.CopyTo(result);
        return result.ToArray();
    }

    private static byte[] MakeWrappedEvent(uint id, string name)
    {
        using var fields = new MemoryStream();
        using (var writer = new BinaryWriter(fields, Encoding.ASCII, leaveOpen: true))
        {
            WriteSizedAscii(writer, id.ToString());
            WriteSizedAscii(writer, name);
        }
        using var eventRecord = new MemoryStream();
        eventRecord.Write(Encoding.ASCII.GetBytes("CSNDEVNT"));
        WriteUInt32(eventRecord, 0);
        WriteUInt32(eventRecord, checked((uint)fields.Length));
        fields.Position = 0;
        fields.CopyTo(eventRecord);
        using var trailing = new MemoryStream();
        trailing.Write(Encoding.ASCII.GetBytes("LOADCONT"));
        WriteUInt32(trailing, 2);
        WriteUInt32(trailing, 0);
        eventRecord.Position = 0;
        eventRecord.CopyTo(trailing);
        return MakeComContainer([(1022u, 44u)], trailing.ToArray());
    }

    private static byte[] MakeLocalization(uint version, string language, string key, string text)
    {
        using var languageData = new MemoryStream();
        WriteUInt32(languageData, 0x00000301);
        languageData.WriteByte(0);
        languageData.Write(Encoding.ASCII.GetBytes(language));
        WriteUInt32(languageData, 1);
        WriteUInt32(languageData, checked((uint)key.Length));
        languageData.Write(Encoding.ASCII.GetBytes(key));
        var textBytes = Encoding.Unicode.GetBytes(text);
        WriteUInt32(languageData, checked((uint)textBytes.Length));
        languageData.Write(textBytes);

        using var trailing = new MemoryStream();
        trailing.Write(Encoding.ASCII.GetBytes("LOCALIZ_"));
        WriteUInt32(trailing, version);
        WriteUInt32(trailing, checked((uint)languageData.Length));
        if (version == 5) trailing.WriteByte(0);
        WriteUInt32(trailing, 1);
        languageData.Position = 0;
        languageData.CopyTo(trailing);
        return MakeComContainer([], trailing.ToArray());
    }

    private static byte[] MakePointerLocalization()
    {
        using var languageData = new MemoryStream();
        WriteUInt32(languageData, 0x00000301);
        languageData.WriteByte(0);
        languageData.Write(Encoding.ASCII.GetBytes("ENG"));
        WriteUInt32(languageData, 1); // multi-key pointer record marker
        WriteUInt32(languageData, 1); // one linked key
        WriteUInt32(languageData, 4);
        languageData.Write(Encoding.ASCII.GetBytes("KEY1"));
        languageData.WriteByte(0); // null pointer form
        WriteUInt32(languageData, 1);
        WriteUInt32(languageData, 0); // end of this language's records

        using var trailing = new MemoryStream();
        trailing.Write(Encoding.ASCII.GetBytes("LOCALIZ_"));
        WriteUInt32(trailing, 6);
        WriteUInt32(trailing, checked((uint)languageData.Length));
        WriteUInt32(trailing, 1);
        languageData.Position = 0;
        languageData.CopyTo(trailing);
        return MakeComContainer([(1022u, 99u)], trailing.ToArray());
    }

    private static byte[] MakeComContainer((uint Type, uint Id)[] references, byte[]? trailing = null)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("COM_CONT"));
        WriteUInt32(stream, 6);
        WriteUInt32(stream, checked((uint)(4 + references.Length * 9)));
        WriteUInt32(stream, checked((uint)references.Length));
        foreach (var (type, id) in references)
        {
            WriteUInt32(stream, type);
            WriteUInt32(stream, id);
            stream.WriteByte(1);
        }
        if (trailing is not null) stream.Write(trailing);
        return stream.ToArray();
    }

    internal static byte[] MakeWem(byte[] data, uint sampleCount = 96000)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("RIFF"));
        WriteUInt32(stream, 0);
        stream.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        WriteUInt32(stream, 66);
        using var fmt = new MemoryStream();
        WriteUInt16(fmt, 0xFFFF);
        WriteUInt16(fmt, 2);
        WriteUInt32(fmt, 48000);
        WriteUInt32(fmt, 192000);
        WriteUInt16(fmt, 4);
        WriteUInt16(fmt, 16);
        fmt.Write(new byte[8]);
        WriteUInt32(fmt, sampleCount);
        fmt.Write(new byte[38]);
        fmt.Position = 0;
        fmt.CopyTo(stream);
        stream.Write(Encoding.ASCII.GetBytes("data"));
        WriteUInt32(stream, checked((uint)data.Length));
        stream.Write(data);
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)(result.Length - 8)));
        return result;
    }

    internal static byte[] MakePcmWem(byte[] data, uint sampleCount = 96000)
    {
        var result = MakeWem(data, sampleCount);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), 1);
        return result;
    }

    private static byte[] BuildPayload(Action<MemoryStream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string tag, byte[] payload)
    {
        stream.Write(Encoding.ASCII.GetBytes(tag));
        WriteUInt32(stream, checked((uint)payload.Length));
        stream.Write(payload);
    }

    private static void WriteSizedAscii(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    internal static uint Fnv1(string value)
    {
        var hash = 2166136261u;
        foreach (var item in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash = unchecked(hash * 16777619u);
            hash ^= item;
        }
        return hash;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
