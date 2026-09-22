using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Crescendo.Services;
using Crescendo.ViewModels;

namespace Crescendo.Views;

public partial class SettingsView : UserControl
{
    private bool _loading;
    private Button? _recordingButton;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += (_, _) => LoadFromSettings();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e) => LoadFromSettings();

    private void LoadFromSettings()
    {
        if (ViewModel is null) return;
        AppSettings settings = ViewModel.Settings;

        _loading = true;
        try
        {
            ThemeSystem.IsChecked = settings.Theme == AppTheme.System;
            ThemeDark.IsChecked = settings.Theme == AppTheme.Dark;
            ThemeLight.IsChecked = settings.Theme == AppTheme.Light;

            // The task is the source of truth, not the stored flag: the user may
            // have removed it in Task Scheduler since the last run.
            settings.StartWithWindows = StartupService.IsEnabled();
            StartWithWindowsToggle.IsChecked = settings.StartWithWindows;
            StartMinimisedToggle.IsChecked = settings.StartMinimized;
            MinimiseToTrayToggle.IsChecked = settings.MinimizeToTray;
            CloseToTrayToggle.IsChecked = settings.CloseToTray;

            StepSlider.Value = settings.BoostStepPercent;
            StepValue.Text = $"{settings.BoostStepPercent}%";

            RefreshHotkeyLabels();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshHotkeyLabels()
    {
        if (ViewModel is null) return;
        AppSettings settings = ViewModel.Settings;

        BoostUpHotkey.Content = settings.BoostUp.ToString();
        BoostDownHotkey.Content = settings.BoostDown.ToString();
        ToggleHotkey.Content = settings.ToggleEngine.ToString();
        ResetHotkey.Content = settings.ResetBoost.ToString();
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || ViewModel is null) return;
        if (sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse(tag, out AppTheme theme)) return;

        ViewModel.Settings.Theme = theme;
        ViewModel.SaveNow();
        ThemeService.Apply(theme);
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ViewModel is null) return;

        bool wanted = StartWithWindowsToggle.IsChecked == true;
        bool applied = StartupService.SetEnabled(wanted, ViewModel.Settings.StartMinimized);

        if (!applied)
        {
            // Reflect what actually happened rather than what was asked for.
            StartWithWindowsToggle.IsChecked = !wanted;
            MessageBox.Show(
                "Windows would not let Crescendo change its startup task. Try again, or add it manually in Task Scheduler.",
                "Crescendo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ViewModel.Settings.StartWithWindows = wanted;
        ViewModel.SaveNow();
    }

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || ViewModel is null) return;

        AppSettings settings = ViewModel.Settings;
        settings.StartMinimized = StartMinimisedToggle.IsChecked == true;
        settings.MinimizeToTray = MinimiseToTrayToggle.IsChecked == true;
        settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        ViewModel.SaveNow();
    }

    private void OnStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ViewModel is null) return;

        int step = (int)Math.Round(e.NewValue / 5) * 5;
        ViewModel.Settings.BoostStepPercent = Math.Clamp(step, 5, 100);
        StepValue.Text = $"{ViewModel.Settings.BoostStepPercent}%";
        ViewModel.SaveNow();
    }

    // ---------------------------------------------------------------- hotkeys

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button button) return;

        CancelRecording();

        _recordingButton = button;
        button.Content = "Press a combination…";
        button.Focus();
        button.PreviewKeyDown += OnHotkeyKeyDown;
        button.LostFocus += OnHotkeyLostFocus;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null || sender is not Button button) return;

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            CancelRecording();
            RefreshHotkeyLabels();
            return;
        }

        // Modifier-only presses are part of the combination being typed.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

        if (parts.Count == 0)
        {
            // Windows refuses unmodified global hotkeys, and rightly so: one
            // would swallow that key for every application on the machine.
            button.Content = "Add Ctrl, Alt or Shift";
            return;
        }

        var binding = new HotkeyBinding
        {
            Enabled = true,
            Modifiers = string.Join('+', parts),
            Key = key.ToString()
        };

        AppSettings settings = ViewModel.Settings;
        switch (button.Tag as string)
        {
            case nameof(HotkeyAction.BoostUp): settings.BoostUp = binding; break;
            case nameof(HotkeyAction.BoostDown): settings.BoostDown = binding; break;
            case nameof(HotkeyAction.ToggleEngine): settings.ToggleEngine = binding; break;
            case nameof(HotkeyAction.ResetBoost): settings.ResetBoost = binding; break;
        }

        CancelRecording();
        ViewModel.SaveNow();
        RefreshHotkeyLabels();
        ReportConflicts();
    }

    private void OnHotkeyLostFocus(object sender, RoutedEventArgs e)
    {
        CancelRecording();
        RefreshHotkeyLabels();
    }

    private void CancelRecording()
    {
        if (_recordingButton is null) return;
        _recordingButton.PreviewKeyDown -= OnHotkeyKeyDown;
        _recordingButton.LostFocus -= OnHotkeyLostFocus;
        _recordingButton = null;
    }

    private void ReportConflicts()
    {
        HotkeyService? service = (Application.Current as App)?.Hotkeys;
        if (service is null || ViewModel is null) return;

        service.Rebind(ViewModel.Settings);

        if (service.Conflicts.Count == 0)
        {
            HotkeyConflictBanner.Visibility = Visibility.Collapsed;
            return;
        }

        string names = string.Join(", ", service.Conflicts.Select(Describe));
        HotkeyConflictText.Text =
            $"Another application already owns the combination for: {names}. Pick a different one.";
        HotkeyConflictBanner.Visibility = Visibility.Visible;
    }

    private static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.BoostUp => "boost up",
        HotkeyAction.BoostDown => "boost down",
        HotkeyAction.ToggleEngine => "boost on/off",
        _ => "reset boost"
    };
}
