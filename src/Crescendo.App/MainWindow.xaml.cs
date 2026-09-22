using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Crescendo.Services;
using Crescendo.ViewModels;

namespace Crescendo;

public partial class MainWindow : Window
{
    /// <summary>Raised instead of closing, so the app can decide to hide to tray.</summary>
    public event EventHandler? CloseRequested;

    private bool _suppressDeviceEvent;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        ThemeService.ThemeChanged += _ => ApplyTitleBarTheme();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnSourceInitialized(object? sender, EventArgs e) => ApplyTitleBarTheme();

    /// <summary>
    /// Tells the desktop window manager to match the window frame and rounded
    /// corners to the theme. Without it a dark window keeps a light system
    /// border on Windows 11.
    /// </summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            int darkMode = ThemeService.IsDarkActive ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int cornerPreference = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows builds reject these attributes; the window is simply
            // drawn with the default frame.
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && ViewModel?.Settings.MinimizeToTray == true)
            Hide();
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceEvent || ViewModel is null) return;
        if (sender is not ComboBox { SelectedItem: AudioDevice device }) return;
        if (device.Id == ViewModel.SelectedDevice?.Id) return;

        ViewModel.SelectDeviceCommand.Execute(device);
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 and the system menu route through here too.
        e.Cancel = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
