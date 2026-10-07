using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DetroitAudio.Audio;
using Microsoft.Win32;
using System.Windows;
using System.Net.Http;

namespace DetroitAudio.Desktop;

public sealed partial class WelcomeViewModel : ObservableObject, IDisposable
{
    private readonly Window owner;
    private readonly UserSettings settings;
    private readonly ToolInstaller installer = new();
    private CancellationTokenSource? setupCancellation;
    private Task? initialization;
    private string toolsRoot = "";
    private bool disposed;
    public ObservableCollection<GameInstallation> Installations { get; } = [];
    public ObservableCollection<ToolStatus> Tools { get; } = [];
    [ObservableProperty] private string selectedSource = "";
    [ObservableProperty] private GameInstallation? selectedInstallation;
    [ObservableProperty] private string gameStatus = "Choose a game folder";
    [ObservableProperty] private string toolStatus = "Checking audio tools";
    [ObservableProperty] private string setupError = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool toolsReady;
    [ObservableProperty] private bool indeterminate = true;
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool showAtStartup;
    [ObservableProperty] private bool reduceMotion;
    public string OpenLabel => Directory.Exists(SelectedSource) ? "Open game" : "Open library";

    public WelcomeViewModel(Window owner, UserSettings settings, string? initialSource = null)
    {
        this.owner = owner; this.settings = settings;
        ShowAtStartup = settings.ShowWelcomeAtStartup; ReduceMotion = settings.ReduceMotion;
        SelectedSource = initialSource ?? settings.LastSource;
    }

