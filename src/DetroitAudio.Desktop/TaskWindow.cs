using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace DetroitAudio.Desktop;

public sealed class TaskWindow : Window
{
    private bool allowClose;
    public OperationSession Session { get; }
    public TaskWindow(Window owner, OperationSession session)
    {
        Owner = owner; Session = session; DataContext = session;
        Title = session.Title; Width = 540; Height = 300; MinWidth = 440; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; ResizeMode = ResizeMode.CanMinimize;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var panel = new Grid { Margin = new Thickness(24) }; Content = panel;
        panel.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            panel.RowDefinitions.Add(new RowDefinition { Height = height });
        var heading = new TextBlock { Text = session.Title, FontSize = 22, Margin = new Thickness(0, 0, 0, 18) };
        heading.SetResourceReference(StyleProperty, "HeadingTextStyle"); panel.Children.Add(heading);
        var phase = new TextBlock { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        phase.SetBinding(TextBlock.TextProperty, new Binding(nameof(session.Phase))); Grid.SetRow(phase, 1); panel.Children.Add(phase);
        var progress = new ProgressBar { Height = 12, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 0, 0, 10) };
        progress.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(session.Progress)));
        progress.SetBinding(ProgressBar.IsIndeterminateProperty, new Binding(nameof(session.Indeterminate)));
        Grid.SetRow(progress, 2); panel.Children.Add(progress);
        var counts = new TextBlock(); counts.SetBinding(TextBlock.TextProperty, new Binding(nameof(session.Counts)));
        Grid.SetRow(counts, 3); panel.Children.Add(counts);
        var detail = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 10, 0, 0) };
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(session.Detail))); detail.SetBinding(ToolTipProperty, new Binding(nameof(session.Detail)));
        Grid.SetRow(detail, 4); panel.Children.Add(detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel" }; cancel.SetBinding(Button.CommandProperty, new Binding(nameof(session.CancelCommand))); actions.Children.Add(cancel);
        var hide = new Button { Content = "Hide" }; hide.Click += (_, _) => Hide(); actions.Children.Add(hide);
        Grid.SetRow(actions, 5); panel.Children.Add(actions);
        session.PropertyChanged += UpdateFinished;
        Closing += (_, args) => { if (!allowClose) { args.Cancel = true; Hide(); } };
        Closed += (_, _) => session.PropertyChanged -= UpdateFinished;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && session.CanCancel) { args.Handled = true; session.Cancel(); } };
        void UpdateFinished(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(session.IsFinished)) hide.Content = session.IsFinished ? "Close" : "Hide";
        }
    }
    public void CloseOwned() { allowClose = true; Close(); }
}
