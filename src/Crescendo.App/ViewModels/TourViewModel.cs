using System.Collections.ObjectModel;
using System.ComponentModel;
using Crescendo.Services;

namespace Crescendo.ViewModels;

public enum TourAction { None, InstallEngine, StartWithWindows }

/// <param name="CardAtTop">For pages whose subject sits low on the page, so the card does not cover it.</param>
public sealed record TourStep(string Title, string Body, NavigationPage Page,
    TourAction Action = TourAction.None, bool CardAtTop = false);

public sealed class TourDot(bool isCurrent)
{
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>
/// The first-run walkthrough. Each step switches the window to the page it
/// talks about, so the explanation sits over the real controls, and the two
/// steps that need an action (engine, start with Windows) can do it in place.
/// </summary>
public sealed class TourViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly List<TourStep> _steps;
    private int _index;

    public TourViewModel(MainViewModel main)
    {
        _main = main;
        _steps = BuildSteps(main.Settings);

        NextCommand = new RelayCommand(Next);
        BackCommand = new RelayCommand(() => GoTo(_index - 1), () => _index > 0);
        SkipCommand = new RelayCommand(Finish);
        EnableAutostartCommand = new RelayCommand(EnableAutostart, () => !AutostartEnabled);

        _main.PropertyChanged += OnMainChanged;
    }

    public ObservableCollection<TourDot> Dots { get; } = [];

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }

    public TourStep Current => _steps[_index];
    public string StepText => _index == 0 ? "Quick tour" : $"Step {_index} of {_steps.Count - 1}";
    public string NextText => _index == 0 ? "Show me" : IsLast ? "Done" : "Next";
    public bool IsLast => _index == _steps.Count - 1;
    public bool CanGoBack => _index > 0;

    public bool ShowInstallEngine => Current.Action == TourAction.InstallEngine && !_main.EngineInstalled;
    public bool ShowEngineReady => Current.Action == TourAction.InstallEngine && _main.EngineInstalled;

    private bool _autostartEnabled;
    public bool AutostartEnabled { get => _autostartEnabled; private set => Set(ref _autostartEnabled, value); }
    public bool ShowAutostart => Current.Action == TourAction.StartWithWindows;

    public System.Windows.VerticalAlignment CardAlignment =>
        Current.CardAtTop ? System.Windows.VerticalAlignment.Top : System.Windows.VerticalAlignment.Bottom;

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand EnableAutostartCommand { get; }

    public void Open()
    {
        AutostartEnabled = StartupService.IsEnabled();
        EnableAutostartCommand.RaiseCanExecuteChanged();
        IsOpen = true;
        GoTo(0);
    }

    private void Next()
    {
        if (IsLast) Finish();
        else GoTo(_index + 1);
    }

    private void GoTo(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Count - 1);
        _main.CurrentPage = Current.Page;

        Dots.Clear();
        for (int i = 1; i < _steps.Count; i++) Dots.Add(new TourDot(i == _index));

        Raise(nameof(Current));
        Raise(nameof(StepText));
        Raise(nameof(NextText));
        Raise(nameof(IsLast));
        Raise(nameof(CanGoBack));
        Raise(nameof(ShowInstallEngine));
        Raise(nameof(ShowEngineReady));
        Raise(nameof(ShowAutostart));
        Raise(nameof(CardAlignment));
        BackCommand.RaiseCanExecuteChanged();
    }

    private void Finish()
    {
        IsOpen = false;
        _main.CurrentPage = NavigationPage.Boost;
        _main.Settings.TourSeen = true;
        _main.SaveNow();
    }

    private void EnableAutostart()
    {
        AutostartEnabled = StartupService.SetEnabled(true);
        if (AutostartEnabled)
        {
            _main.Settings.StartWithWindows = true;
            _main.SaveNow();
        }
        EnableAutostartCommand.RaiseCanExecuteChanged();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.EngineInstalled))
        {
            Raise(nameof(ShowInstallEngine));
            Raise(nameof(ShowEngineReady));
        }
    }

    private static List<TourStep> BuildSteps(AppSettings settings) =>
    [
        new("Welcome to VolumeX",
            "VolumeX makes everything on this PC louder, up to 500%, without the crackle. " +
            "This tour shows the few things worth knowing. It takes about a minute.",
            NavigationPage.Boost),

        new("Install the engine",
            "VolumeX works inside Windows' own audio system, so its engine is installed once. " +
            "Sound cuts out for a second while Windows restarts audio; that is normal. " +
            "You can also do it later from the panel at the bottom left.",
            NavigationPage.Boost, TourAction.InstallEngine),

        new("Turn it up",
            "Switch Boost on at the top left, then drag the dial. 100% is normal volume, 500% the maximum. " +
            "The limiter catches loud peaks, so it gets louder, not distorted.",
            NavigationPage.Boost),

        new("Keyboard shortcuts",
            $"Press {settings.BoostUp} to go louder and {settings.BoostDown} to go quieter, " +
            "or hold them to keep going. " +
            $"{settings.ToggleEngine} turns boost on or off, and {settings.ResetBoost} puts it back to 100%. " +
            "They work in any program; change them below.",
            NavigationPage.Settings, CardAtTop: true),

        new("Presets and equalizer",
            "Music, Movie, Gaming and Voice set the tone in one click. " +
            "On this page you can shape the sound band by band; Custom keeps your own curve.",
            NavigationPage.Equalizer),

        new("One app louder than the rest",
            "Everything that is playing sound appears here. " +
            "Give a quiet game or a video call its own level without touching the others.",
            NavigationPage.Apps),

        new("It lives next to the clock",
            "Closing the window keeps VolumeX running in the notification area; click its icon to come back. " +
            "Start it with Windows and your boost is there every time you sign in.",
            NavigationPage.Boost, TourAction.StartWithWindows),
    ];
}
