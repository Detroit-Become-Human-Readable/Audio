using System.Collections;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DetroitAudio.Core;

namespace DetroitAudio.Desktop;

/// <summary>A logical list whose rows are fetched only as WPF realizes them.</summary>
public sealed class VirtualMediaSource : IList, IDisposable
{
    public const int BlockSize = 500;
    public const int MaximumBlocks = 8;
    private readonly Dispatcher dispatcher;
    private readonly Func<int, int, CancellationToken, IReadOnlyList<MediaEntry>>? read;
    private readonly Func<int, int, CancellationToken, IReadOnlyList<BrowserRow>>? readBrowser;
    public IReadOnlySet<string> ExpandedGroups { get; private set; } = new HashSet<string>();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object lifetimeGate = new();
    // Store errors as results so an unwatched page failure cannot fault an unobserved task.
    private readonly TaskCompletionSource<Exception?> firstPageOutcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, Block> blocks = [];
    private readonly Dictionary<int, VirtualMediaRow> selectedRows = [];
    private long access;
    private bool disposed;
    private int activeLoads;
    public int Count { get; }
    public int CachedBlockCount => blocks.Count;
    public bool IsRetired => disposed;
    public event Action<VirtualMediaRow>? RowLoaded;
    public event Action<IReadOnlyList<VirtualMediaRow>>? RowsLoaded;
    public event Action<Exception>? LoadFailed;
    public async Task WaitForFirstRowsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); lifetimeToken.ThrowIfCancellationRequested();
        if (Count == 0) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken);
        if (!firstPageOutcome.Task.IsCompleted) GetBlock(0);
        var error = await firstPageOutcome.Task.WaitAsync(cancellation.Token);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public VirtualMediaSource(int count, Dispatcher dispatcher, Func<int, int, CancellationToken, IReadOnlyList<MediaEntry>> read)
    { Count = Math.Max(0, count); this.dispatcher = dispatcher; this.read = read; lifetimeToken = lifetime.Token; }
    private VirtualMediaSource(int count, Dispatcher dispatcher, Func<int, int, CancellationToken, IReadOnlyList<BrowserRow>> readBrowser, IReadOnlySet<string> expanded)
    { Count = Math.Max(0, count); this.dispatcher = dispatcher; this.readBrowser = readBrowser; ExpandedGroups = new HashSet<string>(expanded); lifetimeToken = lifetime.Token; }
    public static VirtualMediaSource CreateBrowser(int count, Dispatcher dispatcher,
        Func<int, int, CancellationToken, IReadOnlyList<BrowserRow>> readBrowser, IReadOnlySet<string> expanded) => new(count, dispatcher, readBrowser, expanded);

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            // WPF may finish reading the previous ItemsSource after a binding replacement.
            if (disposed) return selectedRows.GetValueOrDefault(index) ?? new VirtualMediaRow(this, index);
            var blockIndex = index / BlockSize;
            var block = GetBlock(blockIndex);
            // Adjacent blocks are loaded without materializing the rest of the catalog.
            if (blockIndex > 0) GetBlock(blockIndex - 1);
            if ((blockIndex + 1) * BlockSize < Count) GetBlock(blockIndex + 1);
            return block.Rows[index % BlockSize];
        }
        set => throw new NotSupportedException();
    }

    private Block GetBlock(int index)
    {
        if (blocks.TryGetValue(index, out var existing)) { existing.Access = ++access; return existing; }
        while (blocks.Count >= MaximumBlocks)
        {
            // Readiness must settle even if scrolling moves beyond the initial page.
            var oldest = blocks.Where(pair => pair.Key != 0 || firstPageOutcome.Task.IsCompleted).MinBy(pair => pair.Value.Access);
            blocks.Remove(oldest.Key); oldest.Value.Cancel();
        }
        var start = index * BlockSize;
        var rowItems = Enumerable.Range(start, Math.Min(BlockSize, Count - start)).Select(position => selectedRows.GetValueOrDefault(position) ?? new VirtualMediaRow(this, position)).ToArray();
        var block = new Block(rowItems, CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken)) { Access = ++access };
        blocks.Add(index, block);
        lock (lifetimeGate) activeLoads++;
        _ = LoadAsync(index, block);
        return block;
    }

    private async Task LoadAsync(int index, Block block)
    {
        var token = block.Token;
        try
        {
            var entries = await Task.Run(() => readBrowser is not null ? readBrowser(index * BlockSize, block.Rows.Length, token)
                : read!(index * BlockSize, block.Rows.Length, token).Select(media => new BrowserRow(BrowserRowKind.Media, media.Key, null, media.DisplayName, 1, MediaRow.From(media))).ToArray(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (index == 0 && entries.Count == 0) throw new IOException("The first audio page returned no rows. Refresh the library and try again.");
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed || token.IsCancellationRequested || !blocks.TryGetValue(index, out var current) || !ReferenceEquals(current, block)) return;
                for (var i = 0; i < entries.Count && i < block.Rows.Length; i++) { block.Rows[i].Load(entries[i]); RowLoaded?.Invoke(block.Rows[i]); }
                if (index == 0) firstPageOutcome.TrySetResult(null);
                RowsLoaded?.Invoke(block.Rows);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!disposed && !token.IsCancellationRequested) await dispatcher.InvokeAsync(() =>
            {
                if (disposed || token.IsCancellationRequested || !blocks.TryGetValue(index, out var current) || !ReferenceEquals(current, block)) return;
                foreach (var row in block.Rows) row.Fail();
                if (index == 0) firstPageOutcome.TrySetResult(ex);
                LoadFailed?.Invoke(ex);
            });
        }
        finally
        {
            block.Dispose();
            lock (lifetimeGate) { activeLoads--; if (disposed && activeLoads == 0) lifetime.Dispose(); }
        }
    }

    public int IndexOf(object? value)
    {
        return value is VirtualMediaRow row && ReferenceEquals(row.Source, this) ? row.Index : -1;
    }
    public void KeepSelection(IReadOnlyList<VirtualMediaRow> selected)
    { selectedRows.Clear(); foreach (var row in selected.Where(r => ReferenceEquals(r.Source, this))) selectedRows[row.Index] = row; }
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public void CopyTo(Array array, int index) { for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    public IEnumerator GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Dispose()
    {
        lock (lifetimeGate)
        {
            if (disposed) return; disposed = true; lifetime.Cancel();
            if (activeLoads == 0) lifetime.Dispose();
        }
        foreach (var block in blocks.Values) block.Cancel();
        blocks.Clear(); selectedRows.Clear();
    }
    private sealed class Block : IDisposable
    {
        public long Access;
        public VirtualMediaRow[] Rows { get; }
        private readonly CancellationTokenSource cancellation;
        private readonly object gate = new();
        private bool disposed;
        public CancellationToken Token { get; }
        public Block(VirtualMediaRow[] rows, CancellationTokenSource cancellation)
        { Rows = rows; this.cancellation = cancellation; Token = cancellation.Token; }
        public void Cancel() { lock (gate) { if (!disposed) cancellation.Cancel(); } }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; cancellation.Dispose(); } }
    }
}

