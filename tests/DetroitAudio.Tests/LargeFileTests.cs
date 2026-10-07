using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DetroitAudio.Core;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class LargeFileTests
{
    [Fact] public async Task SliceOffsetsBeyondFourGigabytesAreNotTruncated()
    {
        var path = Path.Combine(Path.GetTempPath(), "DetroitAudio-large-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                if (OperatingSystem.IsWindows()) Assert.True(DeviceIoControl(file.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "The temporary file could not be made sparse.");
                file.Position = (long)uint.MaxValue + 1024;
                await file.WriteAsync(new byte[] { 19, 29, 39 });
            }
            using var output = new MemoryStream(); await SliceReader.CopyAsync(new(path, (long)uint.MaxValue + 1024, 3), output); Assert.Equal(new byte[] { 19, 29, 39 }, output.ToArray());
            Assert.Throws<InvalidDataException>(() => SliceReader.Read(new(path, (long)uint.MaxValue + 1025, 3)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
