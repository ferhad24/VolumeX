using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crescendo.Interop;
using Crescendo.Models;

namespace Crescendo.Services;

public enum AppTheme { System, Dark, Light }

public sealed class HotkeyBinding
{
    public bool Enabled { get; set; } = true;
    public string Modifiers { get; set; } = "Ctrl+Alt";
    public string Key { get; set; } = string.Empty;

    public override string ToString() =>
        string.IsNullOrEmpty(Key) ? "Not set" : $"{Modifiers}+{DisplayKey(Key)}";

    /// <summary>WPF key names are not what is printed on the keycap.</summary>
    private static string DisplayKey(string key) => key switch
    {
        "OemPeriod" => ".",
        "OemComma" => ",",
        "OemMinus" => "-",
        "OemPlus" => "=",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "OemOpenBrackets" => "[",
        "Oem6" => "]",
        "Oem5" => "\\",
        "OemQuotes" => "'",
        "Oem3" => "`",
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
        _ => key
    };

    public bool Matches(string modifiers, string key) =>
        string.Equals(Modifiers, modifiers, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Key, key, StringComparison.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public string? LastDeviceId { get; set; }
    public EffectSlot EffectSlot { get; set; } = EffectSlot.Mode;

    /// <summary>
    /// False only on a fresh install, until the first-run tour is finished or
    /// skipped. Null in files written before the tour existed: those users
    /// already know the app and are not shown it uninvited.
    /// </summary>
    public bool? TourSeen { get; set; }

    /// <summary>How much one boost hotkey press moves the slider, in percent.</summary>
    public int BoostStepPercent { get; set; } = 25;

    // Ctrl + . / , sit where a laptop's Fn+. / Fn+, would be. Fn itself never
    // reaches Windows -- the keyboard firmware consumes it -- so it cannot be
    // part of a software hotkey.
    public HotkeyBinding BoostUp { get; set; } = new() { Modifiers = "Ctrl", Key = "OemPeriod" };
    public HotkeyBinding BoostDown { get; set; } = new() { Modifiers = "Ctrl", Key = "OemComma" };
    public HotkeyBinding ToggleEngine { get; set; } = new() { Key = "B" };
    public HotkeyBinding ResetBoost { get; set; } = new() { Key = "D0" };

    /// <summary>
    /// Brings bindings saved by older versions onto the current defaults, but
    /// only where the user never changed them.
    /// </summary>
    public void MigrateHotkeys()
    {
        if (BoostUp.Matches("Ctrl+Alt", "Up")) BoostUp = new() { Modifiers = "Ctrl", Key = "OemPeriod" };
        if (BoostDown.Matches("Ctrl+Alt", "Down")) BoostDown = new() { Modifiers = "Ctrl", Key = "OemComma" };
        // "0" parses to Key.None, so this binding silently never registered.
        if (ResetBoost.Key == "0") ResetBoost.Key = "D0";
    }

    public Dictionary<string, AudioProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persists settings and per-device profiles under ProgramData.
/// </summary>
/// <remarks>
/// ProgramData rather than AppData because Crescendo runs elevated and its
/// settings describe machine-wide audio state: the same profile should apply
/// whichever account is signed in.
/// Saves are written to a temp file and moved into place, so a crash or a power
/// cut during a save cannot leave a truncated settings file behind.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Crescendo");
        _path = Path.Combine(_directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings { TourSeen = false };
            string json = File.ReadAllText(_path);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            settings.MigrateHotkeys();
            return settings;
        }
        catch (Exception)
        {
            // A corrupt settings file must never stop the app from starting;
            // defaults are always a valid state.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string json = JsonSerializer.Serialize(settings, Options);

                string temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception)
            {
                // Losing a settings write is recoverable; crashing over it is not.
            }
        }
    }

    /// <summary>Returns the stored profile for a device, creating a default one if needed.</summary>
    public static AudioProfile GetOrCreateProfile(AppSettings settings, string endpointId, string deviceName)
    {
        if (settings.Profiles.TryGetValue(endpointId, out AudioProfile? existing))
        {
            existing.DeviceName = deviceName;
            // Older files may predate a band count change.
            if (existing.Bands.Count != AudioProfile.DefaultBandFrequencies.Length)
                existing.Bands = AudioProfile.CreateFlatBands();
            return existing;
        }

        var profile = new AudioProfile
        {
            EndpointId = endpointId,
            DeviceName = deviceName
        };
        settings.Profiles[endpointId] = profile;
        return profile;
    }
}
