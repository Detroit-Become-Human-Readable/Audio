using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using DetroitAudio.Audio;
using Microsoft.Win32;

namespace DetroitAudio.Desktop;

public sealed class ToolsWindow : Window
{
    public ToolsWindow(UserSettings settings)
    {
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        Title = "Tools"; Width = 620; Height = 500; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        TextBox Field(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) }; var box = new TextBox { Text = value };
            var browse = new Button { Content = "Browse", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(box); panel.Children.Add(row);
            browse.Click += (_, _) => { var dialog = new OpenFolderDialog { Title = label }; if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName; };
            return box;
        }
        var tools = Field("Tools folder", settings.ToolsDirectory); var cache = Field("Cache folder", settings.CacheDirectory);
        var status = new TextBlock { Margin = new Thickness(0, 0, 0, 15), TextWrapping = TextWrapping.Wrap };
        void Refresh()
        {
            try
            {
                var paths = AudioToolPaths.Discover(tools.Text);
                status.Text = string.Join("\n", new[] { ("vgmstream", paths.VgmstreamCli), ("FFmpeg", paths.Ffmpeg), ("FFprobe", paths.Ffprobe), ("ww2ogg", paths.Ww2Ogg), ("ReVorb", paths.Revorb) }.Select(p => $"{p.Item1}   {(p.Item2 is null ? "Missing" : "Ready")}"));
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        tools.TextChanged += (_, _) => Refresh(); panel.Children.Add(status); Refresh();
        var formats = new ComboBox { ItemsSource = new[] { "Pcm16", "Pcm24", "Float32" }, SelectedItem = settings.WavFormat, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock { Text = "WAV format", Margin = new Thickness(0, 0, 0, 5) }); panel.Children.Add(formats);
        var theme = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = settings.ThemePreference, Margin = new Thickness(0, 0, 0, 14) }; AutomationProperties.SetName(theme, "Appearance theme");
        panel.Children.Add(new TextBlock { Text = "Appearance", Margin = new Thickness(0, 0, 0, 5) }); panel.Children.Add(theme);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var save = new Button { Content = "Save", IsDefault = true }; actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        save.Click += (_, _) =>
        {
            try
            {
                var directory = Path.GetFullPath(cache.Text); Directory.CreateDirectory(directory);
                settings.ToolsDirectory = string.IsNullOrWhiteSpace(tools.Text) ? "" : Path.GetFullPath(tools.Text); settings.CacheDirectory = directory; settings.WavFormat = (string?)formats.SelectedItem ?? "Pcm16"; settings.ThemePreference = (string?)theme.SelectedItem ?? "System"; ThemeManager.SetPreference(settings.ThemePreference); DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Tools", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
    }
}