    public Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        Busy = true;
        setupCancellation = new();
        try
        {
            var token = setupCancellation.Token;
            var installations = await Task.Run(() => GameInstallLocator.Find(SelectedSource), token);
            foreach (var installation in installations) Installations.Add(installation);
            SelectMatchingInstallation();
            if (!GameInstallLocator.IsValidSource(SelectedSource) && Installations.Count > 0) SelectedInstallation = Installations[0];
            foreach (var root in CandidateRoots(settings))
            {
                var states = await installer.GetStatusAsync(root, token);
                if (states.All(item => item.Ready)) { toolsRoot = root; ApplyStates(states); return; }
            }
            toolsRoot = Path.Combine(UserSettings.DataRoot, "tools");
            settings.ToolsDirectory = toolsRoot;
            await InstallAsync(token);
        }
        catch (OperationCanceledException) { await RefreshPartialStatusAsync(); ToolStatus = "Setup paused"; Indeterminate = false; }
        catch (Exception ex) { await RefreshPartialStatusAsync(); SetupError = FriendlyError(ex); ToolStatus = "Setup needs attention"; Indeterminate = false; }
        finally { Busy = false; }
    }

    private static IEnumerable<string> CandidateRoots(UserSettings settings)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ToolsDirectory)) roots.Add(settings.ToolsDirectory);
        roots.Add(Path.Combine(AppContext.BaseDirectory, "tools")); roots.Add(Path.Combine(UserSettings.DataRoot, "tools"));
        return roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task InstallAsync(CancellationToken token)
    {
        SetupError = ""; ToolStatus = "Preparing audio tools"; Indeterminate = true;
        var progress = new Progress<ToolSetupProgress>(p =>
        {
            ToolStatus = p.DisplayName + " · " + p.Phase;
            Indeterminate = p.Total <= 0;
            if (p.Total > 0) Progress = Math.Clamp(p.Completed * 100d / p.Total, 0, 100);
        });
        ApplyStates(await installer.EnsureAsync(toolsRoot, progress, token));
        settings.ToolsDirectory = toolsRoot;
    }

    private void ApplyStates(IReadOnlyList<ToolStatus> states)
    {
        Tools.Clear(); foreach (var state in states) Tools.Add(state);
        ToolsReady = states.All(item => item.Ready); Indeterminate = false; Progress = ToolsReady ? 100 : 0;
        ToolStatus = ToolsReady ? "Audio tools ready" : "Audio tools need setup";
        if (ToolsReady && toolsRoot.Length > 0) { settings.ToolsDirectory = toolsRoot; settings.Save(); }
    }
    private async Task RefreshPartialStatusAsync()
    {
        try
        {
            if (toolsRoot.Length == 0) return;
            var states = await installer.GetStatusAsync(toolsRoot);
            Tools.Clear(); foreach (var state in states) Tools.Add(state); ToolsReady = states.All(state => state.Ready);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
    }

    private bool CanRetry() => !Busy;
    [RelayCommand(CanExecute = nameof(CanRetry))] private async Task RetryAsync()
    {
        Busy = true; setupCancellation?.Dispose(); setupCancellation = new();
        try { if (toolsRoot.Length == 0) toolsRoot = Path.Combine(UserSettings.DataRoot, "tools"); await InstallAsync(setupCancellation.Token); }
        catch (OperationCanceledException) { await RefreshPartialStatusAsync(); ToolStatus = "Setup paused"; Indeterminate = false; }
        catch (Exception ex) { await RefreshPartialStatusAsync(); SetupError = FriendlyError(ex); ToolStatus = "Setup needs attention"; Indeterminate = false; }
        finally { Busy = false; }
    }

    [RelayCommand] private void BrowseGame()
    {
        var dialog = new OpenFolderDialog { Title = "Choose game folder" };
        if (dialog.ShowDialog(owner) == true) SelectedSource = dialog.FolderName;
    }
    [RelayCommand] private void BrowseFile()
    {
        var dialog = new OpenFileDialog { Title = "Open archive", Filter = "Audio archives|*.idx;*.bnk;*.wem;*.mid;*.midi;*.dat;*.d??|All files|*.*" };
        if (dialog.ShowDialog(owner) == true) SelectedSource = dialog.FileName;
    }
    [RelayCommand] public void Cancel() => setupCancellation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanRetry))] private void BrowseToolsFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Audio tools folder" };
        if (dialog.ShowDialog(owner) == true) { toolsRoot = dialog.FolderName; settings.ToolsDirectory = toolsRoot; }
    }
    public bool CanOpen => GameInstallLocator.IsValidSource(SelectedSource) && !Busy;
    public void Save()
    {
        settings.ShowWelcomeAtStartup = ShowAtStartup; settings.ReduceMotion = ReduceMotion;
        if (GameInstallLocator.IsValidSource(SelectedSource)) settings.LastSource = Path.GetFullPath(SelectedSource);
        if (ToolsReady && toolsRoot.Length > 0) settings.ToolsDirectory = toolsRoot;
        settings.Save();
    }
    partial void OnBusyChanged(bool value) { OnPropertyChanged(nameof(CanOpen)); RetryCommand.NotifyCanExecuteChanged(); BrowseToolsFolderCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedSourceChanged(string value)
    {
        SelectMatchingInstallation();
        GameStatus = string.IsNullOrWhiteSpace(value) ? "Choose a game folder" : GameInstallLocator.IsValidSource(value) ? "Ready" : "Choose a folder containing BigFile_PC.idx";
        OnPropertyChanged(nameof(CanOpen)); OnPropertyChanged(nameof(OpenLabel));
    }
    private void SelectMatchingInstallation()
    {
        SelectedInstallation = Installations.FirstOrDefault(installation => string.Equals(installation.Path.TrimEnd('\\', '/'), SelectedSource.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }
    partial void OnSelectedInstallationChanged(GameInstallation? value) { if (value is not null) SelectedSource = value.Path; }
    private static string FriendlyError(Exception error) => error switch
    {
        HttpRequestException => "The tools could not be downloaded. Check your connection and retry.",
        UnauthorizedAccessException => "The tools folder is not writable. Choose another folder.",
        IOException => error.Message.Split('\n')[0],
        _ => error.Message.Split('\n')[0]
    };
    public async Task CancelAndWaitAsync()
    {
        Cancel();
        await Task.WhenAll(new[] { initialization, RetryCommand.ExecutionTask }.OfType<Task>());
    }
    public void Dispose() { if (disposed) return; disposed = true; setupCancellation?.Cancel(); setupCancellation?.Dispose(); installer.Dispose(); }
}
