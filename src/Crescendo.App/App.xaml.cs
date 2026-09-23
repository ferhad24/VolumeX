using System.Threading;
using System.Windows;
using Crescendo.Services;
using Crescendo.ViewModels;

namespace Crescendo;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutex = @"Global\Crescendo.SingleInstance.v1";

    private Mutex? _instanceMutex;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayService? _tray;

    internal HotkeyService? Hotkeys { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Every string in the interface is English, so "-1.5 dB" should not
        // become "-1,5 dB" because the machine is set to another locale.
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        System.Windows.FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(System.Windows.FrameworkElement),
            new FrameworkPropertyMetadata(
                System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        // Called by the uninstaller: undo everything, no UI. Runs before the
        // single-instance check so a copy that is still shutting down cannot
        // make the uninstall silently skip its cleanup.
        if (HasArg(e, "--uninstall"))
        {
            RunUninstall();
            Shutdown();
            return;
        }

        // Scripted "start with Windows", same code path as the settings toggle.
        if (HasArg(e, "--enable-autostart") || HasArg(e, "--disable-autostart"))
        {
            bool enable = HasArg(e, "--enable-autostart");
            LogLine(StartupService.SetEnabled(enable)
                ? $"Start with Windows {(enable ? "enabled" : "disabled")} from the command line."
                : "Start with Windows could not be changed from the command line.");
            Shutdown();
            return;
        }

        // Two copies would fight over the shared configuration block and the
        // per-application mixer, so the second one simply defers to the first.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("VolumeX is already running. Look for it in the notification area.",
                "VolumeX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // Scripted install/removal for the default playback device, then carry
        // on into the normal UI. Uses the same code path as the buttons.
        if (HasArg(e, "--install-engine"))
            RunEngineSetup(install: true);
        else if (HasArg(e, "--remove-engine"))
            RunEngineSetup(install: false);
        else if (HasArg(e, "--update-engine"))
            RunEngineRefresh();

        try
        {
            _viewModel = new MainViewModel();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show(
                $"VolumeX could not start.\n\n{ex.Message}\n\nMake sure it is running as an administrator.",
                "VolumeX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ThemeService.Apply(_viewModel.Settings.Theme);
        SystemEvents_Register();

        // A "start with Windows" task from the Crescendo days points at an exe
        // that is gone, and an older VolumeX task opened the window; bring
        // either up to date rather than drop the setting.
        StartupService.Refresh();

        Hotkeys = new HotkeyService();
        Hotkeys.Triggered += OnHotkey;

        _tray = new TrayService(_viewModel);
        _tray.ShowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;

        _viewModel.ExitRequested += ExitApplication;
        _viewModel.UpdateFound += info => _tray?.ShowMessage(
            $"VolumeX {info.Version} is available",
            "Open VolumeX and press Update — it installs in place, settings are kept.");

        _window = new MainWindow { DataContext = _viewModel };
        _window.CloseRequested += OnWindowCloseRequested;

        Hotkeys.Attach(_window);
        Hotkeys.Rebind(_viewModel.Settings);

        bool autostart = HasArg(e, StartupService.AutostartArgument);
        bool startHidden = autostart || _viewModel.Settings.StartMinimized || HasArg(e, "--minimized");

        if (autostart)
        {
            // Started at sign-in: the tray icon is the only sign VolumeX is there.
            _window.WindowState = WindowState.Minimized;
        }
        else if (startHidden)
        {
            _window.WindowState = WindowState.Minimized;
            _tray.ShowMessage("VolumeX is running",
                "Open it from here whenever you need to change the boost.");
        }
        else
        {
            _window.Show();
        }
    }

    private void OnHotkey(HotkeyAction action, bool isRepeat)
    {
        if (_viewModel is null) return;
        _viewModel.HandleHotkey(action, isRepeat);
        _tray?.Refresh();
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void OnWindowCloseRequested(object? sender, EventArgs e)
    {
        if (_viewModel?.Settings.CloseToTray == true)
        {
            _window?.Hide();
            return;
        }
        ExitApplication();
    }

    private void ExitApplication()
    {
        // The APO outlives this process, so boost and the per-app mixer are both
        // released here. If the process dies instead, the APO's watchdog does it.
        _viewModel?.PrepareForShutdown();
        Shutdown();
    }

    private void SystemEvents_Register()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(ThemeService.RefreshFromSystem);
        };
    }

    private void OnUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // Audio keeps playing regardless of what the UI does, so a failure here
        // is reported rather than allowed to take the process down silently.
        Log(e.Exception);

        MessageBox.Show(
            $"Something went wrong.\n\n{e.Exception.Message}\n\nDetails were written to {LogPath}.",
            "VolumeX", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static bool HasArg(StartupEventArgs e, string name) =>
        e.Args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Run by the setup after every install: swaps in a new engine only when
    /// one was already installed and the shipped DLL differs.
    /// </summary>
    private static void RunEngineRefresh()
    {
        try
        {
            using var engine = new EngineService();
            if (engine.RefreshInstalledEngine())
            {
                EngineService.RestartAudioServiceAsync().GetAwaiter().GetResult();
                LogLine($"Engine refreshed for version {UpdateService.CurrentVersion}.");
            }
            // The audio service now runs the engine from its current folder, so
            // the Crescendo-era copy can go.
            engine.CleanUpLegacyInstall();
        }
        catch (Exception ex)
        {
            // The app still works with the previous engine; report, do not block.
            Log(ex);
        }
    }

    private static void RunUninstall()
    {
        try
        {
            using var engine = new EngineService();
            engine.Uninstall();
            EngineService.RestartAudioServiceAsync().GetAwaiter().GetResult();
            // Only now has audiodg let go of any Crescendo-era engine file.
            engine.CleanUpLegacyInstall();
            LogLine("Uninstalled: engine removed, driver effects and audio policy restored.");
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    private static void RunEngineSetup(bool install)
    {
        try
        {
            var settings = new SettingsStore().Load();
            using var devices = new DeviceService();
            using var engine = new EngineService();

            if (install)
            {
                string deviceId = devices.TryGetDefaultDeviceId()
                    ?? throw new InvalidOperationException("No default playback device.");
                engine.InstallAndAttach(deviceId, settings.EffectSlot);
            }
            else
            {
                engine.RemoveEverything();
            }

            EngineService.RestartAudioServiceAsync().GetAwaiter().GetResult();
            LogLine(install ? "Engine installed from the command line." : "Engine removed from the command line.");
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show($"Engine setup failed.\n\n{ex.Message}", "VolumeX",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static string LogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Crescendo", "crescendo.log");

    /// <summary>
    /// Appends a failure to the log. Best effort: a logging problem must never
    /// become the thing the user sees.
    /// </summary>
    internal static void Log(Exception exception) => LogLine(exception.ToString());

    internal static void LogLine(string message)
    {
        try
        {
            string path = LogPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.AppendAllText(path,
                $"{DateTime.Now:u}  {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing sensible left to do if even the log cannot be written.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hotkeys?.Dispose();
        _tray?.Dispose();
        _viewModel?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
