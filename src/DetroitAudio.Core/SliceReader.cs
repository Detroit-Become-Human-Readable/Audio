namespace DetroitAudio.Core;

public static class SliceReader
{
    public static void Validate(DataSlice slice)
    {
        if (slice.Offset < 0 || slice.Length < 0 || slice.Offset > new FileInfo(slice.FilePath).Length || slice.Length > new FileInfo(slice.FilePath).Length - slice.Offset)
            throw new InvalidDataException("The requested data extends beyond the source file.");
    }

    public static byte[] Read(DataSlice slice, int maximum = 64 * 1024 * 1024)
    {
        Validate(slice);
        if (slice.Length > maximum) throw new InvalidDataException($"The metadata block exceeds the {maximum / 1048576} MB limit.");
        using var file = File.OpenHandle(slice.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[checked((int)slice.Length)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = RandomAccess.Read(file, bytes.AsSpan(read), checked(slice.Offset + read));
            if (count == 0) throw new EndOfStreamException("The source file ended during a read.");
            read += count;
        }
        return bytes;
    }

    public static async Task CopyAsync(DataSlice slice, Stream destination, CancellationToken cancellationToken = default)
    {
        Validate(slice);
        await using var source = new FileStream(slice.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        source.Position = slice.Offset;
        var remaining = slice.Length;
        var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0) throw new EndOfStreamException("The source file ended during export.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }
}
