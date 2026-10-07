using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DetroitAudio.Core;
using DetroitAudio.Indexing;

namespace DetroitAudio.Desktop;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool groupSimilar;
    public IReadOnlyList<LibraryNode> SelectedLibraryNodes { get; private set; } = [];
    private readonly HashSet<string> expandedGroups = new(StringComparer.Ordinal);
    private HashSet<string>? preservedBrowserKeys;
    private bool restoringBrowserSelection;
    public void BeginUserSelection() { preservedBrowserKeys = null; }
    partial void OnGroupSimilarChanged(bool value)
    {
        settings.GroupSimilarAudio = value; settings.Save(); expandedGroups.Clear(); ScheduleQuery();
    }

    public void SelectLibraryNodes(IReadOnlyList<LibraryNode> selected, LibraryNode focused)
    {
        foreach (var item in WalkNodes(Roots)) item.IsSelected = selected.Contains(item);
        SelectedLibraryNodes = selected.Where(item => item.Event is not null || item.Bank is not null && !item.IsGroup).Distinct().ToArray();
        ApplyFocusedNode(focused);
    }

    private CatalogScope? SelectedScope()
    {
        if (Search.Length > 0 && SearchScope == "All audio" || SelectedLibraryNodes.Count == 0) return null;
        return new CatalogScope(SelectedLibraryNodes.Where(item => item.Event is null).Select(item => item.Bank!.Key),
            SelectedLibraryNodes.Where(item => item.Event is not null).Select(item => item.Event!.Key));
    }

    private string ResultsHeading()
    {
        if (Search.Length > 0 && SearchScope == "All audio") return "All audio";
        if (SelectedLibraryNodes.Count <= 1) return EffectiveNode?.Name ?? "All audio";
        var banks = SelectedLibraryNodes.Count(item => item.Event is null); var events = SelectedLibraryNodes.Count - banks;
        return string.Join(" · ", new[] { banks > 0 ? $"{banks:N0} {(banks == 1 ? "bank" : "banks")}" : "", events > 0 ? $"{events:N0} {(events == 1 ? "event" : "events")}" : "" }.Where(text => text.Length > 0));
    }

    [RelayCommand]
    private async Task ToggleGroupAsync(VirtualMediaRow row)
    {
        if (!row.IsGroupHeader || row.GroupKey is not { } key || IsQuerying) return;
        // Preserve actual member selection when logical row positions change during expansion.
        if (store is null) return;
        var token = viewCancellation.Token;
        try
        {
        var snapshot = SelectionSnapshot.CaptureBrowser(resultQuery, resultGeneration, SelectionRows, GroupSimilar, expandedGroups)
            with { PreservedBrowserKeys = preservedBrowserKeys ?? new HashSet<string>() };
        preservedBrowserKeys = await snapshot.ResolveBrowserKeysAsync(store, token);
        if (!expandedGroups.Add(key)) expandedGroups.Remove(key);
        await QueryAsync(false, preserveSelection: true);
        ResultsText = $"{total:N0} entries · {preservedBrowserKeys.Count:N0} selections";
        RefreshCommands();
        }
        catch (OperationCanceledException) { }
    }

    public static IEnumerable<LibraryNode> WalkNodes(IEnumerable<LibraryNode> roots)
    {
        foreach (var item in roots) { yield return item; foreach (var child in WalkNodes(item.Children)) yield return child; }
    }

    private void ScheduleEventDetails()
    {
        if (node?.Event is not { } entry || catalog is null || disposed) return;
        _ = UpdateEventDetailsAsync(entry, debounce: true);
    }
    private async Task WaitForActiveBrowserAsync(CancellationToken token)
    {
        while (!disposed)
        {
            token.ThrowIfCancellationRequested();
            var source = Rows;
            if (IsQuerying || source is null || source.IsRetired) { await Task.Delay(15, token); continue; }
            try { await source.WaitForFirstRowsAsync(token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && (source.IsRetired || !ReferenceEquals(Rows, source))) { continue; }
            if (!IsQuerying && ReferenceEquals(Rows, source)) return;
        }
        throw new OperationCanceledException(token);
    }
}
