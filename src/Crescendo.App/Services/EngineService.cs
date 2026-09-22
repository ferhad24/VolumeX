using System.IO;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Crescendo.Interop;
using Crescendo.Models;

namespace Crescendo.Services;

public enum EngineHealth
{
    /// <summary>The APO is not registered at all.</summary>
    NotInstalled,

    /// <summary>Registered, but not attached to the selected playback device.</summary>
    NotAttached,

    /// <summary>Attached, but Windows has not loaded it yet — the audio service needs a restart.</summary>
    PendingRestart,

    /// <summary>Attached and idle: loaded, waiting for audio to play.</summary>
    Idle,

    /// <summary>Attached and actively processing audio right now.</summary>
    Processing
}

public sealed record EngineStatus(
    EngineHealth Health,
    bool SignatureCheckDisabled,
    EffectSlot? Slot,
    string? ReplacedClsid,
    int SampleRate,
    int Channels);

/// <summary>
/// The bridge between the UI's profile model and the APO running inside
/// audiodg.exe.
/// </summary>
public sealed class EngineService : IDisposable
{
    private readonly SharedState _shared;
    private readonly ApoInstaller _installer = new();
    private readonly System.Threading.Timer _meterTimer;

    private ulong _lastHeartbeat;
    private DateTime _lastHeartbeatChange = DateTime.MinValue;
    private string? _endpointId;
    private bool _disposed;

    /// <summary>Raised roughly 30 times a second on a thread-pool thread.</summary>
    public event Action<MeterSnapshot>? MeterUpdated;

    public EngineService()
    {
        _shared = SharedState.Create();
        _meterTimer = new System.Threading.Timer(OnMeterTick, null, 33, 33);
    }

    public string EngineDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Crescendo", "Engine");

    public string InstalledDllPath => Path.Combine(EngineDirectory, "CrescendoApo.dll");

    public void SetEndpoint(string? endpointId) => _endpointId = endpointId;

    // ------------------------------------------------------------ status

    /// <summary>
    /// Why the link to the engine could not be opened, or null when it is fine.
    /// Without it the UI still works, but nothing it changes reaches the audio.
    /// </summary>
    public string? LinkFailure => _shared.FailureReason;

    public EngineStatus GetStatus(string? endpointId)
    {
        ApoState state = _installer.GetState(endpointId);

        EngineHealth health;
        int sampleRate = 0, channels = 0;

        if (!state.EngineRegistered)
        {
            health = EngineHealth.NotInstalled;
        }
        else if (!state.AttachedToEndpoint)
        {
            health = EngineHealth.NotAttached;
        }
        else if (_shared.TryReadMeter(out MeterSnapshot meter) && meter.Heartbeat > 0)
        {
            sampleRate = meter.SampleRate;
            channels = meter.Channels;
            // The heartbeat only advances while audio is flowing, so a stalled
            // one means "no sound right now", not "broken".
            health = (DateTime.UtcNow - _lastHeartbeatChange) < TimeSpan.FromMilliseconds(600)
                ? EngineHealth.Processing
                : EngineHealth.Idle;
        }
        else
        {
            health = EngineHealth.PendingRestart;
        }

        return new EngineStatus(health, state.SignatureCheckDisabled, state.AttachedSlot,
            state.ReplacedClsid, sampleRate, channels);
    }

    // ------------------------------------------------------------ configuration

    /// <summary>
    /// The gain the APO runs at: the master boost multiplied by the loudest
    /// per-application boost, because the APO applies one gain to the whole mix
    /// and quieter applications are brought back down through the mixer.
    /// </summary>
    public static float ComputeEngineGain(AudioProfile profile)
    {
        float loudestApp = 1.0f;
        foreach (AppBoost app in profile.AppBoosts)
        {
            if (!app.Muted && app.Boost > loudestApp) loudestApp = app.Boost;
        }
        return Math.Clamp(profile.Boost * loudestApp, 0.05f, 8.0f);
    }

