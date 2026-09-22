using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Crescendo.Interop;
using Crescendo.Models;

namespace Crescendo.Services;

public sealed record AudioSessionInfo(
    string Executable,
    string DisplayName,
    uint ProcessId,
    bool IsActive,
    bool IsSystemSounds,
    string? ExecutablePath,
    float SessionVolume);

/// <summary>
/// Reads the per-application mixer and applies per-app boosts.
/// </summary>
/// <remarks>
/// <para>
/// Windows exposes no API to raise a single application above 100%. Crescendo
/// gets there by combining the two levers it does have: the APO applies the
/// loudest boost any application asks for, and every other session is then
/// attenuated to the correct ratio through <c>ISimpleAudioVolume</c>.
/// </para>
/// <para>
/// Example: Chrome at 400%, everything else at 100%. The APO runs at 4x and
/// every non-Chrome session is set to 0.25 — Chrome ends up 4x louder, the rest
/// unchanged, and nothing clips because the limiter still sees the summed signal.
/// </para>
/// <para>
/// The volume a session had before Crescendo touched it is remembered so
/// <see cref="RestoreAll"/> can hand the mixer back exactly as it was.
/// </para>
/// </remarks>
public sealed class SessionService : IDisposable
{
    private readonly DeviceService _devices;
    private readonly Dictionary<string, float> _originalVolumes = new(StringComparer.OrdinalIgnoreCase);
    private static Guid _eventContext = Guid.NewGuid();

    public SessionService(DeviceService devices) => _devices = devices;

    public IReadOnlyList<AudioSessionInfo> GetSessions(string deviceId)
    {
        var results = new List<AudioSessionInfo>();
        IAudioSessionManager2? manager = _devices.OpenSessionManager(deviceId);
        if (manager is null) return results;

        try
        {
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return results;
            try
            {
                if (sessions.GetCount(out int count) != 0) return results;

                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out IAudioSessionControl control) != 0) continue;
                    try
                    {
                        AudioSessionInfo? info = Describe(control);
                        if (info is not null) results.Add(info);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }

        // One row per executable: a browser with six audio sessions should appear
        // once, not six times.
        return results
            .GroupBy(s => s.Executable, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.IsActive).First())
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static AudioSessionInfo? Describe(IAudioSessionControl control)
    {
        if (control is not IAudioSessionControl2 control2) return null;

        control2.GetState(out AudioSessionState state);
        if (state == AudioSessionState.Expired) return null;

        bool isSystemSounds = control2.IsSystemSoundsSession() == 0;
        control2.GetProcessId(out uint pid);

        string executable;
        string display;
        string? path = null;

        if (isSystemSounds)
        {
            executable = "@systemsounds";
            display = "Windows system sounds";
        }
        else
        {
            path = TryGetProcessPath(pid);
            executable = path is not null ? Path.GetFileName(path) : $"pid-{pid}";
            display = DescribeProcess(pid, path, control2);
        }

        float volume = 1.0f;
        if (control is ISimpleAudioVolume simple)
            simple.GetMasterVolume(out volume);

        return new AudioSessionInfo(
            executable.ToLowerInvariant(),
            display,
            pid,
            state == AudioSessionState.Active,
            isSystemSounds,
            path,
            volume);
    }

    private static string DescribeProcess(uint pid, string? path, IAudioSessionControl2 control)
    {
        // The session's own display name is usually empty for desktop apps, so
        // fall back to the file description and finally to the file name.
        if (control.GetDisplayName(out string name) == 0 && !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
            return name;

        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    return info.FileDescription!;
            }
            catch (Exception) { /* unreadable metadata is not worth failing over */ }

            return Path.GetFileNameWithoutExtension(path);
        }

        return $"Process {pid}";
    }

    /// <summary>
    /// Pushes the per-app ratios into the Windows mixer.
    /// </summary>
    /// <param name="engineGain">
    /// The gain the APO is running at — the loudest boost any application asked
    /// for. Sessions are scaled relative to this so nothing is ever asked to
    /// exceed 1.0.
    /// </param>
    public void ApplyBoosts(string deviceId, float masterBoost, IReadOnlyList<AppBoost> boosts, float engineGain)
    {
        if (engineGain <= 0f) return;

        var byExecutable = boosts.ToDictionary(b => b.Executable, StringComparer.OrdinalIgnoreCase);

        IAudioSessionManager2? manager = _devices.OpenSessionManager(deviceId);
        if (manager is null) return;

        try
        {
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return;
            try
            {
                if (sessions.GetCount(out int count) != 0) return;

                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out IAudioSessionControl control) != 0) continue;
                    try
                    {
                        ApplyToSession(control, masterBoost, byExecutable, engineGain);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private void ApplyToSession(IAudioSessionControl control, float masterBoost,
        Dictionary<string, AppBoost> boosts, float engineGain)
    {
        if (control is not IAudioSessionControl2 control2) return;
        if (control is not ISimpleAudioVolume volume) return;

        AudioSessionInfo? info = Describe(control);
        if (info is null) return;

        control2.GetSessionInstanceIdentifier(out string instanceId);
        if (string.IsNullOrEmpty(instanceId)) return;

        boosts.TryGetValue(info.Executable, out AppBoost? app);
        float appBoost = app?.Boost ?? 1.0f;
        bool muted = app?.Muted ?? false;

        // Remember what the user had set before Crescendo first touched this
        // session, so RestoreAll can undo it precisely.
        if (!_originalVolumes.ContainsKey(instanceId))
        {
            volume.GetMasterVolume(out float current);
            _originalVolumes[instanceId] = current;
        }

        float userBase = _originalVolumes[instanceId];
        float effective = masterBoost * appBoost;
        float ratio = Math.Clamp(effective / engineGain, 0f, 1f);
        float target = Math.Clamp(userBase * ratio, 0f, 1f);

        volume.SetMute(muted, ref _eventContext);

        volume.GetMasterVolume(out float existing);
        if (Math.Abs(existing - target) > 0.002f)
            volume.SetMasterVolume(target, ref _eventContext);
    }

    /// <summary>
    /// Returns every session Crescendo has changed to the volume it had before.
    /// </summary>
    public void RestoreAll(string deviceId)
    {
        if (_originalVolumes.Count == 0) return;

        IAudioSessionManager2? manager = _devices.OpenSessionManager(deviceId);
        if (manager is null) return;

        try
        {
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return;
            try
            {
                if (sessions.GetCount(out int count) != 0) return;

                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out IAudioSessionControl control) != 0) continue;
                    try
                    {
                        if (control is not IAudioSessionControl2 control2) continue;
                        if (control is not ISimpleAudioVolume volume) continue;

                        control2.GetSessionInstanceIdentifier(out string instanceId);
                        if (instanceId is not null && _originalVolumes.TryGetValue(instanceId, out float original))
                        {
                            volume.SetMasterVolume(original, ref _eventContext);
                            volume.SetMute(false, ref _eventContext);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }

        _originalVolumes.Clear();
    }

    /// <summary>Forgets remembered volumes without changing anything.</summary>
    public void ForgetBaselines() => _originalVolumes.Clear();

    public void Dispose() => _originalVolumes.Clear();

    // ---- native ------------------------------------------------------------

    private static string? TryGetProcessPath(uint pid)
    {
        if (pid == 0) return null;

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
