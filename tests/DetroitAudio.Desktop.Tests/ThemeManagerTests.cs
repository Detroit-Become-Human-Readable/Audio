using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO.Packaging;
using DetroitAudio.Desktop;
using Xunit;

namespace DetroitAudio.Desktop.Tests;

public sealed class ThemeManagerTests
{
    [Theory]
    [InlineData("System", false, false, "Dark")]
    [InlineData("System", false, true, "Light")]
    [InlineData("System", false, null, "Light")]
    [InlineData("Light", false, false, "Light")]
    [InlineData("Dark", false, true, "Dark")]
    [InlineData("invalid", false, true, "Light")]
    public void SystemThemeFollowsInjectedWindowsPreferenceAndManualChoiceWins(
        string requested, bool highContrast, bool? systemLight, string expected)
    {
        Assert.Equal(expected, ThemeManager.ResolveTheme(requested, highContrast, systemLight));
    }

    [Theory]
    [InlineData("System", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    public void HighContrastOverridesAnySavedTheme(string requested, bool systemLight)
    {
        Assert.Equal("HighContrast", ThemeManager.ResolveTheme(requested, true, systemLight));
    }

    [Fact]
    public void NewSettingsFollowWindowsThemeByDefault()
    {
        Assert.Equal("System", new UserSettings().ThemePreference);
    }

    [Fact]
    public void HighContrastPaletteUsesTheCurrentWindowsSurfaceAndTextColors()
    {
        var colors = OnSta(() =>
        {
            _ = Application.GetResourceStream(new Uri("/" + Uri.EscapeDataString(typeof(App).Assembly.GetName().Name!) + ";component/Themes/Theme.HighContrast.xaml", UriKind.Relative));
            var palette = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/" + Uri.EscapeDataString(typeof(App).Assembly.GetName().Name!) + ";component/Themes/Theme.HighContrast.xaml", UriKind.Absolute)
            };
            var background = Assert.IsType<SolidColorBrush>(palette["BackgroundBrush"]);
            var foreground = Assert.IsType<SolidColorBrush>(palette["TextBrush"]);
            var selectedBackground = Assert.IsType<SolidColorBrush>(palette["SelectedBrush"]);
            var selectedForeground = Assert.IsType<SolidColorBrush>(palette["SelectedTextBrush"]);
            var visual = new Border
            {
                Background = background,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Surface contrast", Foreground = foreground },
                        new TextBlock { Text = "Selection contrast", Foreground = selectedForeground, Background = selectedBackground }
                    }
                }
            };
            visual.Measure(new Size(240, 40));
            visual.Arrange(new Rect(0, 0, 240, 40));
            visual.UpdateLayout();
            return (background.Color, foreground.Color, selectedBackground.Color, selectedForeground.Color);
        });

        Assert.Equal(SystemColors.WindowColor, colors.Item1);
        Assert.Equal(SystemColors.WindowTextColor, colors.Item2);
        Assert.Equal(SystemColors.HighlightColor, colors.Item3);
        Assert.Equal(SystemColors.HighlightTextColor, colors.Item4);
        Assert.NotEqual(colors.Item1, colors.Item2);
        Assert.NotEqual(colors.Item3, colors.Item4);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void AppPalettesKeepTextAndSelectionReadable(string theme)
    {
        var ratios = OnSta(() =>
        {
            _ = Application.GetResourceStream(new Uri("/" + Uri.EscapeDataString(typeof(App).Assembly.GetName().Name!) + ";component/Themes/Theme." + theme + ".xaml", UriKind.Relative));
            var palette = new ResourceDictionary { Source = new Uri("pack://application:,,,/" + Uri.EscapeDataString(typeof(App).Assembly.GetName().Name!) + ";component/Themes/Theme." + theme + ".xaml") };
            Color ColorFor(string key) => Assert.IsType<SolidColorBrush>(palette[key]).Color;
            return (ContrastRatio(ColorFor("PanelBrush"), ColorFor("TextBrush")),
                ContrastRatio(ColorFor("SelectedBrush"), ColorFor("SelectedTextBrush")),
                ContrastRatio(ColorFor("PanelBrush"), ColorFor("MutedBrush")));
        });
        Assert.True(ratios.Item1 >= 4.5);
        Assert.True(ratios.Item2 >= 4.5);
        Assert.True(ratios.Item3 >= 4.5);
    }

    private static double ContrastRatio(System.Windows.Media.Color first, System.Windows.Media.Color second)
    {
        static double Luminance(System.Windows.Media.Color color)
        {
            static double Linear(byte channel)
            {
                var normalized = channel / 255d;
                return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static T OnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("WPF palette inspection failed.", failure);
        return result!;
    }
}
