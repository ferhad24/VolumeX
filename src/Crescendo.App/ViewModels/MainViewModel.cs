using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Crescendo.Interop;
using Crescendo.Models;
using Crescendo.Services;

namespace Crescendo.ViewModels;

public enum NavigationPage { Boost, Equalizer, Apps, Devices, Settings }

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store = new();
    private readonly DeviceService _devices;
    private readonly SessionService _sessions;
    private readonly EngineService _engine;
    private readonly Dispatcher _dispatcher;

    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _saveTimer;

    private AppSettings _settings;
    private AudioProfile _profile;
    private AudioDevice? _selectedDevice;
    private bool _suppressPush;
    private bool _disposed;

    public MainViewModel()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _settings = _store.Load();
        _devices = new DeviceService();
        _sessions = new SessionService(_devices);
        _engine = new EngineService();

        Bands = [];
        Apps = [];
        Devices = [];
        Presets = [.. Preset.All.Select(p => new PresetItemViewModel(p, ApplyPreset))];

        _profile = new AudioProfile();

        ToggleEngineCommand = new RelayCommand(() => EngineEnabled = !EngineEnabled);
        ApplyPresetCommand = new RelayCommand(p => ApplyPreset(p as Preset));
        ResetAllCommand = new RelayCommand(ResetToDefaults);
        ResetEqCommand = new RelayCommand(ResetEqualizer);
        SelectPageCommand = new RelayCommand(p => { if (p is NavigationPage page) CurrentPage = page; });
        SelectDeviceCommand = new RelayCommand(d => { if (d is AudioDevice device) SelectDevice(device); });
        ResetAppBoostCommand = new RelayCommand(a => (a as AppBoostViewModel)?.Reset());

        InstallEngineCommand = new AsyncRelayCommand(InstallEngineAsync);
        RemoveEngineCommand = new AsyncRelayCommand(RemoveEngineAsync);
        RestartAudioCommand = new AsyncRelayCommand(RestartAudioAsync);

        _sessionTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionTimer.Tick += (_, _) => RefreshSessions();

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        // Settings are written on a trailing delay: dragging a slider should not
        // hit the disk on every frame.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(_settings); };

        _engine.MeterUpdated += OnMeterUpdated;
        _devices.DevicesChanged += () => _dispatcher.BeginInvoke(RefreshDevices);
        _devices.DefaultDeviceChanged += id => _dispatcher.BeginInvoke(() => OnDefaultDeviceChanged(id));

        RefreshDevices();
        _sessionTimer.Start();
        _statusTimer.Start();
        RefreshStatus();
        StartUpdateChecks();
    }

    // ---------------------------------------------------------------- state

    public ObservableCollection<BandViewModel> Bands { get; }
    public ObservableCollection<AppBoostViewModel> Apps { get; }
    public ObservableCollection<AudioDevice> Devices { get; }
    public ObservableCollection<PresetItemViewModel> Presets { get; }

    public AppSettings Settings => _settings;

    private NavigationPage _currentPage = NavigationPage.Boost;
    public NavigationPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (Set(ref _currentPage, value))
            {
                Raise(nameof(IsBoostPage));
                Raise(nameof(IsEqualizerPage));
                Raise(nameof(IsAppsPage));
                Raise(nameof(IsDevicesPage));
                Raise(nameof(IsSettingsPage));
                Raise(nameof(PageTitle));
                Raise(nameof(PageSubtitle));
            }
        }
    }

    public bool IsBoostPage => CurrentPage == NavigationPage.Boost;
    public bool IsEqualizerPage => CurrentPage == NavigationPage.Equalizer;
    public bool IsAppsPage => CurrentPage == NavigationPage.Apps;
    public bool IsDevicesPage => CurrentPage == NavigationPage.Devices;
    public bool IsSettingsPage => CurrentPage == NavigationPage.Settings;

    public string PageTitle => CurrentPage switch
    {
        NavigationPage.Boost => "Boost",
        NavigationPage.Equalizer => "Equalizer",
        NavigationPage.Apps => "Applications",
        NavigationPage.Devices => "Devices",
        _ => "Settings"
    };

    public string PageSubtitle => CurrentPage switch
    {
        NavigationPage.Boost => "Amplification, tone and limiting",
        NavigationPage.Equalizer => "Ten bands, shelves and presets",
        NavigationPage.Apps => "Per-application levels",
        NavigationPage.Devices => "Playback devices and engine placement",
        _ => "Behaviour, hotkeys and appearance"
    };

    // ---------------------------------------------------------------- boost

    public bool EngineEnabled
    {
        get => _profile.Enabled;
        set
        {
            if (_profile.Enabled == value) return;
            _profile.Enabled = value;
            Raise();
            Raise(nameof(EngineToggleText));
            Push();
        }
    }

    public string EngineToggleText => EngineEnabled ? "Boost on" : "Boost off";

    /// <summary>100–500. The dial and the hotkeys both drive this.</summary>
    public double BoostPercent
    {
        get => Math.Round(_profile.Boost * 100.0);
        set
        {
            float boost = (float)Math.Clamp(value / 100.0, 1.0, 5.0);
            if (Math.Abs(_profile.Boost - boost) < 0.0005f) return;
            _profile.Boost = boost;
            Raise();
            Raise(nameof(BoostText));
            Raise(nameof(BoostGainDbText));
            Push();
        }
    }

    public string BoostText => $"{BoostPercent:0}%";

    /// <summary>The honest number: 500% is +14 dB, not "five times louder".</summary>
    public string BoostGainDbText
    {
        get
        {
            double db = 20.0 * Math.Log10(Math.Max(_profile.Boost, 0.0001));
            return db <= 0.05 ? "0.0 dB" : $"+{db:0.0} dB";
        }
    }

    public double BassDb
    {
        get => _profile.BassDb;
        set => SetProfileValue(v => _profile.BassDb = v, _profile.BassDb, value, -12, 12,
            nameof(BassDb), nameof(BassText));
    }

    public string BassText => FormatDb(_profile.BassDb);

    public double TrebleDb
    {
        get => _profile.TrebleDb;
        set => SetProfileValue(v => _profile.TrebleDb = v, _profile.TrebleDb, value, -12, 12,
            nameof(TrebleDb), nameof(TrebleText));
    }

    public string TrebleText => FormatDb(_profile.TrebleDb);

    public double Balance
    {
        get => _profile.Balance;
        set => SetProfileValue(v => _profile.Balance = v, _profile.Balance, value, -1, 1,
            nameof(Balance), nameof(BalanceText));
    }

    public string BalanceText => _profile.Balance switch
    {
        < -0.01f => $"L {Math.Abs(_profile.Balance) * 100:0}%",
        > 0.01f => $"R {_profile.Balance * 100:0}%",
        _ => "Centred"
    };

    public bool MonoOutput
    {
        get => _profile.Mono;
        set => SetProfileFlag(v => _profile.Mono = v, _profile.Mono, value, nameof(MonoOutput));
    }

    public bool SwapChannels
    {
        get => _profile.SwapChannels;
        set => SetProfileFlag(v => _profile.SwapChannels = v, _profile.SwapChannels, value, nameof(SwapChannels));
    }

    public bool LimiterEnabled
    {
        get => _profile.LimiterEnabled;
        set => SetProfileFlag(v => _profile.LimiterEnabled = v, _profile.LimiterEnabled, value,
            nameof(LimiterEnabled), nameof(LimiterWarning));
    }

    /// <summary>Shown when the user turns off the only thing preventing clipping.</summary>
    public string? LimiterWarning => !_profile.LimiterEnabled && _profile.Boost > 1.2f
        ? "Without the limiter, boosted peaks will clip and distort."
        : null;

    public bool SoftClip
    {
        get => _profile.SoftClip;
        set => SetProfileFlag(v => _profile.SoftClip = v, _profile.SoftClip, value, nameof(SoftClip));
    }

    public bool SubsonicFilter
    {
        get => _profile.SubsonicFilter;
        set => SetProfileFlag(v => _profile.SubsonicFilter = v, _profile.SubsonicFilter, value, nameof(SubsonicFilter));
    }

    public bool EqEnabled
    {
        get => _profile.EqEnabled;
        set => SetProfileFlag(v => _profile.EqEnabled = v, _profile.EqEnabled, value, nameof(EqEnabled));
    }

    public double LimiterCeilingDb
    {
        get => _profile.LimiterCeilingDb;
        set => SetProfileValue(v => _profile.LimiterCeilingDb = v, _profile.LimiterCeilingDb, value, -6, 0,
            nameof(LimiterCeilingDb), nameof(LimiterCeilingText));
    }

    public string LimiterCeilingText => $"{_profile.LimiterCeilingDb:0.0} dBFS";

    public double LimiterReleaseMs
    {
        get => _profile.LimiterReleaseMs;
        set => SetProfileValue(v => _profile.LimiterReleaseMs = v, _profile.LimiterReleaseMs, value, 20, 500,
            nameof(LimiterReleaseMs), nameof(LimiterReleaseText));
    }

    public string LimiterReleaseText => $"{_profile.LimiterReleaseMs:0} ms";

    public double LimiterLookaheadMs
    {
        get => _profile.LimiterLookaheadMs;
        set => SetProfileValue(v => _profile.LimiterLookaheadMs = v, _profile.LimiterLookaheadMs, value, 1, 15,
            nameof(LimiterLookaheadMs), nameof(LimiterLookaheadText));
    }

    public string LimiterLookaheadText => $"{_profile.LimiterLookaheadMs:0.#} ms latency";

    // ---------------------------------------------------------------- presets

    private Preset? _selectedPreset;
    public Preset? SelectedPreset
    {
        get => _selectedPreset;
        private set
        {
            if (!Set(ref _selectedPreset, value)) return;

            foreach (PresetItemViewModel item in Presets)
                item.SetSelectedQuietly(ReferenceEquals(item.Preset, value));

            Raise(nameof(SelectedPresetDescription));
        }
    }

    public string SelectedPresetDescription => _selectedPreset?.Description ?? string.Empty;

    /// <summary>
    /// Incremented whenever the response changes, so the EQ curve redraws.
    /// Band gains live on plain model objects that the curve cannot observe.
    /// </summary>
    private int _eqRevision;
    public int EqRevision
    {
        get => _eqRevision;
        private set => Set(ref _eqRevision, value);
    }

    private void ApplyPreset(Preset? preset)
    {
        if (preset is null) return;
        if (ReferenceEquals(preset, SelectedPreset)) return;

        preset.ApplyTo(_profile);
        SelectedPreset = preset;

        foreach (BandViewModel band in Bands) band.Refresh();
        EqRevision++;
        Raise(nameof(BassDb));
        Raise(nameof(BassText));
        Raise(nameof(TrebleDb));
        Raise(nameof(TrebleText));
        Raise(nameof(LimiterReleaseMs));
        Raise(nameof(LimiterReleaseText));
        Raise(nameof(MonoOutput));

        Push();
    }

    private void SyncPresetSelection()
    {
        Preset? match = Preset.All.FirstOrDefault(p => !p.IsCustom && p.Matches(_profile));
        Preset resolved = match ?? Preset.Custom;
        if (!ReferenceEquals(resolved, SelectedPreset))
        {
            _profile.PresetName = resolved.Name;
            SelectedPreset = resolved;
        }
    }

    // ---------------------------------------------------------------- meter

    private double _peakLeft, _peakRight, _peakLeftIn, _peakRightIn, _gainReduction;

    /// <summary>0–1, already converted from dBFS to a meter-friendly scale.</summary>
    public double PeakLeft { get => _peakLeft; private set => Set(ref _peakLeft, value); }
    public double PeakRight { get => _peakRight; private set => Set(ref _peakRight, value); }
    public double PeakLeftIn { get => _peakLeftIn; private set => Set(ref _peakLeftIn, value); }
    public double PeakRightIn { get => _peakRightIn; private set => Set(ref _peakRightIn, value); }

    /// <summary>0–1 where 1 means 12 dB of limiting.</summary>
    public double GainReduction { get => _gainReduction; private set => Set(ref _gainReduction, value); }

    private double _gainReductionDb;
    public double GainReductionDb
    {
        get => _gainReductionDb;
        private set
        {
            if (Set(ref _gainReductionDb, value)) Raise(nameof(GainReductionText));
        }
    }

    public string GainReductionText => _gainReductionDb < 0.05 ? "0.0 dB" : $"-{_gainReductionDb:0.0} dB";

    private bool _isClipping;
    public bool IsClipping { get => _isClipping; private set => Set(ref _isClipping, value); }

    private void OnMeterUpdated(MeterSnapshot snapshot)
    {
        // The audio thread publishes at block rate; the UI only needs ~30 Hz and
        // must not touch dependency properties off-thread.
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (_disposed) return;

            PeakLeftIn = Decay(PeakLeftIn, ToMeterScale(snapshot.PeakIn.Length > 0 ? snapshot.PeakIn[0] : 0));
            PeakRightIn = Decay(PeakRightIn, ToMeterScale(snapshot.PeakIn.Length > 1 ? snapshot.PeakIn[1] : 0));
            PeakLeft = Decay(PeakLeft, ToMeterScale(snapshot.PeakOut.Length > 0 ? snapshot.PeakOut[0] : 0));
            PeakRight = Decay(PeakRight, ToMeterScale(snapshot.PeakOut.Length > 1 ? snapshot.PeakOut[1] : 0));

            GainReductionDb = snapshot.GainReductionDb;
            GainReduction = Math.Clamp(snapshot.GainReductionDb / 12.0, 0, 1);
            IsClipping = snapshot.GainReductionDb > 6.0;
        });
    }

    /// <summary>
    /// Maps a linear peak onto a -60..0 dBFS bar. A linear meter spends almost
    /// all of its travel in the top 6 dB and reads as useless.
    /// </summary>
    private static double ToMeterScale(float linearPeak)
    {
        if (linearPeak <= 0.0001f) return 0;
        double db = 20.0 * Math.Log10(linearPeak);
        return Math.Clamp((db + 60.0) / 60.0, 0, 1);
    }

    /// <summary>Instant rise, gentle fall — standard peak-meter ballistics.</summary>
    private static double Decay(double current, double target) =>
        target >= current ? target : current + (target - current) * 0.22;

    // ---------------------------------------------------------------- devices

    public AudioDevice? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            if (Set(ref _selectedDevice, value))
            {
                Raise(nameof(SelectedDeviceName));
                Raise(nameof(SelectedDeviceAdapter));
            }
        }
    }

    public string SelectedDeviceName => SelectedDevice?.Name ?? "No playback device";
    public string SelectedDeviceAdapter => SelectedDevice?.Adapter ?? string.Empty;

    private void RefreshDevices()
    {
        IReadOnlyList<AudioDevice> devices = _devices.GetPlaybackDevices();

        Devices.Clear();
        foreach (AudioDevice device in devices) Devices.Add(device);

        AudioDevice? target =
            devices.FirstOrDefault(d => d.Id == _settings.LastDeviceId) ??
            devices.FirstOrDefault(d => d.IsDefault) ??
            devices.FirstOrDefault();

        if (target is not null && target.Id != SelectedDevice?.Id)
            SelectDevice(target);
        else
            SelectedDevice = devices.FirstOrDefault(d => d.Id == SelectedDevice?.Id) ?? target;
    }

    private void OnDefaultDeviceChanged(string deviceId)
    {
        RefreshDevices();

        // Following the system default is the behaviour people expect when they
        // unplug headphones mid-track.
        AudioDevice? device = Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is not null) SelectDevice(device);
    }

    private void SelectDevice(AudioDevice device)
    {
        // Hand the previous device's mixer back before switching away from it.
        if (SelectedDevice is not null && SelectedDevice.Id != device.Id)
            _sessions.RestoreAll(SelectedDevice.Id);

        SelectedDevice = device;
        _settings.LastDeviceId = device.Id;
        _engine.SetEndpoint(device.Id);

        _profile = SettingsStore.GetOrCreateProfile(_settings, device.Id, device.Name);

        RebuildBands();
        EqRevision++;
        RaiseAllProfileProperties();
        SyncPresetSelection();

        Push();
        RefreshSessions();
        RefreshStatus();
        ScheduleSave();
    }

    private void RebuildBands()
    {
        Bands.Clear();
        foreach (BandSetting band in _profile.Bands)
            Bands.Add(new BandViewModel(band, OnBandChanged));
    }

    private void OnBandChanged()
    {
        EqRevision++;
        SyncPresetSelection();
        Push();
    }

    // ---------------------------------------------------------------- sessions

    private void RefreshSessions()
    {
        if (SelectedDevice is null) return;

        IReadOnlyList<AudioSessionInfo> sessions = _sessions.GetSessions(SelectedDevice.Id);

        // Add rows for newly seen executables, refresh the ones already listed.
        foreach (AudioSessionInfo session in sessions)
        {
            AppBoostViewModel? existing = Apps.FirstOrDefault(
                a => string.Equals(a.Executable, session.Executable, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                AppBoost model = _profile.AppBoosts.FirstOrDefault(
                    b => string.Equals(b.Executable, session.Executable, StringComparison.OrdinalIgnoreCase))
                    ?? AddAppBoost(session);

                model.DisplayName = session.DisplayName;
                Apps.Add(new AppBoostViewModel(model, session, OnAppBoostChanged));
            }
            else
            {
                existing.UpdateActivity(session.IsActive);
            }
        }

        // Drop rows whose application has gone away and that are back at default.
        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            AppBoostViewModel app = Apps[i];
            bool stillPresent = sessions.Any(
                s => string.Equals(s.Executable, app.Executable, StringComparison.OrdinalIgnoreCase));

            if (!stillPresent && Math.Abs(app.BoostPercent - 100) < 0.5 && !app.Muted)
                Apps.RemoveAt(i);
            else if (!stillPresent)
                app.UpdateActivity(false);
        }

        ApplyAppBoosts();
    }

    private AppBoost AddAppBoost(AudioSessionInfo session)
    {
        var model = new AppBoost
        {
            Executable = session.Executable,
            DisplayName = session.DisplayName,
            Boost = 1.0f
        };
        _profile.AppBoosts.Add(model);
        return model;
    }

    private void OnAppBoostChanged()
    {
        Push();
        ApplyAppBoosts();
        ScheduleSave();
    }

    private void ApplyAppBoosts()
    {
        if (SelectedDevice is null) return;

        if (!_profile.Enabled)
        {
            // With the engine off, the mixer must not stay scaled.
            _sessions.RestoreAll(SelectedDevice.Id);
            return;
        }

        float engineGain = EngineService.ComputeEngineGain(_profile);
        _sessions.ApplyBoosts(SelectedDevice.Id, _profile.Boost, _profile.AppBoosts, engineGain);
    }

    // ---------------------------------------------------------------- status

    private EngineStatus? _status;
    public EngineStatus? Status { get => _status; private set => Set(ref _status, value); }

    private string _statusTitle = "Checking engine";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }

    private string _statusDetail = string.Empty;
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

    private bool _engineInstalled;
    public bool EngineInstalled { get => _engineInstalled; private set => Set(ref _engineInstalled, value); }

    private bool _needsRestart;
    public bool NeedsRestart { get => _needsRestart; private set => Set(ref _needsRestart, value); }

    private bool _isHealthy;
    public bool IsHealthy { get => _isHealthy; private set => Set(ref _isHealthy, value); }

    private void RefreshStatus()
    {
        EngineStatus status = _engine.GetStatus(SelectedDevice?.Id);
        Status = status;

        EngineInstalled = status.Health is not (EngineHealth.NotInstalled or EngineHealth.NotAttached);
        NeedsRestart = status.Health == EngineHealth.PendingRestart;
        IsHealthy = status.Health is EngineHealth.Idle or EngineHealth.Processing;

        (StatusTitle, StatusDetail) = status.Health switch
        {
            EngineHealth.NotInstalled =>
                ("Engine not installed",
                 "Crescendo needs to register its audio engine with Windows before it can amplify anything."),
            EngineHealth.NotAttached =>
                ("Not active on this device",
                 $"The engine is registered but not attached to {SelectedDeviceName}."),
            EngineHealth.PendingRestart =>
                ("Restart required",
                 "Windows loads audio effects when the audio service starts. Restart it to activate Crescendo."),
            EngineHealth.Idle =>
                ("Ready",
                 $"Running on {SelectedDeviceName}. Waiting for audio."),
            _ =>
                ("Processing",
                 status.SampleRate > 0
                    ? $"{status.SampleRate / 1000.0:0.#} kHz · {status.Channels} ch · {_profile.LimiterLookaheadMs:0.#} ms added latency"
                    : $"Running on {SelectedDeviceName}.")
        };

        // Overrides everything above: with no link, even a loaded engine never
        // sees a single setting change.
        if (_engine.LinkFailure is string failure)
        {
            IsHealthy = false;
            StatusTitle = "Cannot reach the engine";
            StatusDetail = $"Start Crescendo as an administrator. ({failure})";
        }
    }

    // ---------------------------------------------------------------- updates

    private UpdateInfo? _pendingUpdate;
    private DispatcherTimer? _updateTimer;

    /// <summary>Raised once per newly found version, for the tray notification.</summary>
    public event Action<UpdateInfo>? UpdateFound;

    /// <summary>The setup has started and needs this process gone to replace it.</summary>
    public event Action? ExitRequested;

    public string CurrentVersionText => $"Version {UpdateService.CurrentVersion}";

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }

    private string _updateText = string.Empty;
    public string UpdateText { get => _updateText; private set => Set(ref _updateText, value); }

    private bool _updating;
    public bool Updating { get => _updating; private set => Set(ref _updating, value); }

    public AsyncRelayCommand InstallUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckForUpdatesCommand { get; private set; } = null!;

    private void StartUpdateChecks()
    {
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => _pendingUpdate is not null && !Updating);
        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(userAsked: true));

        // First check shortly after start, so launch itself stays instant;
        // then every six hours for a copy that lives in the tray for days.
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdatesAsync(userAsked: false);
        };
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        if (Updating) return;
        if (userAsked) UpdateText = "Checking for updates…";

        UpdateInfo? info = await UpdateService.CheckAsync(CancellationToken.None).ConfigureAwait(true);

        if (info is null)
        {
            if (userAsked && _pendingUpdate is null)
                UpdateText = $"Crescendo {UpdateService.CurrentVersion} is the latest version.";
            return;
        }

        bool isNew = _pendingUpdate?.Version != info.Version;
        _pendingUpdate = info;
        UpdateAvailable = true;
        UpdateText = $"Crescendo {info.Version} is available.";
        InstallUpdateCommand.RaiseCanExecuteChanged();

        if (isNew) UpdateFound?.Invoke(info);
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null) return;

        Updating = true;
        InstallUpdateCommand.RaiseCanExecuteChanged();
        try
        {
            var progress = new Progress<double>(p => UpdateText = $"Downloading {_pendingUpdate.Version}… {p:0}%");
            await UpdateService.DownloadAndStartAsync(_pendingUpdate, progress, CancellationToken.None)
                .ConfigureAwait(true);

            UpdateText = "Installing…";
            // The setup closes nothing it does not have to: this process leaves
            // on its own (bypassing the engine on the way out) and the new
            // version starts when the installer finishes.
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            UpdateText = $"Update failed: {ex.Message}";
            Updating = false;
            InstallUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    // ---------------------------------------------------------------- commands

    public RelayCommand ToggleEngineCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand ResetAllCommand { get; }
    public RelayCommand ResetEqCommand { get; }
    public RelayCommand SelectPageCommand { get; }
    public RelayCommand SelectDeviceCommand { get; }
    public RelayCommand ResetAppBoostCommand { get; }
    public AsyncRelayCommand InstallEngineCommand { get; }
    public AsyncRelayCommand RemoveEngineCommand { get; }
    public AsyncRelayCommand RestartAudioCommand { get; }

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private string? _busyMessage;
    public string? BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }

    private async Task InstallEngineAsync()
    {
        if (SelectedDevice is null) return;

        Busy = true;
        BusyMessage = "Registering the audio engine…";
        try
        {
            _engine.InstallAndAttach(SelectedDevice.Id, _settings.EffectSlot);

            BusyMessage = "Restarting Windows audio…";
            await EngineService.RestartAudioServiceAsync().ConfigureAwait(true);

            // The endpoint graph is rebuilt asynchronously; give it a moment
            // before reporting what happened.
            await Task.Delay(1500).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            StatusTitle = "Could not install the engine";
            StatusDetail = ex.Message;
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
            RefreshStatus();
            Push();
        }
    }

    private async Task RemoveEngineAsync()
    {
        Busy = true;
        BusyMessage = "Removing the audio engine…";
        try
        {
            if (SelectedDevice is not null) _sessions.RestoreAll(SelectedDevice.Id);

            _engine.RemoveEverything();

            BusyMessage = "Restarting Windows audio…";
            await EngineService.RestartAudioServiceAsync().ConfigureAwait(true);
            await Task.Delay(1000).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            StatusTitle = "Could not remove the engine";
            StatusDetail = ex.Message;
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
            RefreshStatus();
        }
    }

    private async Task RestartAudioAsync()
    {
        Busy = true;
        BusyMessage = "Restarting Windows audio…";
        try
        {
            await EngineService.RestartAudioServiceAsync().ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
            RefreshStatus();
            Push();
        }
    }

    // ---------------------------------------------------------------- hotkeys

    /// <summary>Fine step for a held key: 100% to 500% in about three seconds.</summary>
    private const int HeldStepPercent = 5;

    public void HandleHotkey(HotkeyAction action, bool isRepeat = false)
    {
        // A tap moves by the configured step; holding then glides in fine steps.
        int step = isRepeat ? Math.Min(HeldStepPercent, _settings.BoostStepPercent) : _settings.BoostStepPercent;

        switch (action)
        {
            case HotkeyAction.BoostUp:
                BoostPercent = Math.Min(500, BoostPercent + step);
                break;
            case HotkeyAction.BoostDown:
                BoostPercent = Math.Max(100, BoostPercent - step);
                break;
            case HotkeyAction.ToggleEngine:
                EngineEnabled = !EngineEnabled;
                break;
            case HotkeyAction.ResetBoost:
                BoostPercent = 100;
                break;
        }
    }

    // ---------------------------------------------------------------- reset

    public void ResetToDefaults()
    {
        var fresh = new AudioProfile
        {
            EndpointId = _profile.EndpointId,
            DeviceName = _profile.DeviceName,
            Enabled = _profile.Enabled
        };

        if (SelectedDevice is not null)
        {
            _sessions.RestoreAll(SelectedDevice.Id);
            _settings.Profiles[SelectedDevice.Id] = fresh;
        }

        _profile = fresh;
        Apps.Clear();
        RebuildBands();
        EqRevision++;
        RaiseAllProfileProperties();
        SyncPresetSelection();
        Push();
        ScheduleSave();
    }

    private void ResetEqualizer()
    {
        foreach (BandSetting band in _profile.Bands) band.GainDb = 0;
        _profile.BassDb = 0;
        _profile.TrebleDb = 0;

        foreach (BandViewModel band in Bands) band.Refresh();
        EqRevision++;
        Raise(nameof(BassDb));
        Raise(nameof(BassText));
        Raise(nameof(TrebleDb));
        Raise(nameof(TrebleText));

        SyncPresetSelection();
        Push();
    }

    // ---------------------------------------------------------------- plumbing

    private void SetProfileValue(Action<float> setter, float current, double value,
        double min, double max, params string[] properties)
    {
        float clamped = (float)Math.Clamp(value, min, max);
        if (Math.Abs(current - clamped) < 0.0005f) return;

        setter(clamped);
        foreach (string property in properties) Raise(property);
        EqRevision++;
        SyncPresetSelection();
        Push();
    }

    private void SetProfileFlag(Action<bool> setter, bool current, bool value, params string[] properties)
    {
        if (current == value) return;
        setter(value);
        foreach (string property in properties) Raise(property);
        Push();
    }

    private void RaiseAllProfileProperties()
    {
        foreach (string name in new[]
        {
            nameof(EngineEnabled), nameof(EngineToggleText), nameof(BoostPercent), nameof(BoostText),
            nameof(BoostGainDbText), nameof(BassDb), nameof(BassText), nameof(TrebleDb), nameof(TrebleText),
            nameof(Balance), nameof(BalanceText), nameof(MonoOutput), nameof(SwapChannels),
            nameof(LimiterEnabled), nameof(LimiterWarning), nameof(SoftClip), nameof(SubsonicFilter),
            nameof(EqEnabled), nameof(LimiterCeilingDb), nameof(LimiterCeilingText),
            nameof(LimiterReleaseMs), nameof(LimiterReleaseText),
            nameof(LimiterLookaheadMs), nameof(LimiterLookaheadText)
        })
        {
            Raise(name);
        }
    }

    private void Push()
    {
        if (_suppressPush) return;
        _engine.Push(_profile);
        Raise(nameof(LimiterWarning));
        ApplyAppBoosts();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow() => _store.Save(_settings);

    private static string FormatDb(float value) => value switch
    {
        > 0.05f => $"+{value:0.#} dB",
        < -0.05f => $"{value:0.#} dB",
        _ => "0 dB"
    };

    /// <summary>
    /// Called when the app exits. The APO lives on inside audiodg, so it is put
    /// into bypass, and the per-application mixer is handed back -- nothing may
    /// stay boosted or scaled once there is no window to turn it down from.
    /// </summary>
    public void PrepareForShutdown()
    {
        _suppressPush = true;
        // Boost stops with the app. The saved profile keeps Enabled = true, so
        // the next start picks up exactly where this one left off.
        _engine.PushBypass();
        if (SelectedDevice is not null) _sessions.RestoreAll(SelectedDevice.Id);
        SaveNow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionTimer.Stop();
        _statusTimer.Stop();
        _saveTimer.Stop();
        _updateTimer?.Stop();

        _engine.MeterUpdated -= OnMeterUpdated;
        _engine.Dispose();
        _sessions.Dispose();
        _devices.Dispose();
    }
}
