using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Desktop;
using DetroitAudio.Indexing;
using NAudio.Wave;
using Xunit;
using MediaState = DetroitAudio.Core.MediaState;

namespace DetroitAudio.Desktop.Tests;

[CollectionDefinition("Wpf", DisableParallelization = true)]
public sealed class WpfCollection;

[Collection("Wpf")]
public sealed class BrowserLifecycleTests
{
    [Fact]
    public Task MidiRowsCanExportOriginalButCannotPreviewOrConvert() => Run(async root =>
    {
        Export.ExportPlan? proposed = null;
        using var fixture = new ModelFixture(root, 1, chooseFolder: () => Path.Combine(root, "output"), confirm: plan => { proposed = plan; return null; });
        var media = fixture.Catalog.Media[0]; media.Category = media.Codec = "MIDI"; media.IsRiff = false; media.Midi = new(1, 2, 480);
        var store = (CatalogStore)typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Model)!;
        store.Save(fixture.Catalog, null, default); var model = fixture.Model;
        model.SelectNode(model.QuickViews[0]); await Until(() => !model.IsQuerying); model.Category = "MIDI"; await Until(() => !model.IsQuerying);
        var row = (VirtualMediaRow)model.Rows![0]!; await Until(() => row.Media is not null); model.SelectedRow = row; model.SetSelection([row]);
        await Until(() => model.Details.Contains("Tracks: 2"));
        Assert.False(model.PlayCommand.CanExecute(null)); Assert.False(model.ExportWavCommand.CanExecute(null)); Assert.False(model.ExportOggCommand.CanExecute(null));
        Assert.True(model.ExportOriginalCommand.CanExecute(null)); await model.ExportOriginalCommand.ExecuteAsync(null);
        Assert.EndsWith(".mid", Assert.Single(proposed!.Items).RelativePath);
    });

    [Fact]
    public Task DialogueBranchFiltersBrowsingAndExportsWithoutConstrainingGlobalSearchOrMixedSelections() => Run(async root =>
    {
        Export.ExportPlan? proposed = null;
        using var fixture = new ModelFixture(root, 4, chooseFolder: () => Path.Combine(root, "output"), confirm: plan => { proposed = plan; return null; });
        var bank = new BankInfo { Key = "bank", Name = "Bank" }; fixture.Catalog.Banks.Add(bank);
        foreach (var media in fixture.Catalog.Media) { media.BankKey = bank.Key; media.BankName = bank.Name; }
        var dialogue = new AudioEvent { Key = "dialogue", Id = 10, IsDialogue = true, MediaKeys = ["media-1", "media-3"] };
        fixture.Catalog.Events.Add(dialogue);
        var store = (CatalogStore)typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Model)!;
        store.Save(fixture.Catalog, null, default);
        var model = fixture.Model; model.SelectNode(new LibraryNode { Name = "Dialogue events", Dialogue = true });
        await Until(() => !model.IsQuerying);
        Assert.Equal(2, model.Rows!.Count);
        await model.ExportFilteredCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "media-1", "media-3" }, proposed!.Items.Select(item => item.Key).Order().ToArray());
        model.Search = "Entry"; await Until(() => !model.IsQuerying); Assert.Equal(4, model.Rows!.Count);
        model.SearchScope = "Current view"; await Until(() => !model.IsQuerying); Assert.Equal(2, model.Rows!.Count);
        model.Search = ""; await Until(() => !model.IsQuerying);
        var bankNode = new LibraryNode { Name = bank.Name, Bank = bank };
        var eventNode = new LibraryNode { Name = "Line", Event = dialogue, Dialogue = true };
        model.SelectLibraryNodes([bankNode, eventNode], eventNode); await Until(() => !model.IsQuerying);
        Assert.Equal(4, model.Rows!.Count);
        Assert.True(model.ExportEventWavCommand.CanExecute(null)); Assert.True(model.PlayEventCommand.CanExecute(null));
    });

    [Fact]
    public Task ReadinessKeepsAFailureReportedBeforeTheWaitStarts() => Run(async root =>
    {
        var error = new IOException("Synthetic page failure");
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        using var source = new VirtualMediaSource(1, Dispatcher.CurrentDispatcher, (_, _, _) => { Interlocked.Increment(ref reads); throw error; });
        source.LoadFailed += _ => reported.TrySetResult();
        _ = source[0]; await reported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => source.WaitForFirstRowsAsync(default).WaitAsync(TimeSpan.FromSeconds(2))));
        Assert.Equal(1, reads);
        Assert.Equal("Unable to load", ((VirtualMediaRow)source[0]!).DisplayName);
    });

    [Fact]
    public Task AnUnexpectedlyEmptyFirstPageFailsReadiness() => Run(async root =>
    {
        using var source = new VirtualMediaSource(1, Dispatcher.CurrentDispatcher, (_, _, _) => []);
        var error = await Assert.ThrowsAsync<IOException>(() => source.WaitForFirstRowsAsync(default).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("returned no rows", error.Message);
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => source.WaitForFirstRowsAsync(default)));
    });

    [Fact]
    public Task OpeningReadinessReportsAnAlreadyFailedView() => Run(async root =>
    {
        using var fixture = new ModelFixture(root, 1);
        var error = new IOException("Synthetic page failure"); var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new VirtualMediaSource(1, Dispatcher.CurrentDispatcher, (_, _, _) => throw error);
        source.LoadFailed += _ => reported.TrySetResult(); fixture.Model.Rows = source;
        _ = source[0]; await reported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var waiting = (Task)typeof(MainViewModel).GetMethod("WaitForActiveBrowserAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Model, [CancellationToken.None])!;
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2))));
    });

    [Fact]
    public Task ReadinessKeepsSuccessAfterTheFirstPageLeavesTheCache() => Run(async root =>
    {
        using var source = new VirtualMediaSource(10000, Dispatcher.CurrentDispatcher, (offset, count, _) =>
            Enumerable.Range(offset, count).Select(index => new MediaEntry { Key = "entry-" + index }).ToArray());
        await source.WaitForFirstRowsAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        for (var offset = 1000; offset < 10000; offset += 1000) _ = source[offset];
        Assert.True(source.CachedBlockCount <= VirtualMediaSource.MaximumBlocks);
        await source.WaitForFirstRowsAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
    });

    [Fact]
    public Task AnAdjacentPageFailureDoesNotSettleFirstPageReadiness() => Run(async root =>
    {
        using var release = new ManualResetEventSlim();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new VirtualMediaSource(1000, Dispatcher.CurrentDispatcher, (offset, count, token) =>
        {
            if (offset != 0) throw new IOException("Synthetic adjacent page failure");
            release.Wait(token); return Enumerable.Range(offset, count).Select(index => new MediaEntry { Key = "entry-" + index }).ToArray();
        });
        source.LoadFailed += _ => reported.TrySetResult();
        _ = source[0]; var waiting = source.WaitForFirstRowsAsync(default);
        try { await reported.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(waiting.IsCompleted); }
        finally { release.Set(); }
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    });

    [Fact]
    public Task ScrollingCannotEvictAPendingReadinessPage() => Run(async root =>
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new VirtualMediaSource(10000, Dispatcher.CurrentDispatcher, (offset, count, token) =>
        {
            if (offset == 0) { entered.TrySetResult(); release.Wait(token); }
            return Enumerable.Range(offset, count).Select(index => new MediaEntry { Key = "entry-" + index }).ToArray();
        });
        var waiting = source.WaitForFirstRowsAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var offset = 1000; offset < 10000; offset += 1000) _ = source[offset];
            Assert.True(source.CachedBlockCount <= VirtualMediaSource.MaximumBlocks); Assert.False(waiting.IsCompleted);
        }
        finally { release.Set(); }
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    });

    [Fact]
    public Task RetiringAListCancelsItsFirstRowWaitWithoutCallerCancellation() => Run(async root =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var source = new VirtualMediaSource(500, Dispatcher.CurrentDispatcher, (_, _, token) =>
        { entered.TrySetResult(); release.Wait(); token.ThrowIfCancellationRequested(); return []; });
        var waiting = source.WaitForFirstRowsAsync(default);
        await entered.Task; source.Dispose();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.WaitForFirstRowsAsync(default));
    });

    [Fact]
    public Task OpeningReadinessFollowsTheReplacementView() => Run(async root =>
    {
        using var fixture = new ModelFixture(root, 1);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var model = fixture.Model;
        using var old = new VirtualMediaSource(500, Dispatcher.CurrentDispatcher, (_, _, token) =>
        { entered.Set(); release.Wait(); token.ThrowIfCancellationRequested(); return []; });
        model.Rows = old;
        var waiting = (Task)typeof(MainViewModel).GetMethod("WaitForActiveBrowserAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [CancellationToken.None])!;
        await Until(() => entered.IsSet);
        old.Dispose(); using var replacement = new VirtualMediaSource(0, Dispatcher.CurrentDispatcher, (_, _, _) => []); model.Rows = replacement;
        try { await waiting.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { release.Set(); }
        Assert.False(model.IsBusy);
    });

    [Fact]
    public Task ReplacementSelectionDropsAnExpandedGroupsPreservedMembers() => Run(async root =>
    {
        Export.ExportPlan? proposed = null;
        using var fixture = new ModelFixture(root, 3, chooseFolder: () => Path.Combine(root, "replacement"), confirm: plan => { proposed = plan; return null; });
        fixture.Catalog.Media[0].Name = fixture.Catalog.Media[1].Name = "Group";
        fixture.Catalog.Media[2].Name = "Solo";
        var store = (CatalogStore)typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Model)!;
        store.Save(fixture.Catalog, null, default);
        var model = fixture.Model; model.GroupSimilar = true; model.SelectNode(model.QuickViews[0]); await Until(() => !model.IsQuerying);
        var group = (VirtualMediaRow)model.Rows![0]!; await Until(() => group.IsGroupHeader); model.SetSelection([group]);
        await model.ToggleGroupCommand.ExecuteAsync(group);
        var solo = (VirtualMediaRow)model.Rows![3]!; await Until(() => solo.Media is not null);
        model.BeginUserSelection(); model.SetSelection([solo]);
        await model.ExportOriginalCommand.ExecuteAsync(null);
        Assert.Equal("media-2", Assert.Single(proposed!.Items).Key);
    });

    [Fact]
    public Task SavedReducedMotionAppliesToMainWindowAnimations() => Run(root =>
    {
        using var owner = new DisposableWindow();
        using var model = new MainViewModel(owner.Window, new UserSettings { ReduceMotion = true });
        Assert.True(model.ReducedMotion);
        return Task.CompletedTask;
    });

    private sealed class DisposableWindow : IDisposable
    {
        public Window Window { get; } = new();
        public void Dispose() => Window.Close();
    }

    [Fact]
    public Task SidebarUnionAndMixedSelectionKeepEligibleExportsAvailable() => Run(async root =>
    {
        Export.ExportPlan? proposed = null;
        using var fixture = new ModelFixture(root, 4, chooseFolder: () => Path.Combine(root, "mixed"), confirm: plan => { proposed = plan; return null; });
        var first = new BankInfo { Key = "bank-a", Name = "First" }; var second = new BankInfo { Key = "bank-b", Name = "Second" };
        fixture.Catalog.Banks.AddRange([first, second]);
        for (var index = 0; index < 4; index++) fixture.Catalog.Media[index].BankKey = index < 2 ? first.Key : second.Key;
        fixture.Catalog.Media[1].Completeness = MediaCompleteness.Fragment;
        fixture.Catalog.Media[1].State = MediaState.PrefetchOnly;
        var store = (CatalogStore)typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Model)!;
        store.Save(fixture.Catalog, null, default);
        var nodes = new[] { new LibraryNode { Name = first.Name, Bank = first }, new LibraryNode { Name = second.Name, Bank = second } };
        var model = fixture.Model; model.SelectLibraryNodes(nodes, nodes[1]); await Until(() => !model.IsQuerying);
        Assert.Equal(4, model.Rows!.Count); Assert.Equal("2 banks", model.ScopeText);
        var selected = new[] { (VirtualMediaRow)model.Rows[0]!, (VirtualMediaRow)model.Rows[1]! };
        await Until(() => selected.All(row => row.Media is not null)); model.SetSelection(selected);
        Assert.True(model.ExportWavCommand.CanExecute(null));
        await model.ExportWavCommand.ExecuteAsync(null);
        Assert.Single(proposed!.Items); Assert.Single(proposed.Omitted!);
        Assert.Equal(1, proposed.Counts!.Fragments); Assert.False(model.IsBusy);
    });

    [Fact]
    public Task EventDetailsReplanWhenLoopOptionsChange() => Run(async root =>
    {
        using var fixture = new ModelFixture(root, 1);
        var media = fixture.Catalog.Media[0]; media.Duration = 2; media.SampleRate = 48000; media.Channels = 1;
        var bank = new BankInfo { Key = "bank", Id = 1, Name = "Bank" }; media.BankKey = bank.Key;
        bank.Objects[10] = new() { Id = 10, Type = 4, Actions = [11] };
        bank.Objects[11] = new() { Id = 11, Type = 3, ActionType = 0x0403, TargetId = 12 };
        bank.Objects[12] = new() { Id = 12, Type = 2, LoopCount = 0, Sources = [new(media.Id, 0, 64, 0, 1)] };
        var entry = new AudioEvent { Key = "event", Id = 10, BankKey = bank.Key, Name = "Play_Loop", MediaKeys = [media.Key], Behavior = EventBehavior.Playback };
        fixture.Catalog.Banks.Add(bank); fixture.Catalog.Events.Add(entry);
        var store = (CatalogStore)typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Model)!;
        store.Save(fixture.Catalog, null, default);
        var model = fixture.Model; model.EventLanguage = "ENG";
        model.SelectNode(new LibraryNode { Name = entry.Name, Bank = bank, Event = entry });
        await Until(() => model.EventDetails.Contains("0:04"));
        model.Loops = "5"; model.Loops = "3";
        await Until(() => model.EventDetails.Contains("0:06"));
        Assert.DoesNotContain("0:10", model.EventDetails);
        model.Loops = "invalid"; await Until(() => model.EventDetails.Contains("valid seed"));
        model.Loops = "2"; await Until(() => model.EventDetails.Contains("0:04"));
    });

    [Fact]
    public Task CollapsedGroupSelectionSurvivesExpansionAndExportsAllMembers() => Run(async root =>
    {
        Export.ExportPlan? proposed = null;
        using var fixture = new ModelFixture(root, 6001, chooseFolder: () => Path.Combine(root, "group-export"), confirm: plan => { proposed = plan; return null; });
        foreach (var media in fixture.Catalog.Media) media.Name = "Shared_Clip";
        var store = (CatalogStore)typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Model)!;
        store.Save(fixture.Catalog, null, default);
        var model = fixture.Model; model.GroupSimilar = true; model.SelectNode(model.QuickViews[0]); await Until(() => !model.IsQuerying);
        Assert.Single(model.Rows!);
        var group = (VirtualMediaRow)model.Rows![0]!; await Until(() => group.IsGroupHeader);
        model.SetSelection([group]);
        await model.ToggleGroupCommand.ExecuteAsync(group); Assert.Equal(6002, model.Rows!.Count);
        await model.ExportOriginalCommand.ExecuteAsync(null);
        Assert.Equal(6001, proposed!.Items.Count); Assert.Equal(6001, proposed.Items.Select(item => item.Key).Distinct().Count());
        var expanded = (VirtualMediaRow)model.Rows[0]!; await Until(() => expanded.IsGroupHeader);
        await model.ToggleGroupCommand.ExecuteAsync(expanded); Assert.Single(model.Rows!);
        proposed = null; await model.ExportOriginalCommand.ExecuteAsync(null); Assert.Equal(6001, proposed!.Items.Count);
    });

    [Fact]
    public Task RetryKeepsTheCapturedCatalogAfterTheBrowserSourceChanges() => Run(async root =>
    {
        var destination = Path.Combine(root, "retry-export");
        using var fixture = new ModelFixture(root, 1, chooseFolder: () => destination, confirm: plan =>
        {
            File.Delete(plan.Items.Single().Media!.Slice.FilePath);
            return plan;
        });
        fixture.Catalog.Fingerprint = "captured-source";
        var model = fixture.Model;
        model.SelectedMedia = fixture.Catalog.Media[0];
        await model.ExportOriginalCommand.ExecuteAsync(null);
        Assert.True(model.RetryCommand.CanExecute(null));
        File.WriteAllBytes(fixture.Catalog.Media[0].Slice.FilePath, new byte[64]);
        typeof(MainViewModel).GetField("catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model,
            new AudioCatalog { Fingerprint = "new-browser-source" });
        await model.RetryCommand.ExecuteAsync(null);
        Assert.False(model.RetryCommand.CanExecute(null));
        var completed = Directory.EnumerateFiles(destination, "*.json").Select(path => System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
            .First(document => document.RootElement.GetProperty("entries")[0].GetProperty("Status").GetString() == "Complete");
        using (completed) Assert.Equal("captured-source", completed.RootElement.GetProperty("fingerprint").GetString());
    });

    [Fact]
    public Task RetiredListToleratesBoundGridReadsAndDefersTokensUntilReadersFinish() => Run(async root =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var reads = 0; var failures = 0;
        var source = new VirtualMediaSource(8000, Dispatcher.CurrentDispatcher, (offset, limit, token) =>
        {
            Interlocked.Increment(ref reads); entered.TrySetResult(); release.Wait();
            token.WaitHandle.WaitOne(0); token.ThrowIfCancellationRequested(); return [];
        });
        source.LoadFailed += _ => failures++;
        var grid = new DataGrid { ItemsSource = source };
        var pending = (VirtualMediaRow)source[0]!;
        await entered.Task;
        source.Dispose();
        Assert.NotNull(source[7999]); Assert.Equal(8000, grid.Items.Count);
        var count = Volatile.Read(ref reads); _ = source[4500]; Assert.Equal(count, Volatile.Read(ref reads));
        grid.ItemsSource = null; release.Set();
        await Task.Delay(80);
        Assert.Null(pending.Media); Assert.Equal(0, failures); Assert.Equal(0, source.CachedBlockCount);
        source.Dispose(); release.Dispose();
    });

    [Fact]
    public Task SelectionResolutionReadsEvictedUnloadedRowsInBoundedBlocks() => Run(async root =>
    {
        using var source = new VirtualMediaSource(10000, Dispatcher.CurrentDispatcher, (_, _, _) => []);
        var selected = Enumerable.Range(100, 6001).Select(index => (VirtualMediaRow)source[index]!).ToArray();
        var query = new CatalogQuery(Search: "test", Sort: "ID", Descending: true);
        var snapshot = SelectionSnapshot.Capture(query, 7, selected);
        Assert.True(source.CachedBlockCount <= 8);
        var requested = new List<CatalogQuery>();
        var result = await snapshot.ResolveSelectedEntriesAsync((page, token) =>
        {
            requested.Add(page); Assert.Equal(query.Search, page.Search); Assert.Equal(query.Sort, page.Sort); Assert.True(page.Descending);
            Assert.Equal(500, page.Limit); return Enumerable.Range(page.Offset, page.Limit).Select(index => new MediaEntry { Key = "entry-" + index, Id = (uint)index }).ToArray();
        }, default);
        Assert.Equal(6001, result.Count); Assert.Equal(Enumerable.Range(100, 6001).Select(index => (uint)index), result.Select(entry => entry.Id));
        Assert.Equal(13, requested.Count);
    });

    [Fact]
    public Task SelectionResolutionRejectsChangedIdentitiesAndCancellation() => Run(async root =>
    {
        var snapshot = new SelectionSnapshot(new(), 2, [0, 500], new Dictionary<int, string> { [0] = "original" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.ResolveSelectedEntriesAsync((_, _) => [new() { Key = "changed" }], default));
        using var cancellation = new CancellationTokenSource(); var reads = 0;
        snapshot = snapshot with { Keys = new Dictionary<int, string>() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot.ResolveSelectedEntriesAsync((_, _) =>
        { reads++; cancellation.Cancel(); return Enumerable.Range(0, 500).Select(index => new MediaEntry { Key = "entry-" + index }).ToArray(); }, cancellation.Token));
        Assert.Equal(1, reads);
    });

    [Fact]
    public Task BoundBrowserSurvivesSearchLanguageResetAndStaleResponses() => Run(async root =>
    {
        using var fixture = new ModelFixture(root, 1500);
        var model = fixture.Model;
        var grid = new DataGrid { DataContext = model };
        grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Rows)));
        grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(model.SelectedRow)) { Mode = BindingMode.TwoWay });
        fixture.Owner.Content = grid;
        Assert.Single(model.QuickViews); model.SelectNode(model.QuickViews[0]); await Until(() => !model.IsQuerying);
        model.Category = "Music"; await Until(() => !model.IsQuerying);
        model.Search = "Entry 0002"; await Until(() => !model.IsQuerying);
        Assert.Equal("All audio", model.ScopeText); Assert.True(model.QuickViews[0].IsActive); Assert.Equal("Music", model.Category);
        var previous = model.Rows!; _ = previous[0];
        model.ClearSearchCommand.Execute(null); await Until(() => !model.IsQuerying);
        Assert.Equal("All audio", model.ScopeText); Assert.Equal(750, model.Rows!.Count); Assert.NotNull(previous[0]);
        model.Language = "ENG"; await Until(() => !model.IsQuerying);
        model.Language = "FRE"; await Until(() => !model.IsQuerying);
        Assert.Equal("FRE", model.Language);
        model.Search = "Entry 0010"; model.Search = "Entry 0002"; await Until(() => !model.IsQuerying);
        var row = (VirtualMediaRow)model.Rows![0]!; await Until(() => row.Media is not null);
        Assert.Equal("Entry 0002", row.Media!.Name);
        model.ResetFiltersCommand.Execute(null); await Until(() => !model.IsQuerying);
        Assert.Equal("All languages", model.Language); Assert.Equal("All types", model.Category); Assert.Equal("All codecs", model.Codec);
        model.SelectAllResults(); Assert.Contains("all selected", model.ResultsText);
        Assert.Empty(model.Jobs);
        grid.ItemsSource = null;
    });

    [Fact]
    public Task LargeSelectionExportsExactEntriesAfterEvictionAndViewChangesCancelPreparation() => Run(async root =>
    {
        Export.ExportPlan? proposed = null;
        using var fixture = new ModelFixture(root, 10000, chooseFolder: () => Path.Combine(root, "export"), confirm: plan => { proposed = plan; return null; });
        var model = fixture.Model; model.SelectNode(model.QuickViews[0]); await Until(() => !model.IsQuerying);
        var selected = Enumerable.Range(100, 6001).Select(index => (VirtualMediaRow)model.Rows![index]!).ToArray();
        Assert.Contains(selected, row => row.Media is null); model.SetSelection(selected);
        await model.ExportWavCommand.ExecuteAsync(null);
        Assert.NotNull(proposed); Assert.Equal(6001, proposed.Items.Count);
        Assert.Equal(Enumerable.Range(100, 6001).Select(index => "media-" + index).Order(), proposed.Items.Select(item => item.Key).Order());
        Assert.Equal(Export.ExportScope.Selection, proposed.Options.Scope); Assert.Equal("Ready", model.Status);

        var confirmations = 0;
        ModelFixture? changed = null;
        using (changed = new ModelFixture(Path.Combine(root, "change"), 1000,
            chooseFolder: () => { changed!.Model.Search = "different query"; return Path.Combine(root, "cancelled-output"); },
            confirm: plan => { confirmations++; return plan; }))
        {
            changed.Model.SelectNode(changed.Model.QuickViews[0]); await Until(() => !changed.Model.IsQuerying);
            changed.Model.SelectAllResults(); await changed.Model.ExportOriginalCommand.ExecuteAsync(null);
            Assert.Equal(0, confirmations); Assert.Equal("Cancelled", changed.Model.Status); Assert.False(changed.Model.IsBusy);
            await Until(() => !changed.Model.IsQuerying); Assert.Empty(changed.Model.Jobs);
        }
    });

    [Fact]
    public Task BrowsingDuringAcceptedExportDoesNotCancelItsCapturedSelection() => Run(async root =>
    {
        var output = Path.Combine(root, "output");
        using var fixture = new ModelFixture(root, 1200, chooseFolder: () => output, confirm: plan => plan);
        var model = fixture.Model; model.SelectNode(model.QuickViews[0]); await Until(() => !model.IsQuerying);
        model.SelectAllResults(); var exporting = model.ExportOriginalCommand.ExecuteAsync(null);
        await Until(() => Directory.Exists(output));
        Assert.False(exporting.IsCompleted);
        model.Search = "another result";
        await exporting;
        Assert.Equal(1200, model.Jobs.Count); Assert.All(model.Jobs, job => Assert.Equal("Complete", job.Status));
        Assert.Equal(1200, Directory.EnumerateFiles(output, "*.wem", SearchOption.AllDirectories).Count());
        Assert.False(model.IsBusy); Assert.False(model.CancelCommand.CanExecute(null));
        await Until(() => !model.IsQuerying);
    });

    [Fact]
    public Task CancelledFolderAndReviewLeaveReadyAndWriteNoAudio() => Run(async root =>
    {
        using var folderCancelled = new ModelFixture(Path.Combine(root, "folder"), 8, chooseFolder: () => null);
        folderCancelled.Model.SelectNode(folderCancelled.Model.QuickViews[0]); await Until(() => !folderCancelled.Model.IsQuerying);
        folderCancelled.Model.SelectAllResults();
        await folderCancelled.Model.ExportOriginalCommand.ExecuteAsync(null);
        Assert.Equal("Ready", folderCancelled.Model.Status); Assert.False(folderCancelled.Model.IsBusy); Assert.False(folderCancelled.Model.CancelCommand.CanExecute(null));
        Export.ExportPlan? proposed = null;
        var output = Path.Combine(root, "output");
        using var reviewCancelled = new ModelFixture(Path.Combine(root, "review"), 8, chooseFolder: () => output, confirm: plan => { proposed = plan; return null; });
        reviewCancelled.Model.SelectNode(reviewCancelled.Model.QuickViews[0]); await Until(() => !reviewCancelled.Model.IsQuerying);
        await reviewCancelled.Model.ExportAllWavCommand.ExecuteAsync(null);
        Assert.NotNull(proposed); Assert.Equal(8, proposed.Items.Count);
        Assert.Equal("Ready", reviewCancelled.Model.Status); Assert.False(reviewCancelled.Model.IsBusy); Assert.False(Directory.Exists(output));
    });

    [Fact]
    public Task LatePreviewCancellationAndFailureCannotStopNewPlayerOrChangeStatus() => Run(async root =>
    {
        foreach (var fail in new[] { false, true })
        {
            var first = new TaskCompletionSource<PreviewClip>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0; var factory = new FakeFactory();
            var staleDirectory = Path.Combine(root, fail ? "failed" : "cancelled"); Directory.CreateDirectory(staleDirectory);
            var currentDirectory = Path.Combine(root, fail ? "current-failed" : "current-cancelled"); Directory.CreateDirectory(currentDirectory);
            using var fixture = new ModelFixture(Path.Combine(root, fail ? "case-fail" : "case-cancel"), 2, factory,
                (_, _, _, _) => ++calls == 1 ? first.Task : Task.FromResult(Clip(currentDirectory)));
            fixture.Model.SelectNode(fixture.Model.QuickViews[0]); await Until(() => !fixture.Model.IsQuerying);
            fixture.Model.SelectedMedia = fixture.Catalog.Media[0]; var oldRequest = fixture.Model.PlayCommand.ExecuteAsync(null);
            fixture.Model.SelectedMedia = fixture.Catalog.Media[1]; await fixture.Model.PlayCommand.ExecuteAsync(null);
            Assert.Equal("Playing", fixture.Model.Status);
            if (fail) first.SetException(new IOException("obsolete preview")); else first.SetResult(Clip(staleDirectory));
            await oldRequest;
            Assert.Equal("Playing", fixture.Model.Status); Assert.Empty(fixture.Model.Jobs);
            Assert.Equal(PlaybackState.Playing, Assert.Single(factory.Instances).State); Assert.False(factory.Instances[0].Disposed);
            if (!fail) Assert.False(Directory.Exists(staleDirectory));
            Assert.True(Directory.Exists(currentDirectory));
            await fixture.Model.PlayCommand.ExecuteAsync(null); Assert.Equal("Paused", fixture.Model.Status);
            fixture.Model.Stop(); Assert.Equal("Stopped", fixture.Model.Status); Assert.False(Directory.Exists(currentDirectory));
        }
    });

    [Fact]
    public Task PreparedEventPauseAndResumeReuseItsFrozenPlayback() => Run(async root =>
    {
        var factory = new FakeFactory(); using var fixture = new ModelFixture(root, 1, factory);
        fixture.Model.SelectNode(new LibraryNode { Name = "Event", Event = new AudioEvent { Key = "event", Id = 8, MediaKeys = ["media-0"] } });
        await Until(() => !fixture.Model.IsQuerying);
        var session = (PreviewSession)typeof(MainViewModel).GetMethod("BeginPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Model, ["event"])!;
        await session.Player.LoadAsync("event.wav", .1f); session.Prepared = true; fixture.Model.PreparingPreview = false;
        typeof(MainViewModel).GetField("playingKey", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Model, "event");
        await fixture.Model.PlayCommand.ExecuteAsync(null);
        Assert.Equal(PlaybackState.Paused, session.Player.State); Assert.Equal("Paused", fixture.Model.Status); Assert.False(fixture.Model.PreparingPreview);
        await fixture.Model.PlayCommand.ExecuteAsync(null);
        Assert.Equal(PlaybackState.Playing, session.Player.State); Assert.Single(factory.Instances); Assert.Empty(fixture.Model.Jobs);
        fixture.Model.IsBusy = true; fixture.Model.Status = "Exporting 2 / 4";
        fixture.Model.Stop(); Assert.True(factory.Instances[0].Disposed); Assert.Equal("Exporting 2 / 4", fixture.Model.Status);
        fixture.Model.IsBusy = false; await Until(() => fixture.Model.Status == "Stopped");
    });

    [Fact]
    public Task ReopenAndCloseDuringQueryDoNotPublishDisposedSources() => Run(async root =>
    {
        var file = Path.Combine(root, "tone.wem");
        using (var writer = new WaveFileWriter(file, new WaveFormat(48000, 16, 2))) writer.Write(new byte[4800], 0, 4800);
        using var model = new MainViewModel(new Window(), new UserSettings { CacheDirectory = Path.Combine(root, "cache") });
        await model.OpenAsync(file);
        Assert.NotNull(model.Rows); var old = model.Rows;
        model.Search = "tone"; await model.OpenAsync(file); await Until(() => !model.IsQuerying);
        Assert.NotSame(old, model.Rows); Assert.Empty(model.Jobs);
        model.Search = "pending"; model.Dispose(); await Task.Delay(250);
        Assert.Null(model.Rows); Assert.Empty(model.Jobs);
    });

    private static PreviewClip Clip(string directory) => new(new(Path.Combine(directory, "preview.wav"), WavFormat.Pcm16, 2, 48000, 48000, TimeSpan.FromSeconds(1), "test"), directory);
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF operation did not finish."); await Task.Delay(10); }
    }
    private static Task Run(Func<string, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "detroit-ui-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var previous = Environment.GetEnvironmentVariable("DETROITAUDIO_DATA_ROOT"); Environment.SetEnvironmentVariable("DETROITAUDIO_DATA_ROOT", root);
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(root); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { Environment.SetEnvironmentVariable("DETROITAUDIO_DATA_ROOT", previous); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); try { Directory.Delete(root, true); } catch (IOException) { } }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private sealed class ModelFixture : IDisposable
    {
        public Window Owner { get; } = new();
        public MainViewModel Model { get; }
        public AudioCatalog Catalog { get; } = new() { Fingerprint = "synthetic" };
        public ModelFixture(string root, int count, IPlaybackBackendFactory? factory = null,
            Func<AudioCatalog, MediaEntry, CancellationToken, bool, Task<PreviewClip>>? prepare = null,
            Func<string?>? chooseFolder = null, Func<Export.ExportPlan, Export.ExportPlan?>? confirm = null)
        {
            Directory.CreateDirectory(root); var bytes = Path.Combine(root, "sample.dat"); File.WriteAllBytes(bytes, new byte[64]);
            for (var index = 0; index < count; index++) Catalog.Media.Add(new() { Key = "media-" + index, Id = (uint)index, Name = $"Entry {index:D4}",
                Category = index % 2 == 0 ? "Music" : "Dialogue", Language = index % 3 == 0 ? "ENG" : "FRE", Codec = "PCM", IsRiff = true, Slice = new(bytes, 0, 64), Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid, Availability = SourceAvailability.Available });
            var store = new CatalogStore(Path.Combine(root, "catalog")); store.Save(Catalog, null, default);
            Model = new(Owner, new UserSettings { CacheDirectory = Path.Combine(root, "cache") }, factory, prepare, chooseFolder, confirm);
            typeof(MainViewModel).GetField("catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Model, Catalog);
            typeof(MainViewModel).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Model, store);
            Model.HasLibrary = true;
        }
        public void Dispose() { Owner.Content = null; Model.Dispose(); }
    }
    private sealed class FakeFactory : IPlaybackBackendFactory
    {
        public List<FakeBackend> Instances { get; } = [];
        public IPlaybackBackend Create(string path, float volume, double positionSeconds = 0) { var backend = new FakeBackend { Volume = volume }; Instances.Add(backend); return backend; }
    }
    private sealed class FakeBackend : IPlaybackBackend
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public bool Disposed { get; private set; }
        public PlaybackState State { get; private set; }
        public double Position => 0;
        public double Length => 1;
        public float Volume { get; set; }
        public void Play() => State = PlaybackState.Playing;
        public void Pause() => State = PlaybackState.Paused;
        public void Stop() { State = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new()); }
        public void Dispose() { Disposed = true; State = PlaybackState.Stopped; }
    }
}