    public void Push(AudioProfile profile)
    {
        var config = new ConfigAbi
        {
            Flags = (uint)BuildFlags(profile),
            Boost = ComputeEngineGain(profile),
            PreampDb = profile.PreampDb,
            OutputTrimDb = profile.OutputTrimDb,
            BassDb = profile.BassDb,
            TrebleDb = profile.TrebleDb,
            Balance = Math.Clamp(profile.Balance, -1f, 1f),
            LimiterCeilingDb = Math.Clamp(profile.LimiterCeilingDb, -12f, 0f),
            LimiterReleaseMs = Math.Clamp(profile.LimiterReleaseMs, 5f, 2000f),
            LimiterLookaheadMs = Math.Clamp(profile.LimiterLookaheadMs, 0.2f, 20f),
            BandCount = (uint)Math.Min(profile.Bands.Count, Abi.MaxBands)
        };

        for (int i = 0; i < config.BandCount; i++)
        {
            BandSetting band = profile.Bands[i];
            config.SetBand(i, band.FrequencyHz, Math.Clamp(band.GainDb, -15f, 15f),
                band.Q > 0 ? band.Q : 1.41f);
        }

        _lastConfig = config;
        _shared.Write(config);
    }

    private ConfigAbi _lastConfig;

    /// <summary>
    /// Puts the engine into bypass without touching the saved profile. Called
    /// on exit: the APO outlives this process, and a boost nobody can see a
    /// control for is a boost nobody can turn down.
    /// </summary>
    public void PushBypass()
    {
        ConfigAbi config = _lastConfig;
        config.Flags &= ~(uint)EngineFlags.Enabled;
        _shared.Write(config);
    }

    private static EngineFlags BuildFlags(AudioProfile profile)
    {
        EngineFlags flags = EngineFlags.None;
        if (profile.Enabled) flags |= EngineFlags.Enabled;
        if (profile.LimiterEnabled) flags |= EngineFlags.Limiter;
        if (profile.EqEnabled) flags |= EngineFlags.Equalizer;
        if (profile.ToneEnabled) flags |= EngineFlags.Tone;
        if (profile.Mono) flags |= EngineFlags.Mono;
        if (profile.SubsonicFilter) flags |= EngineFlags.Subsonic;
        if (profile.SoftClip) flags |= EngineFlags.SoftClip;
        if (profile.SwapChannels) flags |= EngineFlags.SwapLeftRight;
        return flags;
    }

    // ------------------------------------------------------------ install

    /// <summary>
    /// Copies the APO next to the app's installation, registers it, allows
    /// unsigned APOs to load and attaches it to one endpoint.
    /// </summary>
    /// <returns>True when the audio service must be restarted to take effect.</returns>
    public bool InstallAndAttach(string endpointId, EffectSlot slot)
    {
        Directory.CreateDirectory(EngineDirectory);
        GrantServiceReadAccess(EngineDirectory);

        string source = LocateSourceDll();
        // Copying over a DLL that audiodg has mapped will fail; that only happens
        // when the engine is already live, in which case the file is already current.
        if (!File.Exists(InstalledDllPath) || !FilesMatch(source, InstalledDllPath))
        {
            try
            {
                File.Copy(source, InstalledDllPath, overwrite: true);
            }
            catch (IOException) when (File.Exists(InstalledDllPath))
            {
                // audiodg.exe has the old engine mapped. Keeping it would mean an
                // update silently never takes effect, so stop the audio service
                // to release the file; the caller's restart brings it back.
                RunAsync("net", "stop AudioEndpointBuilder /y", CancellationToken.None).GetAwaiter().GetResult();
                File.Copy(source, InstalledDllPath, overwrite: true);
            }
        }

        _installer.RegisterEngine(InstalledDllPath);
        _installer.SetSignatureCheckDisabled(true);
        _installer.AttachToEndpoint(endpointId, slot);
        return true;
    }

    public void DetachEndpoint(string endpointId) => _installer.DetachFromEndpoint(endpointId);

