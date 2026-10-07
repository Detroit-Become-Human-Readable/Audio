using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;

namespace DetroitAudio.Desktop;

public static class ThemeManager
{
    private const string ThemeDictionaryPrefix = "Themes/Theme.";
    private static Dispatcher? dispatcher;
    private static string preference = "System";

    public static string Preference => preference;

    public static void Initialize(string savedPreference, Dispatcher uiDispatcher)
    {
        dispatcher = uiDispatcher;
        preference = Normalize(savedPreference);
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply();
    }

    public static void SetPreference(string value)
    {
        preference = Normalize(value);
        Apply();
    }

    public static string ResolveTheme(string requested, bool highContrast, bool? systemLight)
    {
        if (highContrast) return "HighContrast";
        return Normalize(requested) switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => (systemLight ?? true) ? "Light" : "Dark"
        };
    }

    public static bool IsReducedMotion(bool userPreference) => userPreference || !SystemParameters.ClientAreaAnimation;

    private static string Normalize(string? value) => value is "Light" or "Dark" ? value : "System";

    private static bool? ReadSystemLightPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int appTheme) return appTheme != 0;
            if (key?.GetValue("SystemUsesLightTheme") is int systemTheme) return systemTheme != 0;
            return null;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)) return;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(Apply, DispatcherPriority.ApplicationIdle);
    }

    private static void Apply()
    {
        if (Application.Current is not { } app || dispatcher?.HasShutdownStarted == true) return;
        var resolved = ResolveTheme(preference, SystemParameters.HighContrast, ReadSystemLightPreference());
        var assemblyName = Uri.EscapeDataString(typeof(App).Assembly.GetName().Name!);
        var source = new Uri($"pack://application:,,,/{assemblyName};component/{ThemeDictionaryPrefix}{resolved}.xaml", UriKind.Absolute);
        var dictionaries = app.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains(ThemeDictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = source };
        if (current is null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(current)] = replacement;
    }
}
