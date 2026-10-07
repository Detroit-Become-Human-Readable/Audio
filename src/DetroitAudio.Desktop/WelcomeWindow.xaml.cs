using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace DetroitAudio.Desktop;

public partial class WelcomeWindow : Window
{
    private bool closeApproved;
    public WelcomeViewModel ViewModel { get; }
    public string SelectedSource => ViewModel.SelectedSource;
    public event EventHandler? OpenRequested;
    public WelcomeWindow(UserSettings settings, string? initialSource = null, bool automaticSetup = true)
    {
        InitializeComponent();
        ViewModel = new(this, settings, initialSource); DataContext = ViewModel;
        ViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewModel.ReduceMotion)) UpdateAnimation(); };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Loaded += async (_, _) => { UpdateAnimation(); if (automaticSetup) await ViewModel.InitializeAsync(); };
        Closing += async (_, args) =>
        {
            if (closeApproved || !ViewModel.Busy) return;
            args.Cancel = true; await ViewModel.CancelAndWaitAsync(); closeApproved = true; Close();
        };
        Closed += (_, _) => { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; ViewModel.Dispose(); };
    }
    public void StopAnimations()
    {
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null); LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        LogoScale.ScaleX = LogoScale.ScaleY = 1;
    }
    private void UpdateAnimation()
    {
        StopAnimations();
        if (ThemeManager.IsReducedMotion(ViewModel.ReduceMotion)) return;
        var pulse = new DoubleAnimation(1, 1.055, TimeSpan.FromSeconds(2.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse); LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
    }
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Accessibility or UserPreferenceCategory.General)) return;
        _ = Dispatcher.BeginInvoke(UpdateAnimation, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    private void OpenLibrary(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOpen) return;
        ViewModel.Save();
        if (OpenRequested is not null) OpenRequested(this, EventArgs.Empty);
        else DialogResult = true;
    }
}
