using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Export;
using DetroitAudio.Indexing;
using Microsoft.Win32;
using NAudio.Wave;

namespace DetroitAudio.Desktop;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Window owner;
    private readonly UserSettings settings = UserSettings.Load();
    private readonly Player idlePlayer = new();
    private Player player => preview?.Player ?? idlePlayer;
    private readonly IPlaybackBackendFactory? playbackFactory;
    private readonly Func<AudioCatalog, MediaEntry, CancellationToken, bool, Task<PreviewClip>> prepareMedia;
    private readonly Func<string?> chooseExportFolder;
    private readonly Func<ExportPlan, ExportPlan?> confirmExport;
    private readonly Action<Exception> reportError;
    private readonly DispatcherTimer timer;
    private AudioCatalog? catalog;
    private CatalogStore? store;
    private AudioService audio;
    private CancellationTokenSource? queryCancellation;
    private CancellationTokenSource? selectionCancellation, eventDetailsCancellation;
    private bool loadingMediaDetails;
    private CancellationTokenSource viewCancellation = new();
    private PreviewSession? preview;
    private readonly HashSet<PreviewSession> activePreviewRequests = [];
    private CatalogQuery resultQuery = new();
    private int resultGeneration;
    private bool disposed, updatingStatus;
    private string idleStatus = "Ready";
    private LibraryNode? node;
    private ExportPlan? lastPlan;
    private ExportReport? lastReport;
    private AudioCatalog? lastExportCatalog;
    private int total, queryGeneration;
    private bool updatingFilters;
    private bool allResultsSelected;
    private bool updatingPosition;
    private string? playingKey;
    public IReadOnlyList<MediaEntry> Selection { get; set; } = [];
    public IReadOnlyList<VirtualMediaRow> SelectionRows { get; private set; } = [];
    [ObservableProperty] private VirtualMediaSource? rows;
    [ObservableProperty] private VirtualMediaRow? selectedRow;
    public ObservableCollection<LibraryNode> Roots { get; } = [];
    public ObservableCollection<LibraryNode> QuickViews { get; } = [new() { Name = "All audio" }];
    public ObservableCollection<EventChoice> RelatedPlayEvents { get; } = [];
    [ObservableProperty] private EventChoice? relatedPlayEvent;
    public bool HasRelatedPlayChoice => RelatedPlayEvents.Count > 1;
    public IReadOnlyList<string> SearchScopes { get; } = ["All audio", "Current view"];
    [ObservableProperty] private string searchScope = "All audio";
    [ObservableProperty] private string scopeText = "All audio";
    [ObservableProperty] private string emptyText = "";
    [ObservableProperty] private bool isQuerying;
    public ObservableCollection<JobRow> Jobs { get; } = [];
    public ObservableCollection<string> Languages { get; } = ["All languages"];
    public ObservableCollection<string> EventLanguages { get; } = ["ENG"];
    public ObservableCollection<string> Categories { get; } = ["All types"];
    public ObservableCollection<string> Codecs { get; } = ["All codecs"];
    public IReadOnlyList<string> SortOptions { get; } = ["Name", "ID", "Bank", "Language", "Duration", "Size"];
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string language = "All languages";
    [ObservableProperty] private string category = "All types";
    [ObservableProperty] private string codec = "All codecs";
    [ObservableProperty] private string sort = "Name";
    [ObservableProperty] private bool descending;
    [ObservableProperty] private MediaEntry? selectedMedia;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool preparingPreview;
    [ObservableProperty] private bool hasLibrary;
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string resultsText = "";
    [ObservableProperty] private string selectionName = "Preview";
    [ObservableProperty] private string details = "";
    [ObservableProperty] private string eventDetails = "";
    [ObservableProperty] private string playLabel = "Play";
    [ObservableProperty] private string playbackMessage = "";
    [ObservableProperty] private string partialPlayLabel = "Partial preview";
    public bool HasPartialPreview => SelectedMedia?.Completeness == MediaCompleteness.Fragment;
    [ObservableProperty] private double positionSeconds;
    [ObservableProperty] private double lengthSeconds = 1;
    [ObservableProperty] private double volume = .75;
    [ObservableProperty] private string timeText = "0:00";
    [ObservableProperty] private string totalTimeText = "0:00";
    [ObservableProperty] private string eventLanguage = "ENG";
    [ObservableProperty] private string seed = "1";
    [ObservableProperty] private string loops = "2";
    [ObservableProperty] private string switchValues = "";
    public bool ReducedMotion => ThemeManager.IsReducedMotion(settings.ReduceMotion);

    public MainViewModel(Window owner, UserSettings? userSettings = null, IPlaybackBackendFactory? playbackFactory = null,
        Func<AudioCatalog, MediaEntry, CancellationToken, bool, Task<PreviewClip>>? prepareMedia = null,
        Func<string?>? chooseExportFolder = null, Func<ExportPlan, ExportPlan?>? confirmExport = null,
        Action<Exception>? reportError = null)
    {
        settings = userSettings ?? settings;
        groupSimilar = settings.GroupSimilarAudio;
        this.playbackFactory = playbackFactory;
        this.reportError = reportError ?? (error => MessageBox.Show(owner, error.Message, "Detroit Audio", MessageBoxButton.OK, MessageBoxImage.Error));
        this.owner = owner; audio = new(AudioToolPaths.Discover(settings.ToolsDirectory)); Volume = settings.Volume;
        this.prepareMedia = prepareMedia ?? ((data, media, token, partial) => new PreviewService(audio).CreateAsync(data, media, cancellationToken: token, partialPreview: partial));
        this.chooseExportFolder = chooseExportFolder ?? (() => { var dialog = new OpenFolderDialog { Title = "Export folder", InitialDirectory = settings.LastExportDirectory }; return dialog.ShowDialog(owner) == true ? dialog.FolderName : null; });
        this.confirmExport = confirmExport ?? (plan => { var confirmation = new ExportWindow(plan) { Owner = owner }; return confirmation.ShowDialog() == true ? plan with { Options = plan.Options with { Overwrite = confirmation.Overwrite } } : null; });
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => UpdatePlayback(), owner.Dispatcher); timer.Start();
    }

    private PreviewSession BeginPreview(string key)
    {
        RetirePreview();
        var session = preview = new PreviewSession(playbackFactory) { Key = key };
        activePreviewRequests.Add(session);
        session.Player.PlaybackStopped += (_, args) =>
        {
            _ = owner.Dispatcher.InvokeAsync(() =>
            {
                if (disposed || !ReferenceEquals(preview, session)) return;
                if (args.Exception is { } error) { RetirePreview(); AudioError(error); }
                UpdatePlayback();
            });
        };
        PreparingPreview = true;
        return session;
    }

    private void RetirePreview()
    {
        var previous = preview; preview = null; playingKey = null; PreparingPreview = false;
        if (previous is null) return;
        previous.Cancel(); previous.Player.Dispose();
        if (previous.Prepared) _ = previous.DisposeAsync();
    }

    private bool CanOpen() => !IsBusy;
    private bool CanExport() => HasLibrary && !IsBusy;
    private static bool PreviewAvailable(MediaEntry media) => MediaPolicy.CanPreview(media) && File.Exists(media.Slice.FilePath);
    private bool CanPlay() => HasLibrary && !IsBusy && !loadingMediaDetails && SelectedRow?.IsGroupHeader != true && (SelectedMedia is { } media ? media.Completeness != MediaCompleteness.Fragment && PreviewAvailable(media) : CanEvent());
    private bool CanPartialPreview() => CanExport() && SelectedMedia is { Completeness: MediaCompleteness.Fragment } media && PreviewAvailable(media);
    private bool CanExportFragment() => CanExport() && SelectedMedia is { Completeness: MediaCompleteness.Fragment } media && MediaPolicy.HasBytes(media);
    private bool CanConvertSelection() => CanExport() && (allResultsSelected || preservedBrowserKeys?.Count > 0 || SelectionRows.Any(row => row.IsGroupHeader || row.Media is null || MediaPolicy.CanConvert(row.Media)) || Selection.Any(MediaPolicy.CanConvert) || SelectedMedia is { } media && MediaPolicy.CanConvert(media) || SelectedLibraryNodes.Count > 0);
    private bool CanEvent() => CanExport() && EffectiveNode?.Event is { MediaKeys.Count: > 0 } entry && (entry.RelatedPlayEventKeys.Count <= 1 || RelatedPlayEvent is not null);
    private bool CanRenderEvents() => CanExport() && (SelectedLibraryNodes.Any(item => item.Event is { MediaKeys.Count: > 0 }) || CanEvent());
    private bool CanReindex() => CanExport() && catalog is not null;
    private bool CanRetry() => CanExport() && lastReport?.Results.Any(r => r.Status == "Failed") == true;
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnPreparingPreviewChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
    partial void OnHasLibraryChanged(bool value) => RefreshCommands();
    partial void OnSearchChanged(string value) => ScheduleQuery();
    partial void OnSearchScopeChanged(string value) => ScheduleQuery();
    partial void OnLanguageChanged(string value) => ScheduleQuery();
    partial void OnCategoryChanged(string value) => ScheduleQuery();
    partial void OnCodecChanged(string value) => ScheduleQuery();
    partial void OnSortChanged(string value) => ScheduleQuery();
    partial void OnDescendingChanged(bool value) => ScheduleQuery();
    partial void OnEventLanguageChanged(string value) => ScheduleEventDetails();
    partial void OnSeedChanged(string value) => ScheduleEventDetails();
    partial void OnLoopsChanged(string value) => ScheduleEventDetails();
    partial void OnSwitchValuesChanged(string value) => ScheduleEventDetails();
    partial void OnStatusChanged(string value) { if (!updatingStatus) idleStatus = value; }
    partial void OnVolumeChanged(double value) => player.Volume = (float)value;
    partial void OnPositionSecondsChanged(double value) { if (!updatingPosition) player.Position = value; }
    partial void OnSelectedMediaChanged(MediaEntry? value)
    {
        if (preview?.Key != value?.Key) { RetirePreview(); if (!IsBusy) Status = "Ready"; }
        var context = EffectiveNode;
        SelectionName = value?.DisplayName ?? context?.Event?.DisplayName ?? context?.Name ?? "Preview";
        Details = value is null ? BankDetails(context?.Bank) : MediaDetails(value);
        PlaybackMessage = value is null ? "" : MediaPolicy.GetUnavailableReason(value);
        if (value is not null && !File.Exists(value.Slice.FilePath)) PlaybackMessage = "Source file is missing.";
        OnPropertyChanged(nameof(HasPartialPreview));
        RefreshCommands();
    }
    partial void OnSelectedRowChanged(VirtualMediaRow? value)
    {
        SelectedMedia = value?.Media;
        if (value?.IsGroupHeader == true) { SelectionName = value.DisplayName; Details = $"{value.Projection!.Count:N0} audio entries"; }
        _ = LoadSelectedMediaAsync(value);
    }
    private async Task LoadSelectedMediaAsync(VirtualMediaRow? row)
    {
        selectionCancellation?.Cancel();
        if (row?.Media is not { } summary || store is null) { loadingMediaDetails = false; RefreshCommands(); return; }
        var cancellation = selectionCancellation = new CancellationTokenSource(); var token = cancellation.Token; var selectedStore = store;
        loadingMediaDetails = true; RefreshCommands();
        try
        {
            var full = await Task.Run(() => selectedStore.GetMedia(summary.Key, token), token);
            if (!disposed && !token.IsCancellationRequested && ReferenceEquals(SelectedRow, row) && ReferenceEquals(store, selectedStore)) SelectedMedia = full;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!disposed && ReferenceEquals(SelectedRow, row)) AudioError(error); }
        finally { if (ReferenceEquals(selectionCancellation, cancellation)) { selectionCancellation = null; loadingMediaDetails = false; RefreshCommands(); } cancellation.Dispose(); }
    }
    partial void OnRelatedPlayEventChanged(EventChoice? value) { UpdateEventDetails(); RefreshCommands(); }
    private void RefreshCommands()
    {
        WelcomeCommand.NotifyCanExecuteChanged();
        foreach (var command in new IRelayCommand[] { CancelCommand, OpenFolderCommand, OpenFileCommand, ReindexCommand, PlayCommand, PartialPreviewCommand, PlayEventCommand, ExportFragmentCommand, ExportAllOriginalCommand, ExportAllOggCommand, ExportAllWavCommand, ExportOriginalCommand, ExportFilteredCommand, ExportOggCommand, ExportWavCommand, ConvertFilteredOggCommand, ConvertFilteredWavCommand, ExportBankCommand, ExportBankMediaCommand, ExportEventWavCommand, ExportEventOggCommand, RetryCommand }) command.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))] private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Open game folder" }; if (dialog.ShowDialog(owner) == true) await OpenAsync(dialog.FolderName);
    }
    [RelayCommand(CanExecute = nameof(CanOpen))] private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "Open audio archive", Filter = "Audio archives|*.idx;*.bnk;*.wem;*.mid;*.midi;*.dat;*.d??|All files|*.*" };
        if (dialog.ShowDialog(owner) == true) await OpenAsync(dialog.FileName);
    }
    [RelayCommand(CanExecute = nameof(CanReindex))] private Task ReindexAsync() => OpenAsync(settings.LastSource, true);

    public async Task OpenAsync(string path, bool rebuild = false)
    {
        if (IsBusy) return;
        IsBusy = true; InvalidateView(); Stop();
        var session = StartTask("Opening library", OperationKind.Opening);
        var completionState = OperationState.Cancelled; var summary = "Cancelled";
        try
        {
            var progress = new Progress<IndexProgress>(p =>
            {
                if (disposed || !ReferenceEquals(CurrentTask, session) || session.Token.IsCancellationRequested || session.IsFinished) return;
                ReportTask(session, p.Phase, p.Completed, p.Total, p.Detail);
                if (p.Snapshot is { } snapshot) { BuildTree(snapshot); HasLibrary = true; }
            });
            var result = await new IndexService().OpenSessionAsync(path, settings.CacheDirectory, progress, session.Token, rebuild);
            if (disposed) { result.Store.Dispose(); return; }
            ReplaceRows(null); store?.Dispose(); store = result.Store; catalog = store.CreateSummaryCatalog(session.Token); node = QuickViews[0];
            SelectedLibraryNodes = []; expandedGroups.Clear();
            foreach (var view in QuickViews) view.IsActive = ReferenceEquals(view, node);
            RelatedPlayEvents.Clear(); RelatedPlayEvent = null; OnPropertyChanged(nameof(HasRelatedPlayChoice));
            BuildTree(catalog); HasLibrary = true;
            updatingFilters = true;
            var selectedLanguage = Language; var selectedCategory = Category; var selectedCodec = Codec;
            Replace(Languages, "All languages", store.Facets("language").Select(l => l == "SFX" ? "—" : l)); Replace(Categories, "All types", store.Facets("category")); Replace(Codecs, "All codecs", store.Facets("codec"));
            Language = Languages.Contains(selectedLanguage) ? selectedLanguage : "All languages"; Category = Categories.Contains(selectedCategory) ? selectedCategory : "All types"; Codec = Codecs.Contains(selectedCodec) ? selectedCodec : "All codecs";
            var selectedEventLanguage = EventLanguage;
            EventLanguages.Clear(); foreach (var value in Languages.Where(value => value is not ("All languages" or "—"))) EventLanguages.Add(value);
            if (EventLanguages.Count == 0) EventLanguages.Add("ENG");
            EventLanguage = EventLanguages.Contains(selectedEventLanguage) ? selectedEventLanguage : EventLanguages.Contains("ENG") ? "ENG" : EventLanguages[0];
            updatingFilters = false;
            settings.LastSource = path; settings.Save();
            summary = $"{result.Header.BankCount:N0} banks · {result.Header.EventCount:N0} events · {result.Header.MediaCount:N0} entries";
            completionState = OperationState.Completed;
            Progress = 100;
        }
        catch (OperationCanceledException) { completionState = OperationState.Cancelled; summary = "Cancelled"; }
        catch (Exception ex) { completionState = OperationState.Failed; summary = ex.Message.Split('\n')[0]; Error(ex); }
        finally
        {
            updatingFilters = false;
            try
            {
                if (!disposed)
                {
                    if (catalog is not null) BuildTree(catalog); else { Roots.Clear(); ReplaceRows(null); HasLibrary = false; }
                    await QueryAsync(false);
                    if (completionState == OperationState.Completed) await WaitForActiveBrowserAsync(session.Token);
                }
            }
            catch (OperationCanceledException) { completionState = OperationState.Cancelled; summary = "Cancelled"; }
            catch (Exception ex) { completionState = OperationState.Failed; summary = ex.Message.Split('\n')[0]; Error(ex); }
            finally { IsBusy = false; FinishTask(session, completionState, summary); session.Settle(); }
        }
    }

    private void BuildTree(AudioCatalog data)
    {
        Roots.Clear();
        var treeStore = ReferenceEquals(data, catalog) ? store : null;
        var byBank = data.Events.GroupBy(e => e.BankKey).ToDictionary(g => g.Key, g => g.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
        var banks = new LibraryNode { Name = "Banks", IsExpanded = true };
        foreach (var bank in data.Banks.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            var child = new LibraryNode { Name = bank.Name, Bank = bank, Expand = n => AddEventGroups(n, bank, treeStore?.GetEvents(bank.Key) ?? byBank.GetValueOrDefault(bank.Key) ?? []) };
            if (byBank.ContainsKey(bank.Key)) child.Children.Add(new() { Name = "" }); banks.Children.Add(child);
        }
        Roots.Add(banks);
        var archives = new LibraryNode { Name = "Archives" };
        foreach (var group in data.Banks.GroupBy(b => Path.GetFileName(b.Slice.FilePath)).OrderBy(g => g.Key))
        {
            var archive = new LibraryNode { Name = group.Key, Archive = group.Key };
            foreach (var bank in group.OrderBy(b => b.Name))
            {
                var child = new LibraryNode { Name = bank.Name, Bank = bank, Archive = group.Key, Expand = n => AddEventGroups(n, bank, treeStore?.GetEvents(bank.Key) ?? byBank.GetValueOrDefault(bank.Key) ?? []) };
                if (byBank.ContainsKey(bank.Key)) child.Children.Add(new() { Name = "" }); archive.Children.Add(child);
            }
            archives.Children.Add(archive);
        }
        Roots.Add(archives);
        // Dialogue events use a separate branch to avoid eagerly constructing thousands of tree items.
        if (data.Events.Any(e => e.IsDialogue))
        {
            var dialogueEvents = new LibraryNode { Name = "Dialogue events", Dialogue = true, Expand = n => { foreach (var entry in data.Events.Where(e => e.IsDialogue).OrderBy(e => e.DisplayName)) n.Children.Add(new() { Name = entry.DisplayName, Event = entry, Dialogue = true }); } };
            dialogueEvents.Children.Add(new() { Name = "" }); Roots.Add(dialogueEvents);
        }
    }

    private static void AddEventGroups(LibraryNode parent, BankInfo bank, IReadOnlyList<AudioEvent> events)
    {
        foreach (var grouping in events.GroupBy(e => e.Behavior == EventBehavior.StateChange ? "States" : e.Behavior is EventBehavior.Playback or EventBehavior.Mixed ? "Playback" : "Controls").OrderBy(g => g.Key == "Playback" ? 0 : g.Key == "States" ? 1 : 2))
        {
            var group = new LibraryNode { Name = grouping.Key, Bank = bank, Archive = parent.Archive, IsGroup = true, IsExpanded = true };
            foreach (var entry in grouping) group.Children.Add(new() { Name = entry.DisplayName, Bank = bank, Archive = parent.Archive, Event = entry });
            parent.Children.Add(group);
        }
    }

    public void SelectNode(LibraryNode selected)
    {
        SelectedLibraryNodes = selected.Event is not null || selected.Bank is not null && !selected.IsGroup ? [selected] : [];
        foreach (var item in WalkNodes(Roots)) item.IsSelected = ReferenceEquals(item, selected);
        ApplyFocusedNode(selected);
    }
    private void ApplyFocusedNode(LibraryNode selected)
    {
        if (selected.Event is { } summary && store?.GetEvent(summary.Key) is { } full) selected.Event = full;
        node = selected; SelectedRow = null; SelectedMedia = null; RetirePreview();
        updatingFilters = true;
        if (selected == QuickViews[0]) { Language = "All languages"; Category = "All types"; Codec = "All codecs"; }
        updatingFilters = false;
        RelatedPlayEvents.Clear(); RelatedPlayEvent = null;
        if (selected.Event is { } entry && catalog is not null)
        {
            foreach (var key in entry.RelatedPlayEventKeys)
            {
                var playback = store?.GetEvent(key) ?? catalog.Events.FirstOrDefault(e => e.Key == key);
                if (playback is not null) RelatedPlayEvents.Add(new(key, playback.DisplayName));
            }
            RelatedPlayEvent = RelatedPlayEvents.Count == 1 ? RelatedPlayEvents[0] : null; UpdateEventDetails();
        }
        else EventDetails = "";
        OnPropertyChanged(nameof(HasRelatedPlayChoice));
        RefreshCommands(); _ = QueryAsync(false);
    }
    private void UpdateEventDetails()
    {
        if (node?.Event is not { } entry || catalog is null) return;
        Details = BankDetails(node.Bank) + "\n\n" + string.Join("\n", entry.Diagnostics.Select(d => d.Message));
        if (RelatedPlayEvents.Count > 1 && RelatedPlayEvent is null) { EventDetails = "Choose a playback event"; return; }
        _ = UpdateEventDetailsAsync(entry);
    }
    private async Task UpdateEventDetailsAsync(AudioEvent entry, bool debounce = false)
    {
        eventDetailsCancellation?.Cancel(); var cancellation = eventDetailsCancellation = new CancellationTokenSource(); var token = cancellation.Token;
        EventDetails = "Preparing event…";
        try
        {
            if (debounce) await Task.Delay(150, token);
            var selectedStore = store; var fallback = catalog; var options = MakeEventOptions();
            var data = await Task.Run(() => selectedStore?.CreateOperationCatalog(eventKeys: [entry.Key], token: token) ?? fallback!, token);
            var plan = await audio.CreateEventPlanAsync(data, entry, options, cancellationToken: token);
            if (!disposed && !token.IsCancellationRequested && node?.Event?.Key == entry.Key && ReferenceEquals(store, selectedStore))
            {
                EventDetails = $"{plan.Items.Count} sources · {TimeSpan.FromSeconds(plan.DurationSeconds):m\\:ss}";
                Details = BankDetails(node.Bank) + "\n\n" + string.Join("\n", entry.Diagnostics.Select(d => d.Message).Concat(plan.Limitations).Distinct());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!disposed && !token.IsCancellationRequested && node?.Event?.Key == entry.Key) EventDetails = error.Message; }
        finally { if (ReferenceEquals(eventDetailsCancellation, cancellation)) eventDetailsCancellation = null; cancellation.Dispose(); }
    }
    private LibraryNode? EffectiveNode => Search.Length > 0 && SearchScope == "All audio" ? null : node;
    private CatalogQuery CurrentQuery(int pageOffset = 0, int limit = VirtualMediaSource.BlockSize)
    {
        var current = EffectiveNode; var scope = SelectedScope();
        return new(Search, scope is null && current?.Event is null ? current?.Bank?.Key : null, scope is null ? current?.Event?.Key : null,
            Language == "All languages" ? null : Language == "—" ? "SFX" : Language, Category == "All types" ? null : Category, Codec == "All codecs" ? null : Codec,
            Offset: pageOffset, Limit: limit, Sort: Sort, Descending: Descending, Archive: scope is null ? current?.Archive : null,
            Scope: scope, Dialogue: current is { Dialogue: true, Event: null });
    }
    private void ScheduleQuery() { if (store is not null && !updatingFilters) _ = QueryAsync(true); }
    private void InvalidateView()
    {
        queryCancellation?.Cancel(); ++queryGeneration;
        selectionCancellation?.Cancel(); eventDetailsCancellation?.Cancel();
        viewCancellation.Cancel(); viewCancellation.Dispose(); viewCancellation = new();
    }
    private void ReplaceRows(VirtualMediaSource? replacement)
    {
        var previous = Rows;
        SelectedRow = null; SelectedMedia = null; Selection = []; SelectionRows = []; allResultsSelected = false;
        Rows = replacement;
        previous?.Dispose();
    }
    private async Task QueryAsync(bool debounce, bool preserveSelection = false)
    {
        if (store is null || disposed) return;
        if (!preserveSelection) preservedBrowserKeys = null;
        InvalidateView(); var cancellation = queryCancellation = new(); var token = cancellation.Token; var generation = queryGeneration; var query = CurrentQuery(); var queryStore = store;
        IsQuerying = true; EmptyText = "";
        try
        {
            if (debounce) await Task.Delay(200, token);
            var grouped = GroupSimilar; var expansion = new HashSet<string>(expandedGroups);
            var counts = await Task.Run(() => (Media: queryStore.Count(query, token), Logical: queryStore.CountBrowserRows(query, grouped, expansion, token)), token);
            if (generation != queryGeneration || disposed) return;
            total = counts.Media;
            var source = VirtualMediaSource.CreateBrowser(counts.Logical, owner.Dispatcher,
                (offset, limit, token) => queryStore.ReadBrowserRows(query, grouped, expansion, offset, limit, token), expansion);
            source.RowsLoaded += loaded =>
            {
                if (!ReferenceEquals(Rows, source)) return;
                if (preservedBrowserKeys is { } keys)
                {
                    restoringBrowserSelection = true;
                    try { foreach (var row in loaded) row.IsSelected = row.StableKey is { } key && keys.Contains(key); }
                    finally { restoringBrowserSelection = false; }
                }
                if (SelectedRow is { } selected && loaded.Contains(selected)) { SelectedMedia = selected.Media; _ = LoadSelectedMediaAsync(selected); }
                Selection = SelectionRows.Select(r => r.Media).OfType<MediaEntry>().ToList(); RefreshCommands();
            };
            var reportedFailure = false;
            source.LoadFailed += ex => { if (!ReferenceEquals(Rows, source) || reportedFailure) return; reportedFailure = true; Status = ex.Message.Split('\n')[0]; Details = ex.Message; Jobs.Add(new("Browser", "Failed", ex.Message)); };
            resultQuery = query; resultGeneration = generation;
            ReplaceRows(source); ResultsText = $"{total:N0} entries";
            var context = EffectiveNode;
            ScopeText = ResultsHeading();
            foreach (var view in QuickViews) view.IsActive = context is null ? ReferenceEquals(view, QuickViews[0]) : ReferenceEquals(view, context);
            UpdateTreeActivity(Roots, context);
            SelectionName = context?.Name ?? "Preview"; Details = BankDetails(context?.Bank);
            if (context?.Event is null) EventDetails = ""; else UpdateEventDetails();
            if (total == 0) EmptyText = Search.Length > 0 || Language != "All languages" || Category != "All types" || Codec != "All codecs" ? "No matches" : node?.Event is { LinkStatus: EventLinkStatus.Missing or EventLinkStatus.Ambiguous } ? "Audio link missing" : node?.Event is not null ? "No audio assigned" : "No matches";
            RefreshCommands();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == queryGeneration && !disposed) Error(ex); }
        finally { if (generation == queryGeneration) IsQuerying = false; if (ReferenceEquals(queryCancellation, cancellation)) queryCancellation = null; cancellation.Dispose(); }
    }
    [RelayCommand] private void ClearSearch() => Search = "";
    private static void UpdateTreeActivity(IEnumerable<LibraryNode> nodes, LibraryNode? active)
    {
        foreach (var item in nodes) { item.IsActive = ReferenceEquals(item, active); UpdateTreeActivity(item.Children, active); }
    }
    [RelayCommand] private void ResetFilters()
    {
        updatingFilters = true; Language = "All languages"; Category = "All types"; Codec = "All codecs"; updatingFilters = false;
        ScheduleQuery();
    }
    [RelayCommand] private void SelectView(LibraryNode view) => SelectNode(view);
    public void SetSelection(IReadOnlyList<VirtualMediaRow> selected)
    {
        if (preservedBrowserKeys is { } preserved && !restoringBrowserSelection)
            foreach (var row in selected) if (row.StableKey is { } key) preserved.Add(key);
        SelectionRows = selected.Where(row => ReferenceEquals(row.Source, Rows)).ToArray(); Selection = SelectionRows.Select(r => r.Media).OfType<MediaEntry>().ToList(); allResultsSelected = false;
        Rows?.KeepSelection(selected);
        ResultsText = selected.Count == 0 ? $"{total:N0} entries" : $"{total:N0} entries · {selected.Count:N0} selected";
        RefreshCommands();
    }
    public void SelectAllResults()
    {
        if (!HasLibrary) return; allResultsSelected = true; SelectionRows = []; Selection = [];
        ResultsText = $"{total:N0} entries · all selected";
        RefreshCommands();
    }
    public void SetSort(string value) { if (Sort == value) Descending = !Descending; else Sort = value; }

    [RelayCommand(CanExecute = nameof(CanPlay), AllowConcurrentExecutions = true)] private async Task PlayAsync()
    {
        if (SelectedMedia is null && node?.Event is not null) { await PlayEventAsync(); return; }
        await PlayMediaAsync(false);
    }
    [RelayCommand(CanExecute = nameof(CanPartialPreview), AllowConcurrentExecutions = true)] private Task PartialPreviewAsync() => PlayMediaAsync(true);
    private async Task PlayMediaAsync(bool partial)
    {
        if (catalog is null || SelectedMedia is not { } media) return;
        if (playingKey == media.Key && player.Length > 0) { player.Toggle(); UpdatePlayback(); return; }
        var session = BeginPreview(media.Key);
        try
        {
            PlaybackMessage = ""; Status = "Preparing preview";
            var prepared = session.Clip = await prepareMedia(catalog, media, session.Token, partial);
            session.Kind = prepared.Kind;
            session.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview, session) || disposed) return;
            await session.Player.LoadAsync(prepared.WavPath, (float)Volume,
                (input, token) => audio.NormalizePreviewToStereoAsync(input, prepared.FallbackWavPath, token), session.Token);
            session.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview, session) || disposed) return;
            playingKey = media.Key;
            if (prepared.Kind == PreviewKind.Complete) media.Duration = prepared.Result.Duration.TotalSeconds;
            Details = MediaDetails(media); Status = prepared.Kind switch { PreviewKind.Partial => "Partial preview", PreviewKind.Available => "Preview", _ => "Playing" }; UpdatePlayback();
        }
        catch (OperationCanceledException) { if (ReferenceEquals(preview, session)) { RetirePreview(); Status = "Ready"; } }
        catch (Exception ex) { if (ReferenceEquals(preview, session) && !disposed) { RetirePreview(); AudioError(ex); } }
        finally { session.MarkPrepared(); activePreviewRequests.Remove(session); if (ReferenceEquals(preview, session)) { PreparingPreview = false; UpdatePlayback(); } else await session.DisposeAsync(); }
    }
    [RelayCommand(CanExecute = nameof(CanEvent), AllowConcurrentExecutions = true)] private async Task PlayEventAsync()
    {
        if (catalog is null || EffectiveNode?.Event is not { } entry) return;
        if (playingKey == entry.Key && player.Length > 0) { player.Toggle(); UpdatePlayback(); return; }
        var bank = node?.Bank;
        var session = BeginPreview(entry.Key);
        try
        {
            Status = "Preparing event";
            var selectedStore = store;
            var data = selectedStore is null ? catalog : await Task.Run(() => selectedStore.CreateOperationCatalog(eventKeys: [entry.Key], token: session.Token), session.Token);
            var plan = await audio.CreateEventPlanAsync(data, entry, MakeEventOptions(), cancellationToken: session.Token);
            session.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview, session) || disposed) return;
            EventDetails = $"{plan.Items.Count} sources · {TimeSpan.FromSeconds(plan.DurationSeconds):m\\:ss}"; Details = BankDetails(bank) + "\n\n" + string.Join("\n", plan.Limitations);
            session.Directory = Path.Combine(Path.GetTempPath(), "DetroitAudio", "preview-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session.Directory);
            var output = Path.Combine(session.Directory, "event.wav"); await audio.RenderAsync(data, plan, output, cancellationToken: session.Token);
            session.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview, session) || disposed) return;
            await session.Player.LoadAsync(output, (float)Volume,
                (input, token) => audio.NormalizePreviewToStereoAsync(input, Path.Combine(session.Directory, "device-stereo.wav"), token), session.Token);
            session.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preview, session) || disposed) return;
            playingKey = entry.Key; PlaybackMessage = ""; Status = "Playing"; UpdatePlayback();
        }
        catch (OperationCanceledException) { if (ReferenceEquals(preview, session)) { RetirePreview(); Status = "Ready"; } }
        catch (Exception ex) { if (ReferenceEquals(preview, session) && !disposed) { RetirePreview(); AudioError(ex); } }
        finally { session.MarkPrepared(); activePreviewRequests.Remove(session); if (ReferenceEquals(preview, session)) { PreparingPreview = false; UpdatePlayback(); } else await session.DisposeAsync(); }
    }
    private EventOptions MakeEventOptions()
    {
        if (!int.TryParse(Seed, out var randomSeed) || !int.TryParse(Loops, out var loopCount) || loopCount is < 1 or > 100) throw new InvalidOperationException("Enter a valid seed and 1–100 loops.");
        var values = new Dictionary<uint, uint>();
        foreach (var line in SwitchValues.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.Split('=', StringSplitOptions.TrimEntries); if (pair.Length != 2) throw new InvalidOperationException("Use group ID = value ID for switches.");
            uint Parse(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.Parse(value[2..], NumberStyles.HexNumber) : uint.Parse(value);
            values[Parse(pair[0])] = Parse(pair[1]);
        }
        return new EventOptions(string.IsNullOrWhiteSpace(EventLanguage) || EventLanguage is "All languages" or "—" ? "ENG" : EventLanguage, randomSeed, loopCount, SwitchValues: values, StateValues: values, RelatedPlayEventKey: RelatedPlayEvent?.Key);
    }
    [RelayCommand] public void Stop() { RetirePreview(); SetPlaybackStatus("Stopped"); UpdatePlayback(); }
    private void SetPlaybackStatus(string value) { idleStatus = value; if (!IsBusy) Status = value; }
    private void UpdatePlayback()
    {
        updatingPosition = true; PositionSeconds = player.Position; LengthSeconds = Math.Max(.001, player.Length); updatingPosition = false;
        TimeText = TimeSpan.FromSeconds(player.Position).ToString(@"m\:ss"); TotalTimeText = TimeSpan.FromSeconds(player.Length).ToString(@"m\:ss"); PlayLabel = player.State == PlaybackState.Playing ? "Pause" : SelectedMedia?.Completeness == MediaCompleteness.Unverified ? "Preview" : "Play";
        PartialPlayLabel = player.State == PlaybackState.Playing && playingKey == SelectedMedia?.Key ? "Pause partial preview" : "Partial preview";
        if (!IsBusy && !PreparingPreview)
        {
            var text = preview is { Prepared: true } && player.Length > 0 ? player.State switch
            {
                PlaybackState.Paused => "Paused",
                PlaybackState.Playing => preview.Kind switch { PreviewKind.Partial => "Partial preview", PreviewKind.Available => "Preview", _ => "Playing" },
                _ => "Finished"
            } : idleStatus;
            updatingStatus = true; Status = text; updatingStatus = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportOriginalAsync() => ExportAsync(ExportFormat.Original);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportFilteredAsync() => ExportAsync(ExportFormat.Original, true);
    [RelayCommand(CanExecute = nameof(CanConvertSelection))] private Task ExportOggAsync() => ExportAsync(ExportFormat.Ogg);
    [RelayCommand(CanExecute = nameof(CanConvertSelection))] private Task ExportWavAsync() => ExportAsync(ExportFormat.Wav);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ConvertFilteredOggAsync() => ExportAsync(ExportFormat.Ogg, true);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ConvertFilteredWavAsync() => ExportAsync(ExportFormat.Wav, true);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportBankAsync() => ExportAsync(ExportFormat.Bank);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportBankMediaAsync() => ExportAsync(ExportFormat.BankWithMedia);
    [RelayCommand(CanExecute = nameof(CanRenderEvents))] private Task ExportEventWavAsync() => ExportAsync(ExportFormat.EventWav);
    [RelayCommand(CanExecute = nameof(CanRenderEvents))] private Task ExportEventOggAsync() => ExportAsync(ExportFormat.EventOgg);
    [RelayCommand(CanExecute = nameof(CanExportFragment))] private Task ExportFragmentAsync() => ExportAsync(ExportFormat.Original, fragmentsOnly: true);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportAllOriginalAsync() => ExportAsync(ExportFormat.Original, wholeLibrary: true);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportAllOggAsync() => ExportAsync(ExportFormat.Ogg, wholeLibrary: true);
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportAllWavAsync() => ExportAsync(ExportFormat.Wav, wholeLibrary: true);
    private async Task ExportAsync(ExportFormat format, bool filtered = false, bool wholeLibrary = false, bool fragmentsOnly = false)
    {
        if (catalog is null || IsBusy) return;
        if (!wholeLibrary && (IsQuerying || resultGeneration != queryGeneration)) { Status = "Wait for search to finish"; return; }
        var data = catalog;
        var context = EffectiveNode;
        var allSelected = allResultsSelected;
        var snapshot = SelectionSnapshot.CaptureBrowser(resultQuery, resultGeneration, SelectionRows, GroupSimilar, new HashSet<string>(expandedGroups))
            with { PreservedBrowserKeys = preservedBrowserKeys is null ? new HashSet<string>() : new HashSet<string>(preservedBrowserKeys) };
        var queryStore = store;
        var librarySelection = EffectiveNode is null ? Array.Empty<LibraryNode>() : SelectedLibraryNodes.ToArray();
        var bankKeys = librarySelection.Select(item => item.Bank?.Key).OfType<string>().Distinct().ToArray();
        var eventKeys = librarySelection.Select(item => item.Event?.Key).OfType<string>().Distinct().ToArray();
        EventOptions? eventOptions = null;
        IsBusy = true;
        var session = StartTask(wholeLibrary ? "Exporting whole library" : "Exporting audio", OperationKind.Export,
            wholeLibrary ? default : viewCancellation.Token);
        var token = session.Token;
        ReportTask(session, "Preparing export");
        try
        {
            if (format is ExportFormat.EventWav or ExportFormat.EventOgg) eventOptions = MakeEventOptions();
            var selected = Selection.Count > 0 ? Selection.ToList() : SelectedMedia is { } entry ? [entry] : new List<MediaEntry>();
            if (fragmentsOnly) selected = SelectedMedia is { } fragment ? [fragment] : [];
            if (!wholeLibrary && !fragmentsOnly && queryStore is not null && format is not (ExportFormat.Bank or ExportFormat.BankWithMedia or ExportFormat.EventWav or ExportFormat.EventOgg))
            {
                if (filtered || allSelected) selected = await Task.Run(() => queryStore.Enumerate(snapshot.Query, token).ToList(), token);
                else if (snapshot.Indices.Length > 0 || snapshot.PreservedBrowserKeys.Count > 0) selected = (await snapshot.ResolveBrowserEntriesAsync(queryStore, token)).ToList();
                else if (selected.Count == 0 && librarySelection.Length > 0) selected = await Task.Run(() => queryStore.Enumerate(snapshot.Query, token).ToList(), token);
            }
            if (queryStore is not null)
            {
                if (wholeLibrary) selected = await Task.Run(() => queryStore.Enumerate(new(), token).ToList(), token);
                else if (format == ExportFormat.BankWithMedia)
                {
                    if (bankKeys.Length == 0 && context?.Bank is { } mediaBank) bankKeys = [mediaBank.Key];
                    if (bankKeys.Length == 0) bankKeys = selected.Select(media => media.BankKey).Where(key => key.Length > 0).Distinct().ToArray();
                    selected = bankKeys.Length == 0 ? [] : await Task.Run(() => queryStore.Enumerate(new(Scope: new CatalogScope(bankKeys)), token).ToList(), token);
                }
                if (format is ExportFormat.EventOgg or ExportFormat.EventWav)
                {
                    if (eventKeys.Length == 0 && context?.Event is { } exportEvent) eventKeys = [exportEvent.Key];
                    data = await Task.Run(() => queryStore.CreateOperationCatalog(eventKeys: eventKeys, token: token), token);
                }
                else data = new AudioCatalog { SourcePath = data.SourcePath, Fingerprint = data.Fingerprint, IndexedResourceCount = queryStore.Header.ResourceCount, Banks = data.Banks, Media = selected };
            }
            token.ThrowIfCancellationRequested();
            var folder = chooseExportFolder();
            if (folder is null) { FinishTask(session, OperationState.Cancelled, "Ready"); return; }
            token.ThrowIfCancellationRequested();
            var overrides = eventOptions is null ? null : eventKeys.ToDictionary(key => key, key => eventOptions with { RelatedPlayEventKey = key == context?.Event?.Key ? eventOptions.RelatedPlayEventKey : null });
            var options = new ExportOptions(folder, format, WavFormat: Enum.TryParse<WavFormat>(settings.WavFormat, out var wav) ? wav : WavFormat.Pcm16,
                EventOptions: eventOptions, Scope: wholeLibrary ? ExportScope.All : filtered || allSelected ? ExportScope.Filtered : ExportScope.Selection,
                AllowEmpty: true, EventOverrides: overrides);
            var service = new ExportService(audio);
            var banks = bankKeys.Length > 0 ? data.Banks.Where(bank => bankKeys.Contains(bank.Key)) : context?.Bank is { } bank ? new[] { bank } : selected.Select(m => data.Banks.FirstOrDefault(b => b.Key == m.BankKey)).OfType<BankInfo>();
            var events = eventKeys.Length > 0 ? data.Events.Where(entry => eventKeys.Contains(entry.Key)) : context?.Event is { } selectedEvent ? new[] { selectedEvent } : Array.Empty<AudioEvent>();
            var proposed = await Task.Run(() => service.Plan(data, options, selected, banks, events, token), token);
            token.ThrowIfCancellationRequested();
            var plan = confirmExport(proposed);
            if (plan is null) { FinishTask(session, OperationState.Cancelled, "Ready"); return; }
            token.ThrowIfCancellationRequested();
            settings.LastExportDirectory = folder; settings.Save();
            // Once accepted, the plan owns its selection independently of browser navigation.
            session.OutputDirectory = folder;
            session.BeginExecution();
            await ExecuteExportAsync(data, plan, session.Token, session);
        }
        catch (OperationCanceledException) { FinishTask(session, OperationState.Cancelled, "Cancelled"); }
        catch (Exception ex) { FinishTask(session, OperationState.Failed, ex.Message.Split('\n')[0]); Error(ex); }
        finally { session.Settle(); IsBusy = false; UpdatePlayback(); }
    }
    private async Task RunExportAsync(ExportPlan plan)
    {
        if (catalog is null || IsBusy) return;
        IsBusy = true; var session = StartTask("Retrying export", OperationKind.Export); session.OutputDirectory = plan.Options.Directory;
        try { await ExecuteExportAsync(lastExportCatalog ?? catalog, plan, session.Token, session); }
        catch (OperationCanceledException) { FinishTask(session, OperationState.Cancelled, "Cancelled"); }
        catch (Exception ex) { FinishTask(session, OperationState.Failed, ex.Message.Split('\n')[0]); Error(ex); }
        finally { session.Settle(); IsBusy = false; UpdatePlayback(); }
    }
    private async Task ExecuteExportAsync(AudioCatalog data, ExportPlan plan, CancellationToken token, OperationSession session)
    {
        lastPlan = plan; lastExportCatalog = data; Jobs.Clear();
        try
        {
            var progress = new Progress<ExportProgress>(p => ReportTask(session, p.Status, p.Completed, p.Total, p.Name));
            lastReport = await new ExportService(audio).RunAsync(data, plan, progress, token);
            foreach (var result in lastReport.Results) Jobs.Add(new(Path.GetFileName(result.File), result.Status, result.Error));
            FinishTask(session, lastReport.Cancelled ? OperationState.Cancelled : OperationState.Completed,
                lastReport.Cancelled ? "Cancelled" : $"{lastReport.Results.Count(r => r.Status == "Complete"):N0} exported · {lastReport.Results.Count(r => r.Status == "Skipped"):N0} skipped · {lastReport.Results.Count(r => r.Status == "Failed"):N0} failed");
        }
        catch (OperationCanceledException) { FinishTask(session, OperationState.Cancelled, "Cancelled"); }
        catch (Exception ex) { FinishTask(session, OperationState.Failed, ex.Message.Split('\n')[0]); Error(ex); }
        finally { RefreshCommands(); }
    }
    [RelayCommand(CanExecute = nameof(CanRetry))] private Task RetryAsync()
    {
        var keys = lastReport!.Results.Where(r => r.Status == "Failed").Select(r => r.Key).ToHashSet(); return RunExportAsync(lastPlan! with { Items = lastPlan!.Items.Where(i => keys.Contains(i.Key)).ToArray() });
    }
    [RelayCommand] private void Tools()
    {
        var dialog = new ToolsWindow(settings) { Owner = owner }; if (dialog.ShowDialog() == true) { settings.Save(); audio = new(AudioToolPaths.Discover(settings.ToolsDirectory)); }
    }
    [RelayCommand(CanExecute = nameof(CanOpen))] private async Task WelcomeAsync()
    {
        var welcome = new WelcomeWindow(settings, settings.LastSource) { Owner = owner };
        var result = welcome.ShowDialog(); audio = new(AudioToolPaths.Discover(settings.ToolsDirectory));
        if (result == true) await OpenAsync(welcome.SelectedSource);
    }
    private bool CanCancel() => IsBusy || PreparingPreview;
    [RelayCommand(CanExecute = nameof(CanCancel))] public void Cancel() { CurrentTask?.Cancel(); preview?.Cancel(); }
    private void AudioError(Exception error)
    {
        if (disposed) return;
        var message = error.Message.Split('\n')[0];
        var diagnostic = string.Join("\n", new[] { error.Message, (error as AudioToolException)?.Diagnostic }.Where(value => !string.IsNullOrWhiteSpace(value)));
        PlaybackMessage = message; Details += "\n\n" + diagnostic;
        if (Status != message) Jobs.Add(new("Playback", "Failed", diagnostic));
        SetPlaybackStatus(message);
    }
    private void Error(Exception error) { if (disposed) return; Status = error.Message.Split('\n')[0]; Jobs.Add(new("Error", "Failed", error.Message)); reportError(error); }
    private static void Replace(ObservableCollection<string> collection, string first, IEnumerable<string> values) { collection.Clear(); collection.Add(first); foreach (var value in values) collection.Add(value); }
    private static string MediaDetails(MediaEntry media)
    {
        var text = new StringBuilder().AppendLine(media.IdText).AppendLine().AppendLine($"Bank: {media.BankName}").AppendLine($"Language: {media.LanguageDisplay}").AppendLine($"Type: {media.Category}").AppendLine($"Codec: {media.Codec}");
        if (media.Midi is null) text.AppendLine($"Channels: {media.Channels}").AppendLine($"Rate: {media.SampleRate:N0} Hz");
        text.AppendLine($"Size: {media.SizeText}").AppendLine($"Status: {media.State}").AppendLine().AppendLine($"File: {Path.GetFileName(media.Slice.FilePath)}").AppendLine($"Offset: 0x{media.Slice.Offset:X}");
        if (media.Midi is { } midi)
        {
            text.AppendLine($"MIDI format: {midi.Format}").AppendLine($"Tracks: {midi.TrackCount}");
            text.AppendLine((midi.Division & 0x8000) == 0 ? $"Ticks per quarter note: {midi.Division}" : $"SMPTE division: 0x{midi.Division:X4}");
        }
        text.AppendLine($"Completeness: {media.Completeness}").AppendLine($"Container: {media.ContainerValidity}");
        if (media.ResolutionDetails.Length > 0) text.AppendLine(media.ResolutionDetails);
        if (media.RawLanguageId is { } languageId) text.AppendLine($"Language ID: {languageId}");
        foreach (var source in media.SourceEvidence)
            text.AppendLine($"Source object: {source.ObjectId} · stream {source.Declaration.StreamType} · size {source.Declaration.InMemorySize:N0} · flags 0x{source.Declaration.Flags:X2} · 0x{source.Offset:X}");
        foreach (var group in media.EquivalenceEvidence)
            text.AppendLine($"{group.SourceLocations.Count:N0} identical source locations · SHA-256 {group.Sha256}");
        foreach (var candidate in media.Candidates)
            text.AppendLine($"Candidate: {Path.GetFileName(candidate.FilePath)} · 0x{candidate.Offset:X} · {candidate.Length:N0} bytes");
        foreach (var rejection in media.CandidateRejections)
            text.AppendLine($"Rejected: {Path.GetFileName(rejection.Slice.FilePath)} · 0x{rejection.Slice.Offset:X} · {rejection.Reason}");
        if (media.Names.Any(name => name.Kind == NameKind.Stored && name.Text == media.Name)) text.AppendLine($"Stored name: {media.Name}");
        if (media.Subtitle.Length > 0) text.AppendLine().AppendLine(media.Subtitle);
        foreach (var name in media.Names)
        {
            text.AppendLine().AppendLine($"{name.Text}\n{name.Kind} · {name.Confidence}\n{Path.GetFileName(name.Source)}");
            text.AppendLine($"{name.Namespace} · {name.Id} · 0x{name.Offset:X}");
            if (name.HashMethod is { } method) text.AppendLine(method);
            if (name.InputSha256 is { } hash) text.AppendLine($"SHA-256: {hash}");
            if (name.CollisionGroup is { } collision) text.AppendLine($"Collision: {collision}");
        }
        return text.ToString();
    }
    private static string BankDetails(BankInfo? bank)
    {
        if (bank is null) return "";
        var summary = $"ID: {bank.Id} / 0x{bank.Id:X8}\nVersion: {bank.Version}\nSize: {bank.Slice.Length:N0} bytes\nObjects: {Math.Max(bank.Objects.Count, bank.IndexedObjectCount):N0}\n\n";
        return summary + string.Join("\n", bank.Diagnostics.GroupBy(item => item.Code).Select(group => group.Key switch
        {
            "AmbiguousExternalMedia" => $"{group.Count():N0} streams with distinct matching payloads",
            "MissingExternalMedia" => $"{group.Count():N0} external streams missing",
            "NonWemSourcePlugin" => $"{group.Count():N0} non-WEM sources",
            _ => $"{group.Key}: {group.Count():N0} · {group.First().Message}"
        }));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Cancel(); InvalidateView(); ReplaceRows(null); timer.Stop();
        taskWindow?.CloseOwned(); taskWindow = null;
        try { settings.Volume = (float)Volume; settings.Save(); }
        finally { RetirePreview(); idlePlayer.Dispose(); viewCancellation.Dispose(); store?.Dispose(); }
    }
}
