using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace DetroitAudio.Desktop.Tests;

public sealed class ApplicationMetadataTests
{
    [Fact]
    public void CompiledApplicationRetainsProductDetailsAndReleaseVersion()
    {
        var assembly = typeof(App).Assembly;
        var versionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);

        Assert.Equal("Detroit Audio Extractor", versionInfo.FileDescription);
        Assert.Equal("Detroit Audio Extractor", versionInfo.ProductName);
        Assert.Equal("Detroit: Become Human Readable", versionInfo.CompanyName);
        Assert.Equal("1.0.0.0", versionInfo.FileVersion);
        Assert.Equal("1.0.0", versionInfo.ProductVersion);
        Assert.Equal(new Version(1, 0, 0, 0), assembly.GetName().Version);
        Assert.Equal("1.0.0", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        Assert.Equal("Tool to extract audio files from the game \"Detroit: Become Human\"",
            assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description);
    }
}
