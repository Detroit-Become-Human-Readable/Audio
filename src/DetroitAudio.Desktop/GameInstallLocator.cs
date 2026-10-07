using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DetroitAudio.Desktop;

public sealed record GameInstallation(string Name, string Path);

public static class GameInstallLocator
{
    public static bool IsValidSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            if (Directory.Exists(source)) return File.Exists(Path.Combine(source, "BigFile_PC.idx"));
            if (!File.Exists(source)) return false;
            var extension = Path.GetExtension(source).ToLowerInvariant();
            return extension is ".idx" or ".bnk" or ".wem" or ".mid" or ".midi" or ".dat" || extension.Length == 4 && extension[1] == 'd' && char.IsDigit(extension[2]) && char.IsDigit(extension[3]);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }

    public static IReadOnlyList<GameInstallation> Find(string? rememberedSource = null)
    {
        var results = new List<GameInstallation>();
        void Add(string name, string? path)
        {
            if (path is null || !IsValidSource(path)) return;
            path = Path.GetFullPath(path);
            if (!results.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) results.Add(new(name, path));
        }
        Add("Recent", rememberedSource);
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is string path) steamRoots.Add(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFiles)) steamRoots.Add(Path.Combine(programFiles, "Steam"));
        foreach (var steamRoot in steamRoots.ToArray())
        {
            try
            {
                var libraries = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraries) && new FileInfo(libraries).Length < 1024 * 1024)
                    foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s*\"([^\"]+)\"")) steamRoots.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var steamRoot in steamRoots)
        {
            try
            {
                var steamApps = Path.Combine(steamRoot, "steamapps");
                var manifest = Path.Combine(steamApps, "appmanifest_1222140.acf");
                if (File.Exists(manifest) && new FileInfo(manifest).Length < 1024 * 1024)
                {
                    var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
                    if (match.Success && !match.Groups[1].Value.Contains("..")) Add("Steam", Path.Combine(steamApps, "common", match.Groups[1].Value));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        try
        {
            var epicManifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (Directory.Exists(epicManifests))
            {
                foreach (var file in Directory.EnumerateFiles(epicManifests, "*.item"))
                {
                    if (new FileInfo(file).Length > 1024 * 1024) continue;
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(file));
                        if (json.RootElement.TryGetProperty("InstallLocation", out var location)) Add("Epic Games", location.GetString());
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return results;
    }
}
