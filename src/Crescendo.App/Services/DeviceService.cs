using System.Runtime.InteropServices;
using Crescendo.Interop;

namespace Crescendo.Services;

public sealed record AudioDevice(string Id, string Name, string Adapter, bool IsDefault)
{
    public string ShortId => ApoInstaller.ExtractEndpointGuid(Id);
}

/// <summary>
/// Enumerates playback endpoints and reports when Windows changes the default
/// one or a device is plugged in or removed.
/// </summary>
public sealed class DeviceService : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notifications;
    private bool _disposed;

    /// <summary>Raised on a COM thread; subscribers must marshal to the UI.</summary>
    public event Action? DevicesChanged;

    /// <summary>Raised when Windows switches the default playback device.</summary>
    public event Action<string>? DefaultDeviceChanged;

    public DeviceService()
    {
        Type? type = Type.GetTypeFromCLSID(CoreAudioGuids.MMDeviceEnumerator)
            ?? throw new InvalidOperationException("The Windows audio enumerator is unavailable.");

        _enumerator = (IMMDeviceEnumerator)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not start the Windows audio enumerator."));

        _notifications = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
    }

    public IReadOnlyList<AudioDevice> GetPlaybackDevices()
    {
        var devices = new List<AudioDevice>();
        string? defaultId = TryGetDefaultDeviceId();

        if (_enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out IMMDeviceCollection collection) != 0)
            return devices;

        try
        {
            if (collection.GetCount(out int count) != 0) return devices;

            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice device) != 0) continue;
                try
                {
                    if (device.GetId(out string id) != 0) continue;

                    string name = ReadProperty(device, PropertyKey.DeviceFriendlyName) ?? "Playback device";
                    string adapter = ReadProperty(device, PropertyKey.DeviceInterfaceFriendlyName) ?? string.Empty;

                    devices.Add(new AudioDevice(id, name, adapter,
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }

        return devices;
    }

    public string? TryGetDefaultDeviceId()
    {
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device) != 0)
            return null;
        try
        {
            return device.GetId(out string id) == 0 ? id : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>
    /// Opens the session manager for one endpoint. The caller owns the returned
    /// object and must release it.
    /// </summary>
    internal IAudioSessionManager2? OpenSessionManager(string deviceId)
    {
        if (_enumerator.GetDevice(deviceId, out IMMDevice device) != 0) return null;
        try
        {
            Guid iid = CoreAudioGuids.IAudioSessionManager2;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object instance) != 0) return null;
            return instance as IAudioSessionManager2;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Endpoint-level peak meter, used as a fallback when the APO is not loaded.</summary>
    internal IAudioMeterInformation? OpenMeter(string deviceId)
    {
        if (_enumerator.GetDevice(deviceId, out IMMDevice device) != 0) return null;
        try
        {
            Guid iid = CoreAudioGuids.IAudioMeterInformation;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object instance) != 0) return null;
            return instance as IAudioMeterInformation;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    internal IAudioEndpointVolume? OpenEndpointVolume(string deviceId)
    {
        if (_enumerator.GetDevice(deviceId, out IMMDevice device) != 0) return null;
        try
        {
            Guid iid = CoreAudioGuids.IAudioEndpointVolume;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object instance) != 0) return null;
            return instance as IAudioEndpointVolume;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static string? ReadProperty(IMMDevice device, PropertyKey key)
    {
        if (device.OpenPropertyStore(STGM_READ, out IPropertyStore store) != 0) return null;
        try
        {
            PropertyKey local = key;
            if (store.GetValue(ref local, out PropVariant value) != 0) return null;
            return value.AsString();
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); }
        catch (Exception) { /* the enumerator may already be torn down at shutdown */ }

        Marshal.ReleaseComObject(_enumerator);
    }

    private const uint CLSCTX_ALL = 23;
    private const uint STGM_READ = 0;

    private sealed class NotificationClient(DeviceService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.DevicesChanged?.Invoke();
        public void OnDeviceAdded(string deviceId) => owner.DevicesChanged?.Invoke();
        public void OnDeviceRemoved(string deviceId) => owner.DevicesChanged?.Invoke();

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId)
        {
            // Console and Multimedia usually move together; reacting to one role
            // avoids doing the same work three times.
            if (flow == EDataFlow.Render && role == ERole.Multimedia)
                owner.DefaultDeviceChanged?.Invoke(defaultDeviceId);
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
