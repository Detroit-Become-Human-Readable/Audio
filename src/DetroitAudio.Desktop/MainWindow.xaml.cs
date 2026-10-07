using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DetroitAudio.Core;

namespace DetroitAudio.Desktop;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private bool handlingTreeInput, closeApproved;
    private int brandLogoClicks;
    private LibraryNode? treeAnchor;
    public MainWindow(Action<Exception>? reportError = null)
    {
        InitializeComponent();
        ViewModel = new MainViewModel(this, reportError: reportError);
        DataContext = ViewModel;
        Closed += (_, _) => ViewModel.Dispose();
        Closing += OnClosing;
        LibraryTree.PreviewMouseLeftButtonDown += OnLibraryMouseDown;
        LibraryTree.PreviewKeyDown += OnLibraryKeyDown;
        MediaGrid.PreviewMouseRightButtonDown += OnMediaRightButtonDown;
        MediaGrid.PreviewMouseLeftButtonDown += (_, e) => { if (FindParent<Button>(e.OriginalSource as DependencyObject) is null) ViewModel.BeginUserSelection(); };
        MediaGrid.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.A or Key.Space) ViewModel.BeginUserSelection(); };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && MediaGrid.IsKeyboardFocusWithin) { e.Handled = true; MediaGrid.UnselectAll(); ViewModel.SelectAllResults(); return; }
            if (e.Key == Key.Space && Keyboard.FocusedElement is not (TextBox or Button or ComboBox or TreeViewItem) && ViewModel.PlayCommand.CanExecute(null)) { e.Handled = true; await ViewModel.PlayCommand.ExecuteAsync(null); }
        };
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        if (ViewModel.IsBusy || ViewModel.PreparingPreview)
        {
            e.Cancel = true;
            await ViewModel.CancelAndWaitAsync(); closeApproved = true; Close();
        }
        else ViewModel.Cancel();
    }
    private void OnMediaRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is { } row && !row.IsSelected)
        { ViewModel.BeginUserSelection(); MediaGrid.UnselectAll(); row.IsSelected = true; }
    }
    private void OnBrandLogoClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (++brandLogoClicks < 3) return;
        brandLogoClicks = 0;
        if (ViewModel.ReducedMotion)
        {
            BrandLogoRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            BrandLogoRotation.Angle = 0;
            return;
        }

        var spin = new DoubleAnimation(0, 720, TimeSpan.FromMilliseconds(800))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        spin.Completed += (_, _) => BrandLogoRotation.Angle = 0;
        BrandLogoRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
    }
    private void OnTreeSelection(object sender, RoutedPropertyChangedEventArgs<object> e) { if (!handlingTreeInput && e.NewValue is LibraryNode node) { treeAnchor = node; ViewModel.SelectNode(node); } }
    private void OnLibraryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindParent<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext is not LibraryNode item || !Selectable(item)) return;
        handlingTreeInput = true;
        try { ApplyLibrarySelection(item, Keyboard.Modifiers); e.Handled = true; LibraryTree.Focus(); }
        finally { handlingTreeInput = false; }
    }
    private void OnLibraryKeyDown(object sender, KeyEventArgs e)
    {
        var visible = VisibleLibraryNodes().Where(Selectable).ToList();
        if (visible.Count == 0) return;
        var focused = ViewModel.SelectedLibraryNodes.LastOrDefault();
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { ViewModel.SelectLibraryNodes(visible, focused ?? visible[0]); e.Handled = true; return; }
        if (e.Key is not (Key.Up or Key.Down)) return;
        var index = focused is null ? 0 : Math.Max(0, visible.IndexOf(focused));
        index = Math.Clamp(index + (e.Key == Key.Up ? -1 : 1), 0, visible.Count - 1);
        ApplyLibrarySelection(visible[index], Keyboard.Modifiers); e.Handled = true;
    }
    private void ApplyLibrarySelection(LibraryNode item, ModifierKeys modifiers)
    {
        var selected = ViewModel.SelectedLibraryNodes.ToList();
        if (modifiers.HasFlag(ModifierKeys.Shift) && treeAnchor is not null)
        {
            var visible = VisibleLibraryNodes().Where(Selectable).ToList(); var start = visible.IndexOf(treeAnchor); var end = visible.IndexOf(item);
            if (start >= 0 && end >= 0)
            {
                if (!modifiers.HasFlag(ModifierKeys.Control)) selected.Clear();
                selected.AddRange(visible.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1));
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control)) { if (!selected.Remove(item)) selected.Add(item); treeAnchor = item; }
        else { selected = [item]; treeAnchor = item; }
        ViewModel.SelectLibraryNodes(selected.Distinct().ToArray(), item);
    }
    private IEnumerable<LibraryNode> VisibleLibraryNodes()
    {
        return Visible(ViewModel.Roots);
        static IEnumerable<LibraryNode> Visible(IEnumerable<LibraryNode> nodes)
        { foreach (var item in nodes) { yield return item; if (item.IsExpanded) foreach (var child in Visible(item.Children)) yield return child; } }
    }
    private static bool Selectable(LibraryNode node) => node.Event is not null || node.Bank is not null && !node.IsGroup;
    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    { while (source is not null) { if (source is T found) return found; source = source is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); } return null; }
    private void OnMediaSelection(object sender, SelectionChangedEventArgs e) => ViewModel.SetSelection(MediaGrid.SelectedItems.OfType<VirtualMediaRow>().ToList());
    private async void OnMediaDoubleClick(object sender, MouseButtonEventArgs e) { if (ViewModel.PlayCommand.CanExecute(null)) await ViewModel.PlayCommand.ExecuteAsync(null); }
    private void OnExportMenu(object sender, RoutedEventArgs e) { if (sender is Button { ContextMenu: { } menu } button) { menu.DataContext = ViewModel; menu.PlacementTarget = button; menu.IsOpen = true; } }
    private void OnGridSorting(object sender, DataGridSortingEventArgs e) { e.Handled = true; ViewModel.SetSort(e.Column.SortMemberPath); }
    private async void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await ViewModel.OpenAsync(files[0]); }
}
