using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Crescendo.Services;

public enum HotkeyAction { BoostUp, BoostDown, ToggleEngine, ResetBoost }

/// <summary>
/// Registers system-wide hotkeys so the boost can be changed without bringing
/// the window forward.
/// </summary>
/// <remarks>
/// Uses <c>RegisterHotKey</c> rather than a low-level keyboard hook: a hook in
/// an elevated process intercepts every keystroke on the machine, which is far
/// more invasive than this feature warrants.
/// </remarks>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private HwndSource? _source;
    private IntPtr _handle;
    private int _nextId = 0xC001;
    private bool _disposed;

    public event Action<HotkeyAction>? Triggered;

    /// <summary>Actions whose key combination was already taken by another app.</summary>
    public IReadOnlyCollection<HotkeyAction> Conflicts => _conflicts;
    private readonly List<HotkeyAction> _conflicts = [];

    public void Attach(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
    }

    public void Rebind(AppSettings settings)
    {
        UnregisterAll();
        _conflicts.Clear();
        if (_handle == IntPtr.Zero) return;

        Register(HotkeyAction.BoostUp, settings.BoostUp);
        Register(HotkeyAction.BoostDown, settings.BoostDown);
        Register(HotkeyAction.ToggleEngine, settings.ToggleEngine);
        Register(HotkeyAction.ResetBoost, settings.ResetBoost);
    }

    private void Register(HotkeyAction action, HotkeyBinding binding)
    {
        if (!binding.Enabled || string.IsNullOrWhiteSpace(binding.Key)) return;
        if (!TryParse(binding, out uint modifiers, out uint virtualKey)) return;

        int id = _nextId++;
        // MOD_NOREPEAT stops a held key from firing dozens of times a second.
        if (RegisterHotKey(_handle, id, modifiers | MOD_NOREPEAT, virtualKey))
            _registered[id] = action;
        else
            _conflicts.Add(action);
    }

    public static bool TryParse(HotkeyBinding binding, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        foreach (string part in binding.Modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries |
                                                             StringSplitOptions.TrimEntries))
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => MOD_CONTROL,
                "alt" => MOD_ALT,
                "shift" => MOD_SHIFT,
                "win" or "windows" => MOD_WIN,
                _ => 0u
            };
        }

        if (modifiers == 0) return false;   // Windows rejects unmodified hotkeys for good reason

        if (!Enum.TryParse(binding.Key, ignoreCase: true, out Key key)) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out HotkeyAction action))
        {
            Triggered?.Invoke(action);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        if (_handle == IntPtr.Zero) return;
        foreach (int id in _registered.Keys) UnregisterHotKey(_handle, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