public sealed class VirtualMediaRow : INotifyPropertyChanged
{
    internal VirtualMediaSource Source { get; }
    internal int Index { get; }
    internal VirtualMediaRow(VirtualMediaSource source, int index) { Source = source; Index = index; }
    public MediaEntry? Media { get; private set; }
    public BrowserRow? Projection { get; private set; }
    public bool IsGroupHeader => Projection?.Kind == BrowserRowKind.GroupHeader;
    public string? StableKey => Projection?.Key ?? Media?.Key;
    public string? GroupKey => Projection?.GroupKey;
    public bool IsGroupChild => !IsGroupHeader && GroupKey is not null;
    public bool IsExpanded => GroupKey is { } key && Source.ExpandedGroups.Contains(key);
    public string ExpandGlyph => IsGroupHeader ? IsExpanded ? "▾" : "▸" : "";
    public System.Windows.Thickness NameIndent => IsGroupChild ? new(18, 0, 0, 0) : new(0);
    private bool failed;
    private bool isSelected;
    public bool IsSelected { get => isSelected; set { if (isSelected == value) return; isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public string DisplayName => IsGroupHeader ? $"{Projection!.Label} ({Projection.Count:N0})" : Media?.DisplayName ?? (failed ? "Unable to load" : "Loading…");
    public uint? Id => Media?.Id;
    public string BankName => Media?.BankName ?? "";
    public string LanguageDisplay => Media?.LanguageDisplay ?? "";
    public string DurationText => Media?.DurationText ?? "";
    public string SizeText => Media?.SizeText ?? "";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Load(MediaEntry media) { Media = media; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    internal void Load(BrowserRow row) { Projection = row; Media = row.Media?.ToSummary(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    internal void Fail() { failed = true; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
}
