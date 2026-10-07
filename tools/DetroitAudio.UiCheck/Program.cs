using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DetroitAudio.Core;
using DetroitAudio.Desktop;
using DetroitAudio.Indexing;
using DetroitAudio.Audio;
using DetroitAudio.Export;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using MediaState = DetroitAudio.Core.MediaState;

namespace DetroitAudio.UiCheck;

internal static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        if (args is ["--playback", var wavPath, var toolsPath]) return VerifyDevice(wavPath, toolsPath);
        if (args is ["--browse", var sourcePath, var cachePath, var browseOutput]) return VerifyCachedBrowser(sourcePath, cachePath, browseOutput);
        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "ui-check")); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DETROITAUDIO_DATA_ROOT", Path.Combine(output, "settings"));
        var app = new App { InteractiveStartup = false }; app.InitializeComponent();
        app.DispatcherUnhandledException += (_, args) => { Console.Error.WriteLine(args.Exception); Environment.Exit(1); };
        ThemeManager.Initialize("System", app.Dispatcher);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(error => throw new InvalidOperationException("Browser operation failed.", error)) { Width = 1370, Height = 860, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        window.Show();
        var model = window.ViewModel;
        var bank = new BankInfo { Key = "bank-a", Id = 300, Name = "Ambient", Version = 120, Slice = new("sample.bnk", 0, 128) };
        var catalog = new AudioCatalog { Fingerprint = "synthetic-ui", Banks = [bank] };
        var samplePath = Path.Combine(output, "sample.dat"); File.WriteAllBytes(samplePath, new byte[32 * 1024]);
        foreach (var (name, index) in new[] { "Rain", "Wind", "Footsteps", "Water", "Birds", "Traffic", "Music", "Door", "Room tone", "Dialogue" }.Select((name, index) => (name, index)))
            catalog.Media.Add(new() { Key = "media-" + index, Id = (uint)(1000 + index), Name = name, BankKey = bank.Key, BankName = bank.Name, Language = index == 9 ? "ENG" : "SFX", Channels = index % 2 + 1, SampleRate = 48000, Codec = "Vorbis", Duration = 12 + index * 4, Slice = new(samplePath, index * 1024, 2048 + index * 512), IsRiff = true, Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid, Availability = SourceAvailability.Available });
        catalog.Events.Add(new() { Key = "event-a", Id = 500, Name = "Play_Ambient", BankKey = bank.Key, Behavior = EventBehavior.Playback, LinkStatus = EventLinkStatus.Resolved, MediaKeys = [catalog.Media[0].Key] });
        using var store = new CatalogStore(Path.Combine(output, "catalog")); store.Save(catalog, null, default);
        typeof(MainViewModel).GetField("catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, catalog);
        typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, store);
        typeof(MainViewModel).GetMethod("BuildTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [catalog]);
        model.HasLibrary = true; model.SelectNode(model.QuickViews[0]);
        PumpUntil(() => model.Rows is { Count: 10 } && !model.IsQuerying);
        var firstRow = (VirtualMediaRow)model.Rows![0]!; PumpUntil(() => firstRow.Media is not null); model.SelectedRow = firstRow;
        model.Status = "1 bank · 1 event · 10 entries"; model.Languages.Add("—"); model.Languages.Add("ENG"); model.Categories.Add("Music"); model.Codecs.Add("Vorbis");
        var content = (FrameworkElement)window.Content; content.Measure(new Size(1370, 820)); content.Arrange(new Rect(0, 0, 1370, 820)); content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { })); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1370, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(output, "browser.png"))) encoder.Save(stream);
        var grid = (DataGrid)window.FindName("MediaGrid");
        if (grid.Items.Count != 10 || model.QuickViews.Count != 1 || model.Roots.Count < 2) throw new InvalidOperationException($"Browser controls did not load: grid={grid.Items.Count}, rows={model.Rows?.Count}, source={grid.ItemsSource?.GetType().Name}, same={ReferenceEquals(grid.ItemsSource, model.Rows)}, context={ReferenceEquals(grid.DataContext, model)}, language={model.Language}, category={model.Category}, codec={model.Codec}, querying={model.IsQuerying}.");
        if (!store.Query(new(Search: "rain")).Entries.Any(m => m.Name == "Rain")) throw new InvalidOperationException("Search failed.");
        model.Language = "ENG"; model.Category = "Music"; model.Codec = "Vorbis"; model.SelectNode(model.QuickViews[0]);
        if (model.Language != "All languages" || model.Category != "All types" || model.Codec != "All codecs") throw new InvalidOperationException("All audio retained filters.");
        model.SelectNode(new LibraryNode { Name = "Empty bank", Bank = new BankInfo { Key = "missing-bank" } }); model.Search = "Rain";
        PumpUntil(() => !model.IsQuerying && model.Rows is { Count: 1 });
        model.SearchScope = "Current view"; PumpUntil(() => !model.IsQuerying && model.Rows is { Count: 0 });
        if (model.EmptyText != "No matches") throw new InvalidOperationException("Empty search state is missing.");
        model.Search = ""; model.SearchScope = "All audio"; model.SelectNode(model.QuickViews[0]); PumpUntil(() => !model.IsQuerying && model.Rows is { Count: 10 });
        model.Search = "Wind"; model.Search = "Rain"; PumpUntil(() => !model.IsQuerying && model.Rows is { Count: 1 });
        var match = (VirtualMediaRow)model.Rows![0]!; PumpUntil(() => match.Media is not null);
        if (match.DisplayName != "Rain") throw new InvalidOperationException("An older search replaced the current result.");
        model.SelectAllResults(); if (!model.ResultsText.Contains("all selected")) throw new InvalidOperationException("Select all did not select the complete result.");
        VerifySidebarSelection(window);
        VerifyReplacementRightClick(window, catalog, store);
        VerifyReducedMotionLogo(window);
        VerifyPlaybackEligibility(model, catalog.Media[0]);
        VerifyDialogsAndHover(window, output);
        VerifyTaskWindow(window, output);
        VerifyThemeChanges(window, output);
        VerifyHighContrastSelections(window);
        VerifyVirtualList(window);
        var gameFolder = Path.Combine(output, "game"); Directory.CreateDirectory(gameFolder); File.WriteAllText(Path.Combine(gameFolder, "BigFile_PC.idx"), "synthetic index");
        var welcome = new WelcomeWindow(new UserSettings { ReduceMotion = true }, gameFolder, automaticSetup: false) { Width = 860, Height = 735 };
        welcome.ViewModel.ToolStatus = "Audio tools ready"; welcome.ViewModel.ToolsReady = true; welcome.ViewModel.Indeterminate = false; welcome.ViewModel.Progress = 100;
        foreach (var item in new[] { ("vgmstream", "Audio decoder"), ("ffmpeg", "Audio conversion"), ("ww2ogg", "Vorbis export"), ("revorb", "OGG repair") }) welcome.ViewModel.Tools.Add(new(item.Item1, item.Item2, "test", true));
        welcome.StopAnimations();
        var welcomeContent = (FrameworkElement)welcome.Content; welcomeContent.Measure(new Size(860, 696)); welcomeContent.Arrange(new Rect(0, 0, 860, 696)); welcomeContent.UpdateLayout();
        var welcomeBitmap = new RenderTargetBitmap(860, 696, 96, 96, PixelFormats.Pbgra32); welcomeBitmap.Render(welcomeContent);
        var welcomeEncoder = new PngBitmapEncoder(); welcomeEncoder.Frames.Add(BitmapFrame.Create(welcomeBitmap)); using (var stream = File.Create(Path.Combine(output, "welcome.png"))) welcomeEncoder.Save(stream);
        if (!welcome.ViewModel.CanOpen || !((Button)welcome.FindName("OpenLibraryButton")).IsEnabled) throw new InvalidOperationException("Welcome did not accept a valid game folder.");
        welcome.ViewModel.SelectedSource = Path.Combine(output, "missing-game");
        if (welcome.ViewModel.CanOpen) throw new InvalidOperationException("Welcome accepted a missing game folder.");
        welcome.ViewModel.Dispose();
        window.Close(); Console.WriteLine("WPF browser rendered; global search, scope, filter reset, and 100,123-row virtualization passed."); return 0;
    }

    private static int VerifyCachedBrowser(string sourcePath, string cachePath, string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DETROITAUDIO_DATA_ROOT", Path.Combine(output, "settings"));
        new UserSettings { CacheDirectory = Path.GetFullPath(cachePath), ToolsDirectory = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR") ?? "", ShowWelcomeAtStartup = false }.Save();
        var app = new App { InteractiveStartup = false }; app.InitializeComponent();
        app.DispatcherUnhandledException += (_, args) => { Console.Error.WriteLine(args.Exception); Environment.Exit(1); };
        ThemeManager.Initialize("System", app.Dispatcher);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(error => throw new InvalidOperationException("Browser operation failed.", error))
        { Width = 1370, Height = 860, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        window.Show();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var opening = window.ViewModel.OpenAsync(sourcePath);
        PumpUntil(() => opening.IsCompleted && window.ViewModel.Rows is { Count: > 0 } && !window.ViewModel.IsQuerying);
        opening.GetAwaiter().GetResult();
        var row = (VirtualMediaRow)window.ViewModel.Rows![0]!; PumpUntil(() => row.Media is not null);
        var seconds = watch.Elapsed.TotalSeconds;
        var memory = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d;
        Console.WriteLine($"Cached desktop: {window.ViewModel.Rows.Count:N0} entries; first row {seconds:F3} s; peak {memory:F1} MB.");
        if (seconds >= 2 || memory >= 400) throw new InvalidOperationException("Cached desktop browsing exceeded its performance targets.");
        window.ViewModel.SelectedRow = row;
        PumpUntil(() => window.ViewModel.SelectedMedia?.Names.Count > 0);
        VerifyThemeChanges(window, output);
        VerifyEventSwitching(window);
        window.Close();
        return 0;
    }

    private static void VerifyEventSwitching(MainWindow window)
    {
        var model = window.ViewModel;
        var store = (CatalogStore)typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        var examples = new List<(BankInfo Bank, AudioEvent Event)>();
        foreach (var bank in store.GetBanks().Where(bank => bank.Name.Contains("Music", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var entry in store.GetEvents(bank.Key).Where(entry => entry.Behavior == EventBehavior.StateChange && entry.MediaKeys.Count > 0 && entry.RelatedPlayEventKeys.Count == 1))
                examples.Add((bank, entry));
            if (examples.Count >= 20) break;
        }
        if (examples.Count < 2) throw new InvalidOperationException("No representative music states were available for event-switching checks.");
        var timings = new List<double>();
        foreach (var (bank, entry) in examples.Take(12))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            model.SelectNode(new LibraryNode { Name = entry.DisplayName, Bank = bank, Event = entry });
            PumpUntil(() => !model.IsQuerying && model.EventDetails != "Preparing event…" && model.EventDetails.Length > 0);
            timings.Add(watch.Elapsed.TotalMilliseconds);
            if (model.Rows!.Count != entry.MediaKeys.Count) throw new InvalidOperationException("Event switching displayed stale audio links.");
        }
        foreach (var (bank, entry) in examples.Take(12)) model.SelectNode(new LibraryNode { Name = entry.DisplayName, Bank = bank, Event = entry });
        var final = examples.Take(12).Last();
        PumpUntil(() => !model.IsQuerying && model.EventDetails != "Preparing event…" && model.EventDetails.Length > 0);
        if (model.ScopeText != final.Event.DisplayName || model.Rows!.Count != final.Event.MediaKeys.Count) throw new InvalidOperationException("Rapid event switches installed a stale result.");
        Console.WriteLine($"Large-library event switching: {timings.Count} states, maximum {timings.Max():F1} ms; rapid replacement passed.");
    }

    private static void VerifyThemeChanges(MainWindow window, string output)
    {
        var rows = window.ViewModel.Rows;
        var selected = window.ViewModel.SelectedRow;
        var search = window.ViewModel.Search;
        var colors = new List<Color>();
        foreach (var theme in new[] { "Light", "Dark", "System" })
        {
            ThemeManager.SetPreference(theme);
            window.UpdateLayout();
            if (!ReferenceEquals(rows, window.ViewModel.Rows) || !ReferenceEquals(selected, window.ViewModel.SelectedRow) || search != window.ViewModel.Search)
                throw new InvalidOperationException("A theme change reset the browser state.");
            colors.Add(((SolidColorBrush)((Grid)window.Content).Background).Color);
            Render(window, Path.Combine(output, "browser-" + theme.ToLowerInvariant() + ".png"));
            VerifyDialogsAndHover(window, Path.Combine(output, theme.ToLowerInvariant()));
        }
        if (colors[0] == colors[1]) throw new InvalidOperationException("Light and Dark applied the same surface color.");
        Console.WriteLine("Light, Dark and System changes preserved the browser; shared dialog styles and fixed hover dimensions passed.");
    }

    private static void Render(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void VerifyHighContrastSelections(MainWindow window)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var previous = dictionaries[0];
        dictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/" + Uri.EscapeDataString(typeof(App).Assembly.GetName().Name!) + ";component/Themes/Theme.HighContrast.xaml") };
        try
        {
            var grid = (DataGrid)window.FindName("MediaGrid");
            var row = window.ViewModel.Rows![0]!;
            grid.SelectedItem = row; grid.ScrollIntoView(row); grid.UpdateLayout();
            var id = (TextBlock)grid.Columns[1].GetCellContent(row);
            var expected = ((SolidColorBrush)window.FindResource("SelectedTextBrush")).Color;
            if (((SolidColorBrush)id.Foreground).Color != expected) throw new InvalidOperationException("Selected IDs do not use the high-contrast selection text color.");
            var tree = (TreeView)window.FindName("LibraryTree");
            window.ViewModel.Roots[0].IsActive = true; tree.UpdateLayout();
            var item = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            item.ApplyTemplate();
            var header = (ContentPresenter)item.Template.FindName("PART_Header", item);
            header.ApplyTemplate();
            var label = FindText(header);
            if (label is null || ((SolidColorBrush)label.Foreground).Color != expected) throw new InvalidOperationException("Selected tree labels do not use the high-contrast selection text color.");
        }
        finally { dictionaries[0] = previous; }
        Console.WriteLine("High-contrast table IDs and tree labels use the Windows highlight text color.");

        static TextBlock? FindText(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is TextBlock text) return text;
                if (FindText(child) is { } found) return found;
            }
            return null;
        }
    }

    private static int VerifyDevice(string wavPath, string toolsPath)
    {
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var input = new WaveFileReader(wavPath);
        Console.WriteLine($"Preview source: {input.WaveFormat.Channels} channels, {input.WaveFormat.SampleRate} Hz");
        Console.WriteLine($"Output device: {device.FriendlyName}; {device.AudioClient.MixFormat}");
        using var player = new Player(); var audio = new AudioService(AudioToolPaths.Discover(toolsPath));
        Exception? failure = null; player.PlaybackStopped += (_, args) => failure = args.Exception ?? failure;
        var fallback = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(wavPath))!, "device-stereo.wav");
        player.LoadAsync(wavPath, .12f, (source, token) => audio.NormalizePreviewToStereoAsync(source, fallback, token)).GetAwaiter().GetResult();
        Thread.Sleep(1000);
        if (failure is not null || player.State != PlaybackState.Playing) throw new InvalidOperationException("Device playback failed.", failure);
        player.Position = Math.Min(2, Math.Max(0, player.Length / 2)); Thread.Sleep(200);
        if (failure is not null) throw new InvalidOperationException("Playback seek failed.", failure);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (player.State == PlaybackState.Playing && DateTime.UtcNow < deadline) Thread.Sleep(50);
        if (failure is not null || player.State != PlaybackState.Stopped) throw new InvalidOperationException("Playback did not finish cleanly.", failure);
        player.Toggle(); Thread.Sleep(200);
        if (player.State != PlaybackState.Playing) throw new InvalidOperationException("Completed audio did not replay.");
        player.Toggle(); player.Position = .5;
        if (player.State != PlaybackState.Paused) throw new InvalidOperationException("Seek did not preserve pause.");
        player.Toggle(); Thread.Sleep(100); player.Stop();
        Console.WriteLine("Actual device playback, seek, EOF replay and paused seeking passed."); return 0;
    }

    private static void VerifyTaskWindow(MainWindow main, string output)
    {
        using var session = new OperationSession("Exporting WAV", OperationKind.Export);
        session.Report("Exporting", 25, 100, "Selected audio");
        var window = new TaskWindow(main, session) { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        window.Show(); window.UpdateLayout();
        if (!main.IsEnabled || !window.IsVisible) throw new InvalidOperationException("The task window blocked browsing or did not open.");
        Render(window, Path.Combine(output, "task-window.png"));
        window.Hide(); window.Show();
        session.Cancel(); if (!session.Token.IsCancellationRequested || session.State != OperationState.Cancelling) throw new InvalidOperationException("The task window did not cancel its operation.");
        session.Settle(); window.CloseOwned();
        Console.WriteLine("Modeless task window, hide/reopen, progress styling and cancellation passed.");
    }

    private static void VerifyReducedMotionLogo(MainWindow window)
    {
        var settings = (UserSettings)typeof(MainViewModel).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.ViewModel)!;
        var previous = settings.ReduceMotion; settings.ReduceMotion = true;
        try
        {
            var click = typeof(MainWindow).GetMethod("OnBrandLogoClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var index = 0; index < 3; index++) click.Invoke(window, [window.FindName("BrandLogo"), new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent }]);
            var rotation = (RotateTransform)window.FindName("BrandLogoRotation");
            if (rotation.HasAnimatedProperties || rotation.Angle != 0) throw new InvalidOperationException("The logo spin ignored reduced motion.");
        }
        finally { settings.ReduceMotion = previous; }
        Console.WriteLine("Main-window logo respects reduced motion.");
    }

    private static void VerifyReplacementRightClick(MainWindow window, AudioCatalog catalog, CatalogStore store)
    {
        catalog.Media[3].Name = catalog.Media[4].Name = "Grouped_Clip";
        store.Save(catalog, null, default);
        var model = window.ViewModel; model.GroupSimilar = true; model.SelectNode(model.QuickViews[0]); PumpUntil(() => !model.IsQuerying);
        var rows = Enumerable.Range(0, model.Rows!.Count).Select(index => (VirtualMediaRow)model.Rows[index]!).ToArray();
        PumpUntil(() => rows.All(row => row.Projection is not null));
        var group = rows.First(row => row.IsGroupHeader); var grid = (DataGrid)window.FindName("MediaGrid"); grid.SelectedItem = group;
        var expanding = model.ToggleGroupCommand.ExecuteAsync(group); PumpUntil(() => expanding.IsCompleted); expanding.GetAwaiter().GetResult();
        var expanded = Enumerable.Range(0, model.Rows!.Count).Select(index => (VirtualMediaRow)model.Rows[index]!).ToArray();
        PumpUntil(() => expanded.All(row => row.Projection is not null));
        var solo = expanded.First(row => !row.IsGroupHeader && !row.IsGroupChild);
        grid.ScrollIntoView(solo); grid.UpdateLayout();
        var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(solo);
        if (container.IsSelected) throw new InvalidOperationException("The right-click target was already selected.");
        var rightClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = container };
        typeof(MainWindow).GetMethod("OnMediaRightButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [grid, rightClick]);
        var snapshot = SelectionSnapshot.CaptureBrowser(new(), 0, model.SelectionRows, true, model.Rows.ExpandedGroups);
        var preserved = (HashSet<string>?)typeof(MainViewModel).GetField("preservedBrowserKeys", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);
        if (preserved is not null) snapshot = snapshot with { PreservedBrowserKeys = preserved };
        var resolving = snapshot.ResolveBrowserEntriesAsync(store, default); PumpUntil(() => resolving.IsCompleted);
        var selected = resolving.GetAwaiter().GetResult();
        if (selected.Count != 1 || selected[0].Key != solo.Media!.Key) throw new InvalidOperationException("Right-click replacement retained hidden group members.");
        model.GroupSimilar = false; model.SelectNode(model.QuickViews[0]); PumpUntil(() => !model.IsQuerying);
        Console.WriteLine("Right-click replacement exports exactly the visible selection after group expansion.");
    }

    private static void VerifySidebarSelection(MainWindow window)
    {
        var model = window.ViewModel;
        model.Search = ""; model.SelectNode(model.QuickViews[0]); PumpUntil(() => !model.IsQuerying);
        var bank = model.Roots[0].Children[0]; bank.IsExpanded = true;
        var entry = bank.Children.SelectMany(group => group.Children).First(node => node.Event is not null);
        var select = typeof(MainWindow).GetMethod("ApplyLibrarySelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        select.Invoke(window, [bank, System.Windows.Input.ModifierKeys.None]);
        select.Invoke(window, [entry, System.Windows.Input.ModifierKeys.Control]);
        PumpUntil(() => !model.IsQuerying);
        if (model.SelectedLibraryNodes.Count != 2 || model.Rows!.Count != 10 || !bank.IsSelected || !entry.IsSelected)
            throw new InvalidOperationException("Sidebar multi-selection lost its union scope or highlights.");
        select.Invoke(window, [bank, System.Windows.Input.ModifierKeys.None]);
        select.Invoke(window, [entry, System.Windows.Input.ModifierKeys.Shift]);
        if (model.SelectedLibraryNodes.Count != 2) throw new InvalidOperationException("Sidebar range selection failed.");
        model.SelectNode(model.QuickViews[0]); PumpUntil(() => !model.IsQuerying);
        Console.WriteLine("Sidebar Ctrl/Shift selections preserve the bank/event union without duplicate media.");
    }

    private static void VerifyPlaybackEligibility(MainViewModel model, MediaEntry complete)
    {
        model.SetSelection([]);
        model.SelectedMedia = complete;
        if (!model.PlayCommand.CanExecute(null) || !model.ExportWavCommand.CanExecute(null)) throw new InvalidOperationException("Complete audio playback/conversion was disabled.");
        var fragment = new MediaEntry { Key = "fragment", IsRiff = true, Slice = complete.Slice, State = MediaState.PrefetchOnly, Completeness = MediaCompleteness.Fragment, ContainerValidity = ContainerValidity.Valid, Availability = SourceAvailability.Available };
        model.SelectedMedia = fragment;
        if (model.PlayCommand.CanExecute(null) || !model.PartialPreviewCommand.CanExecute(null) || model.ExportWavCommand.CanExecute(null)) throw new InvalidOperationException("Fragment playback/conversion controls are wrong.");
        model.SelectedMedia = new() { State = MediaState.MissingExternal, Availability = SourceAvailability.Missing };
        if (model.PlayCommand.CanExecute(null) || model.PlaybackMessage.Length == 0) throw new InvalidOperationException("Missing audio playback is enabled or unexplained.");
        model.SelectedMedia = complete;
    }

    private static void VerifyDialogsAndHover(MainWindow main, string output)
    {
        Directory.CreateDirectory(output);
        var tools = new ToolsWindow(new UserSettings());
        var export = new ExportWindow(new(new(Path.Combine(output, "export"), ExportFormat.Original), [], 0, new(1, 2, 3, 4)));
        foreach (var (window, name) in new[] { ((Window)tools, "tools"), ((Window)export, "export") })
        {
            window.UpdateLayout();
            if (window.Style is null || window.Background is not SolidColorBrush) throw new InvalidOperationException("Dialog theme was not applied.");
            var content = (FrameworkElement)window.Content; content.Measure(new Size(window.Width, window.Height)); content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        var tree = (TreeView)main.FindName("LibraryTree"); tree.UpdateLayout();
        var item = (TreeViewItem?)tree.ItemContainerGenerator.ContainerFromIndex(0) ?? throw new InvalidOperationException("Tree did not realize its first row.");
        item.ApplyTemplate(); var border = (Border)item.Template.FindName("HeaderBorder", item);
        var key = (DependencyPropertyKey?)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) ?? throw new InvalidOperationException("Hover test property was not found.");
        item.SetValue(key, false); main.UpdateLayout(); var size = border.RenderSize;
        item.SetValue(key, true); main.UpdateLayout();
        if (size != border.RenderSize) throw new InvalidOperationException("Hover changes tree row dimensions.");
        item.SetValue(key, false);
    }

    private static void VerifyVirtualList(MainWindow window)
    {
        var dispatcher = window.Dispatcher;
        using var rows = new VirtualMediaSource(100123, dispatcher, (offset, limit, token) => Enumerable.Range(offset, limit).Select(index => { token.ThrowIfCancellationRequested(); return new MediaEntry { Key = "virtual-" + index, Id = (uint)index, Name = "Entry " + index }; }).ToArray());
        var first = (VirtualMediaRow)rows[0]!; PumpUntil(() => first.Media is not null); rows.KeepSelection([first]);
        for (var i = 1; i < 30; i++) _ = rows[i * 2500];
        var last = (VirtualMediaRow)rows[100122]!; PumpUntil(() => last.Media is not null);
        if (last.Id != 100122 || rows.CachedBlockCount > VirtualMediaSource.MaximumBlocks || rows.IndexOf(first) != 0) throw new InvalidOperationException("Virtual browser lost count, cache bound, or selection identity.");
        if (!ReferenceEquals(rows[0], first)) throw new InvalidOperationException("Reload discarded a selected row.");
        var grid = (DataGrid)window.FindName("MediaGrid"); grid.ItemsSource = rows; grid.UpdateLayout();
        if (grid.Items.Count != 100123) throw new InvalidOperationException("The grid did not expose the complete logical list.");
        grid.ScrollIntoView(last); grid.UpdateLayout();
        PumpUntil(() => grid.ItemContainerGenerator.ContainerFromIndex(100122) is DataGridRow);
        if (rows.CachedBlockCount > VirtualMediaSource.MaximumBlocks) throw new InvalidOperationException("Scrolling exceeded the virtual cache bound.");
        using var cancelled = new VirtualMediaSource(1000, dispatcher, (offset, limit, token) => { token.WaitHandle.WaitOne(100); token.ThrowIfCancellationRequested(); return []; });
        var pending = (VirtualMediaRow)cancelled[0]!; cancelled.Dispose();
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (pending.Media is not null) throw new InvalidOperationException("Cancelled virtual query published stale rows.");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI check did not finish loading.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
    }
}
