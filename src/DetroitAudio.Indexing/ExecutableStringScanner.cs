using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DetroitAudio.Indexing;

public sealed record ExecutableString(string Text, string Section, long Offset, string InputSha256);

/// <summary>Extracts complete identifier-like strings from non-executable PE data sections.</summary>
public static partial class ExecutableStringScanner
{
    private const int MaximumSections = 96;
    private const int MaximumImageBytes = 512 * 1024 * 1024;

    public static IReadOnlyList<ExecutableString> Read(string path, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (stream.Length is < 64 or > MaximumImageBytes) return [];
        var image = new byte[checked((int)stream.Length)];
        var read = 0;
        while (read < image.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(image, read, Math.Min(1024 * 1024, image.Length - read));
            if (count == 0) return [];
            read += count;
        }
        var sha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        return Read(image, sha256, token);
    }

    internal static IReadOnlyList<ExecutableString> Read(ReadOnlySpan<byte> image, string sha256, CancellationToken token = default)
    {
        if (!TryGetDataSections(image, out var sections)) return [];
        var result = new List<ExecutableString>();
        foreach (var section in sections)
        {
            token.ThrowIfCancellationRequested();
            ScanAscii(image.Slice(section.Offset, section.Length), section, sha256, result, token);
            ScanUtf16(image.Slice(section.Offset, section.Length), section, sha256, result, token);
        }
        return result;
    }

    private static bool TryGetDataSections(ReadOnlySpan<byte> image, out List<Section> sections)
    {
        sections = [];
        if (image.Length < 64 || image[0] != (byte)'M' || image[1] != (byte)'Z') return false;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(0x3C, 4));
        if (peOffset < 64 || peOffset > image.Length - 24 || !image.Slice(peOffset, 4).SequenceEqual("PE\0\0"u8)) return false;
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(peOffset + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(peOffset + 20, 2));
        if (sectionCount is 0 or > MaximumSections) return false;
        var table = (long)peOffset + 24 + optionalSize;
        if (table > image.Length || (long)sectionCount * 40 > image.Length - table) return false;
        for (var index = 0; index < sectionCount; index++)
        {
            var header = image.Slice(checked((int)table + index * 40), 40);
            var nameEnd = header[..8].IndexOf((byte)0);
            if (nameEnd < 0) nameEnd = 8;
            var name = Encoding.ASCII.GetString(header[..nameEnd]);
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
            var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(36, 4));
            const uint executable = 0x20000000;
            if ((characteristics & executable) != 0 || !IsDataSection(name) || rawSize == 0 || rawOffset > image.Length || rawSize > image.Length - rawOffset) continue;
            sections.Add(new(name, checked((int)rawOffset), checked((int)rawSize)));
        }
        return true;
    }

    private static bool IsDataSection(string name) => name.Equals(".rdata", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".rodata", StringComparison.OrdinalIgnoreCase) || name.Equals(".data", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".pdata", StringComparison.OrdinalIgnoreCase) || name.Equals(".xdata", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".rsrc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("data", StringComparison.OrdinalIgnoreCase);

    private static void ScanAscii(ReadOnlySpan<byte> bytes, Section section, string sha256, List<ExecutableString> result, CancellationToken token)
    {
        var start = 0;
        for (var position = 0; position <= bytes.Length; position++)
        {
            if ((position & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            if (position < bytes.Length && bytes[position] is >= 0x20 and <= 0x7E) continue;
            if (position - start is >= 2 and <= 256 && position < bytes.Length && bytes[position] == 0)
                Add(Encoding.ASCII.GetString(bytes.Slice(start, position - start)), section, start, sha256, result);
            start = position + 1;
        }
    }

    private static void ScanUtf16(ReadOnlySpan<byte> bytes, Section section, string sha256, List<ExecutableString> result, CancellationToken token)
    {
        for (var parity = 0; parity < 2; parity++)
        {
            var position = parity;
            while (position + 3 < bytes.Length)
            {
                if ((position & 0xFFFF) == parity) token.ThrowIfCancellationRequested();
                var start = position;
                if (!IsUtf16Start(bytes, start)) { position += 2; continue; }
                while (position + 1 < bytes.Length && bytes[position] is >= 0x20 and <= 0x7E && bytes[position + 1] == 0)
                {
                    position += 2;
                    if ((position & 0xFFFF) == parity) token.ThrowIfCancellationRequested();
                }
                var terminated = position + 1 < bytes.Length && bytes[position] == 0 && bytes[position + 1] == 0;
                if (position - start is >= 4 and <= 512 && terminated)
                {
                    Add(Encoding.Unicode.GetString(bytes.Slice(start, position - start)), section, start, sha256, result);
                    position += 2;
                }
                else position = Math.Max(position, start + 2);
            }
        }
    }

    private static bool IsUtf16Start(ReadOnlySpan<byte> bytes, int start)
    {
        if (start == 0 || start >= 2 && bytes[start - 1] == 0 && bytes[start - 2] == 0) return true;
        if (bytes[start - 1] != 0) return false;
        var cursor = start - 2;
        var precedingAsciiLength = 0;
        while (cursor >= 0 && bytes[cursor] is >= 0x20 and <= 0x7E)
        {
            precedingAsciiLength++;
            cursor--;
        }
        return precedingAsciiLength >= 2;
    }

    private static void Add(string text, Section section, int relativeOffset, string sha256, List<ExecutableString> result)
    {
        if (text.Length is < 2 or > 256 || !Identifier().IsMatch(text)) return;
        result.Add(new(text, section.Name, (long)section.Offset + relativeOffset, sha256));
    }

    [GeneratedRegex(@"\A[\p{L}_][\p{L}\p{N}_.-]{1,255}\z")]
    private static partial Regex Identifier();

    private readonly record struct Section(string Name, int Offset, int Length);
}
