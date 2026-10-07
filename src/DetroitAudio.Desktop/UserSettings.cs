using System.Text.Json;

namespace DetroitAudio.Desktop;

public sealed class UserSettings
{
    public string ToolsDirectory { get; set; } = "";
    public string CacheDirectory { get; set; } = Path.Combine(DataRoot, "cache");
    public string LastExportDirectory { get; set; } = "";
    public string LastSource { get; set; } = "";
    public string WavFormat { get; set; } = "Pcm16";
    public float Volume { get; set; } = .75f;
    public bool ShowWelcomeAtStartup { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public string ThemePreference { get; set; } = "System";
    public bool GroupSimilarAudio { get; set; }
    public static string DataRoot => Environment.GetEnvironmentVariable("DETROITAUDIO_DATA_ROOT") is { Length: > 0 } configured ? Path.GetFullPath(configured) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DetroitAudio");
    private static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public static UserSettings Load()
    {
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    }
}
