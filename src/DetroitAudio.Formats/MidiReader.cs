using System.Buffers.Binary;
using System.Text;
using DetroitAudio.Core;

namespace DetroitAudio.Formats;

public sealed record MidiDocument(DataSlice Slice, MidiMetadata Metadata, IReadOnlyList<NameEvidence> Names);

/// <summary>Reads bounded Standard MIDI Files and Detroit's uncompressed RAW_FILE MIDI wrapper.</summary>
public static class MidiReader
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public static ReadOnlySpan<byte> WrapperMagic => "QZIP\0RAW_FILE"u8;

    public static MidiDocument ReadWrapped(DataSlice source, CancellationToken token = default)
    {
        var header = Read(source with { Length = 33 }, source, token);
        if (!header.AsSpan(0, 13).SequenceEqual(WrapperMagic) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(13)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(21)) != 4 || !header.AsSpan(25, 4).SequenceEqual("MIDI"u8))
            throw new InvalidDataException("Unsupported MIDI wrapper.");
        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(17));
        var midiLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(29));
        if (midiLength > MaximumBytes || midiLength < 14 || (long)midiLength + 12 != rawLength || 21L + rawLength > source.Length)
            throw new InvalidDataException("MIDI wrapper lengths exceed the resource bounds.");
        return ReadStandard(source with { Offset = checked(source.Offset + 33), Length = midiLength }, token);
    }

    public static MidiDocument ReadStandard(DataSlice source, CancellationToken token = default)
    {
        if (source.Length < 14 || source.Length > MaximumBytes) throw new InvalidDataException("MIDI file size is outside the supported bounds.");
        var data = Read(source, source, token);
        if (!data.AsSpan(0, 4).SequenceEqual("MThd"u8)) throw new InvalidDataException("The MIDI header is missing.");
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (headerLength < 6 || 8L + headerLength > data.Length) throw new InvalidDataException("The MIDI header is truncated.");
        var format = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8));
        var tracks = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
        var division = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12));
        var frameRate = unchecked((sbyte)(division >> 8));
        if (format > 2 || tracks == 0 || tracks > 4096 || format == 0 && tracks != 1 || division == 0 ||
            (division & 0x8000) != 0 && (frameRate is not (-24 or -25 or -29 or -30) || (division & 0xFF) == 0))
            throw new InvalidDataException("The MIDI format, track count or time division is invalid.");
        var position = checked(8 + (int)headerLength); var found = 0; var names = new List<NameEvidence>();
        while (position < data.Length)
        {
            token.ThrowIfCancellationRequested();
            if (data.Length - position < 8) throw new InvalidDataException("A MIDI chunk header is truncated.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4));
            if (length > data.Length - position - 8) throw new InvalidDataException("A MIDI chunk exceeds the file bounds.");
            var end = checked(position + 8 + (int)length);
            if (data.AsSpan(position, 4).SequenceEqual("MTrk"u8))
            {
                if (++found > tracks) throw new InvalidDataException("The MIDI file contains more tracks than declared.");
                ReadTrack(data, position + 8, end, found - 1, source, names, token);
            }
            else if (data.AsSpan(position, 4).SequenceEqual("MThd"u8)) throw new InvalidDataException("The MIDI file contains a second header.");
            position = end;
        }
        if (found != tracks) throw new InvalidDataException("The MIDI file is missing a declared track.");
        return new(source, new(format, tracks, division), names);
    }

    private static void ReadTrack(byte[] data, int position, int end, int track, DataSlice source, List<NameEvidence> names, CancellationToken token)
    {
        byte running = 0; var events = 0; var ended = false;
        while (position < end)
        {
            if ((events++ & 255) == 0) token.ThrowIfCancellationRequested();
            ReadVlq(data, ref position, end);
            if (position >= end) throw new InvalidDataException("A MIDI event is truncated.");
            var status = data[position];
            if (status >= 0x80) position++;
            else { if (running == 0) throw new InvalidDataException("MIDI running status has no channel message."); status = running; }
            if (status < 0xF0)
            {
                running = status; var count = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                if (end - position < count) throw new InvalidDataException("A MIDI channel message is truncated.");
                for (var i = 0; i < count; i++) if (data[position++] >= 0x80) throw new InvalidDataException("Invalid MIDI channel data.");
                continue;
            }
            running = 0;
            if (status is 0xF0 or 0xF7)
            {
                var length = ReadVlq(data, ref position, end); Skip(length); continue;
            }
            if (status != 0xFF || position >= end) throw new InvalidDataException("Invalid MIDI event status.");
            var type = data[position++]; if (type >= 0x80) throw new InvalidDataException("Invalid MIDI meta-event type.");
            var size = ReadVlq(data, ref position, end);
            if (size > end - position) throw new InvalidDataException("A MIDI meta-event exceeds its track.");
            if (type == 3 && size is > 0 and <= 4096 && names.Count < 256)
            {
                string text;
                try { text = new UTF8Encoding(false, true).GetString(data, position, (int)size); }
                catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(data, position, (int)size); }
                text = new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim();
                if (text.Length > 0) names.Add(new(text, "MidiTrack", (uint)track, NameKind.Stored, source.FilePath, source.Offset + position));
            }
            if (type == 0x2F)
            {
                if (size != 0 || position != end) throw new InvalidDataException("MIDI end-of-track does not match its chunk boundary.");
                ended = true; break;
            }
            Skip(size);
        }
        if (!ended) throw new InvalidDataException("The MIDI track has no end-of-track event.");
        void Skip(uint count) { if (count > end - position) throw new InvalidDataException("A MIDI event exceeds its track."); position += (int)count; }
    }

    private static uint ReadVlq(byte[] data, ref int position, int end)
    {
        uint value = 0;
        for (var count = 0; count < 4; count++)
        {
            if (position >= end) throw new InvalidDataException("A MIDI variable-length value is truncated.");
            var item = data[position++]; value = (value << 7) | (uint)(item & 0x7F);
            if ((item & 0x80) == 0) return value;
        }
        throw new InvalidDataException("A MIDI variable-length value exceeds four bytes.");
    }

    private static byte[] Read(DataSlice requested, DataSlice owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (requested.Length < 0 || requested.Length > MaximumBytes || requested.Offset < owner.Offset || requested.Offset - owner.Offset > owner.Length - requested.Length)
            throw new InvalidDataException("The MIDI extent exceeds its resource.");
        using var file = new FileStream(requested.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        if (requested.Offset < 0 || requested.Offset > file.Length - requested.Length) throw new InvalidDataException("The MIDI extent exceeds its source file.");
        file.Position = requested.Offset; var bytes = new byte[(int)requested.Length];
        for (var position = 0; position < bytes.Length;)
        {
            token.ThrowIfCancellationRequested(); var read = file.Read(bytes, position, Math.Min(65536, bytes.Length - position));
            if (read == 0) throw new EndOfStreamException("The MIDI source changed during reading."); position += read;
        }
        return bytes;
    }
}
