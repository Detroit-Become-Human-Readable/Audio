namespace DetroitAudio.Audio;

/// <summary>A pinned component definition embedded in the application.</summary>
public sealed record ToolComponentSpec(
    string Name,
    string Version,
    string Archive,
    string Sha256,
    IReadOnlyList<ToolFileSpec> Files,
    string? Url = null,
    string? SeedArchive = null,
    string? SeedPath = null,
    string? DisplayName = null);

/// <summary>A required file and its pinned SHA-256 digest.</summary>
public sealed record ToolFileSpec(string Path, string Sha256);

public sealed record ToolStatus(string Name, string DisplayName, string Version, bool Ready, string? Issue = null);

public sealed record ToolSetupProgress(string Component, string DisplayName, string Phase, long Completed = 0, long Total = 0);

public sealed record ToolSetupResult(IReadOnlyList<ToolStatus> Components)
{
    public bool Ready => Components.All(component => component.Ready);
}
