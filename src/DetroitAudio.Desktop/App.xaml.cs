using System.Windows;
using DetroitAudio.Audio;

namespace DetroitAudio.Desktop;

public partial class App : Application
{
    public bool InteractiveStartup { get; init; } = true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!InteractiveStartup) return;
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "Detroit Audio", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        var settings = UserSettings.Load();
        ThemeManager.Initialize(settings.ThemePreference, Dispatcher);
        if (e.Args.Contains("--smoke-test"))
        {
            var window = new MainWindow(); MainWindow = window; window.Show();
            _ = window.Dispatcher.InvokeAsync(() =>
            {
                var ok = window.Title == "Detroit Audio Browser" && window.FindName("MediaGrid") is not null && window.FindName("LibraryTree") is not null;
                window.Close(); Shutdown(ok ? 0 : 2);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Contains("--smoke-welcome"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var check = new WelcomeWindow(settings); MainWindow = check; check.Show();
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await check.ViewModel.InitializeAsync();
                    var ok = check.ViewModel.ToolsReady && check.FindName("WelcomeLogo") is not null && check.FindName("GameFolderBox") is not null;
                    check.Close(); Shutdown(ok ? 0 : 2);
                }
                catch { check.Close(); Shutdown(2); }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        var path = e.Args.FirstOrDefault(value => !value.StartsWith("--")) ?? settings.LastSource;
        if (!settings.ShowWelcomeAtStartup && GameInstallLocator.IsValidSource(path))
        {
            try
            {
                using var installer = new ToolInstaller();
                var roots = new[] { settings.ToolsDirectory, Path.Combine(AppContext.BaseDirectory, "tools"), Path.Combine(UserSettings.DataRoot, "tools") }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                    if ((await installer.GetStatusAsync(root)).All(tool => tool.Ready)) { settings.ToolsDirectory = root; settings.Save(); OpenBrowser(path); return; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var welcome = new WelcomeWindow(settings, path); MainWindow = welcome;
        welcome.OpenRequested += (_, _) => { OpenBrowser(welcome.SelectedSource); welcome.Close(); };
        welcome.Closed += (_, _) => { if (ReferenceEquals(MainWindow, welcome)) Shutdown(); };
        welcome.Show();
    }
    private void OpenBrowser(string path)
    {
        var window = new MainWindow(); MainWindow = window; ShutdownMode = ShutdownMode.OnMainWindowClose; window.Show();
        if (GameInstallLocator.IsValidSource(path)) _ = window.ViewModel.OpenAsync(path);
    }
}
