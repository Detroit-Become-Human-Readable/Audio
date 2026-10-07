using System.Windows;
using System.Windows.Controls;
using DetroitAudio.Export;

namespace DetroitAudio.Desktop;

public sealed class ExportWindow : Window
{
    private readonly CheckBox overwrite = new() { Content = "Replace existing files", Margin = new Thickness(0, 12, 0, 12) };
    public bool Overwrite => overwrite.IsChecked == true;
    public ExportWindow(ExportPlan plan)
    {
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        Title = "Export"; Width = 720; Height = 500; MinWidth = 500; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(22) }; Content = panel;
        panel.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(new TextBlock { Text = $"{plan.Items.Count:N0} files", FontSize = 20 });
        if (plan.Options.Scope == ExportScope.All)
            header.Children.Add(new TextBlock { Text = "This exports the whole library and can take a long time.", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (plan.Counts is { } counts) header.Children.Add(new TextBlock { Text = $"{counts.Complete:N0} complete · {counts.Fragments:N0} fragments · {counts.Unverified:N0} unverified · {counts.Unavailable:N0} unavailable", Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (plan.Omitted is { Count: > 0 }) header.Children.Add(new TextBlock { Text = $"{plan.Omitted.Count:N0} entries excluded", Margin = new Thickness(0, 5, 0, 0) });
        header.Children.Add(new TextBlock { Text = plan.Options.Directory, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom); bottom.Children.Add(overwrite);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var export = new Button { Content = "Export", IsDefault = true, IsEnabled = plan.Items.Count > 0 }; export.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(export); bottom.Children.Add(actions);
        var list = new ListBox { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), ItemsSource = plan.Items.Select(i => i.RelativePath + (File.Exists(Path.Combine(plan.Options.Directory, i.RelativePath)) ? "  ·  Exists" : "")).ToArray() };
        list.SetResourceReference(ForegroundProperty, "TextBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto); VirtualizingPanel.SetIsVirtualizing(list, true); panel.Children.Add(list);
    }
}