    /// <summary>
    /// After an app update: if the engine is installed and the shipped DLL
    /// differs from the one audiodg loads, replace it. Never installs an engine
    /// that was not installed before -- that stays the user's explicit choice.
    /// </summary>
    /// <returns>True when the audio service must restart to load the new engine.</returns>
    public bool RefreshInstalledEngine()
    {
        if (!_installer.GetState(null).EngineRegistered) return false;

        string source = LocateSourceDll();
        if (File.Exists(InstalledDllPath) && FilesMatch(source, InstalledDllPath)) return false;

        try
        {
            File.Copy(source, InstalledDllPath, overwrite: true);
        }
        catch (IOException)
        {
            // Mapped by audiodg: stop the service to release it.
            RunAsync("net", "stop AudioEndpointBuilder /y", CancellationToken.None).GetAwaiter().GetResult();
            File.Copy(source, InstalledDllPath, overwrite: true);
        }

        _installer.RegisterEngine(InstalledDllPath);
        return true;
    }

    /// <summary>For the uninstaller: everything Crescendo changed, undone.</summary>
    public void Uninstall()
    {
        _installer.RemoveEverything();
        StartupService.SetEnabled(false, startMinimized: false);
    }

    public void RemoveEverything() => _installer.RemoveEverything();

    private string LocateSourceDll()
    {
        string appDir = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(appDir, "CrescendoApo.dll"),
            Path.Combine(appDir, "Engine", "CrescendoApo.dll"),
            // Development layout: src/Crescendo.App/bin/... alongside artifacts/
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "artifacts", "CrescendoApo.dll"))
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "CrescendoApo.dll was not found next to the application. Reinstall Crescendo.",
            candidates[0]);
    }

    private static bool FilesMatch(string a, string b)
    {
        // By content: installers and signing both rewrite timestamps, so a
        // timestamp comparison can call two different engines "the same".
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            using FileStream fa = File.OpenRead(a);
            using FileStream fb = new(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return sha.ComputeHash(fa).AsSpan().SequenceEqual(sha.ComputeHash(fb));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// audiodg.exe runs as LOCAL SERVICE. Program Files normally grants it read
    /// access through the Users group, but an explicit grant removes any doubt on
    /// machines with hardened ACLs.
    /// </summary>
    private static void GrantServiceReadAccess(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            DirectorySecurity security = info.GetAccessControl();
            var localService = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);

            security.AddAccessRule(new FileSystemAccessRule(
                localService,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception)
        {
            // Not fatal: the inherited Program Files ACL is usually sufficient.
        }
    }

    // ------------------------------------------------------------ audio service

    /// <summary>
    /// Restarts the Windows audio service so the endpoint graph is rebuilt and
    /// the APO is picked up.
    /// </summary>
    /// <remarks>
    /// Audio stops for a second or two and a few applications re-open their
    /// stream. It is the only reliable way to make Windows reload endpoint
    /// effects without disabling the device in Device Manager.
    /// </remarks>
    public static async Task RestartAudioServiceAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync("net", "stop AudioEndpointBuilder /y", cancellationToken).ConfigureAwait(false);
        await RunAsync("net", "start AudioEndpointBuilder", cancellationToken).ConfigureAwait(false);
        // Stopping the endpoint builder takes Audiosrv down with it, but starting
        // it does not always bring Audiosrv back.
        await RunAsync("net", "start Audiosrv", cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process? process = Process.Start(startInfo);
        if (process is null) return;
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ meter

    private void OnMeterTick(object? state)
    {
        if (_disposed) return;

        // Keep the APO's watchdog fed; if this stops for 3 s it bypasses.
        _shared.StampHeartbeat();

        if (!_shared.TryReadMeter(out MeterSnapshot snapshot)) return;

        if (snapshot.Heartbeat != _lastHeartbeat)
        {
            _lastHeartbeat = snapshot.Heartbeat;
            _lastHeartbeatChange = DateTime.UtcNow;
        }

        MeterUpdated?.Invoke(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _meterTimer.Dispose();
        _shared.Dispose();
    }
}
