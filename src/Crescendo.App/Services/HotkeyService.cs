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

    // Holding boost up/down keeps stepping. The repeat is driven here rather
    // than by keyboard auto-repeat so it feels the same on every machine,
    // whatever the user's repeat delay and rate are set to.
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan HoldInterval = TimeSpan.FromMilliseconds(40);

    private readonly Dictionary<int, Registration> _registered = [];
    private readonly System.Windows.Threading.DispatcherTimer _holdTimer;
    private Registration? _held;
    private DateTime _heldSince;
    private HwndSource? _source;
    private IntPtr _handle;
    private int _nextId = 0xC001;
    private bool _disposed;

    private sealed record Registration(HotkeyAction Action, uint Modifiers, uint VirtualKey)
    {
        // Toggling or resetting 25 times a second while a key is held would be
        // chaos; only the two stepping actions repeat.
        public bool Repeats => Action is HotkeyAction.BoostUp or HotkeyAction.BoostDown;
    }

    /// <summary>Raised on the UI thread. The flag is true for a held-key repeat.</summary>
    public event Action<HotkeyAction, bool>? Triggered;

    public HotkeyService()
    {
        _holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = HoldInterval };
        _holdTimer.Tick += OnHoldTick;
    }

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
        // MOD_NOREPEAT: Windows auto-repeat is replaced by the hold timer below.
        if (RegisterHotKey(_handle, id, modifiers | MOD_NOREPEAT, virtualKey))
            _registered[id] = new Registration(action, modifiers, virtualKey);
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
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out Registration? registration))
        {
            Triggered?.Invoke(registration.Action, false);
            handled = true;

            if (registration.Repeats)
            {
                _held = registration;
                _heldSince = DateTime.UtcNow;
                _holdTimer.Start();
            }
        }
        return IntPtr.Zero;
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        // Stops the moment the key or any of its modifiers is let go.
        if (_held is null || !IsHeld(_held))
        {
            _holdTimer.Stop();
            _held = null;
            return;
        }

        if (DateTime.UtcNow - _heldSince >= HoldDelay)
            Triggered?.Invoke(_held.Action, true);
    }

    private static bool IsHeld(Registration registration)
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        if (!Down((int)registration.VirtualKey)) return false;
        if ((registration.Modifiers & MOD_CONTROL) != 0 && !Down(VK_CONTROL)) return false;
        if ((registration.Modifiers & MOD_ALT) != 0 && !Down(VK_MENU)) return false;
        if ((registration.Modifiers & MOD_SHIFT) != 0 && !Down(VK_SHIFT)) return false;
        if ((registration.Modifiers & MOD_WIN) != 0 && !Down(VK_LWIN) && !Down(VK_RWIN)) return false;
        return true;
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

        _holdTimer.Stop();
        _held = null;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
