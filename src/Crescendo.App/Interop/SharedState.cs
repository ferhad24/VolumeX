using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Crescendo.Interop;

/// <summary>
/// Owns the two shared-memory pages the APO talks through.
/// </summary>
/// <remarks>
/// <para>
/// These are created with <c>CreateFileMapping</c> rather than
/// <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/> because the pages
/// live in the <c>Global\</c> namespace and need an explicit DACL: audiodg.exe
/// runs as LOCAL SERVICE and would otherwise be unable to open them.
/// </para>
/// <para>
/// Writes use a seqlock. The sequence counter goes odd before the payload is
/// touched and even again afterwards, so the audio thread can detect a torn read
/// and retry rather than block. Nothing here ever takes a lock the APO could
/// wait on.
/// </para>
/// </remarks>
internal sealed unsafe class SharedState : IDisposable
{
    // Everyone gets read access to the config (the APO only reads it); the meter
    // additionally needs write access because the APO publishes levels into it.
    private const string ConfigSddl = "D:(A;;GA;;;BA)(A;;GA;;;SY)(A;;GR;;;WD)";
    private const string MeterSddl = "D:(A;;GA;;;BA)(A;;GA;;;SY)(A;;GRGW;;;WD)";

    private IntPtr _configHandle;
    private IntPtr _meterHandle;
    private ConfigAbi* _config;
    private MeterAbi* _meter;
    private uint _seq;
    private bool _disposed;

    public bool IsOpen => _config != null && _meter != null;

    /// <summary>
    /// Opens both pages. Never throws: creating a <c>Global\</c> object needs
    /// SeCreateGlobalPrivilege, and if Crescendo is somehow running without it
    /// the UI should still come up and say so rather than fail to start.
    /// </summary>
    public static SharedState Create()
    {
        var state = new SharedState();
        try
        {
            state.Open();
        }
        catch (Exception ex)
        {
            state.FailureReason = ex.Message;
            state.Dispose();
        }
        return state;
    }

    /// <summary>Why the shared block could not be opened, if it could not be.</summary>
    public string? FailureReason { get; private set; }

    private void Open()
    {
        _configHandle = CreateMapping(Abi.ConfigMapName, sizeof(ConfigAbi), ConfigSddl, out bool configExisted);
        _config = (ConfigAbi*)MapView(_configHandle, sizeof(ConfigAbi));

        _meterHandle = CreateMapping(Abi.MeterMapName, sizeof(MeterAbi), MeterSddl, out bool meterExisted);
        _meter = (MeterAbi*)MapView(_meterHandle, sizeof(MeterAbi));

        // A page that already existed belongs to a live APO instance; stamping the
        // header again is harmless and keeps a half-initialised page from sticking.
        _config->Magic = Abi.ConfigMagic;
        _config->StructSize = (uint)sizeof(ConfigAbi);

        _meter->Magic = Abi.MeterMagic;
        _meter->StructSize = (uint)sizeof(MeterAbi);

        if (!configExisted)
        {
            _config->Seq = 0;
            _config->Flags = 0;
            _config->Boost = 1.0f;
        }
        if (!meterExisted)
        {
            _meter->Seq = 0;
            _meter->Heartbeat = 0;
        }

        _seq = _config->Seq;
    }

    /// <summary>
    /// Publishes a complete configuration snapshot to the APO.
    /// </summary>
    public void Write(in ConfigAbi value)
    {
        if (_config == null) return;

        ConfigAbi local = value;
        local.Magic = Abi.ConfigMagic;
        local.StructSize = (uint)sizeof(ConfigAbi);
        // The full-struct copy below would otherwise zero the watchdog stamp,
        // which the APO reads as "no watchdog".
        local.UiHeartbeatMs = CurrentStamp();

        // Odd sequence = update in flight. The odd value is also baked into the
        // struct that gets copied, so the counter never dips back to an even
        // number partway through the store.
        _seq = (_seq + 1) | 1u;
        local.Seq = _seq;

        Volatile.Write(ref _config->Seq, _seq);
        Thread.MemoryBarrier();

        *_config = local;

        Thread.MemoryBarrier();
        _seq += 1;
        Volatile.Write(ref _config->Seq, _seq);
    }

    /// <summary>
    /// Tells the APO the UI is still alive. A single aligned 32-bit store is
    /// atomic, so this bypasses the seqlock and never disturbs a reader.
    /// </summary>
    public void StampHeartbeat()
    {
        if (_config == null) return;
        Volatile.Write(ref _config->UiHeartbeatMs, CurrentStamp());
    }

    // Same clock as GetTickCount in the APO. Never zero (zero means "no
    // watchdog"), and never rounded upward: a stamp ahead of the APO's clock
    // once read as four billion milliseconds old and bypassed every other block.
    private static uint CurrentStamp()
    {
        uint now = unchecked((uint)Environment.TickCount);
        return now == 0 ? uint.MaxValue : now;
    }

    /// <summary>
    /// Reads the meter page. Returns <c>false</c> while the APO is mid-update or
    /// has never run.
    /// </summary>
    public bool TryReadMeter(out MeterSnapshot snapshot)
    {
        snapshot = default;
        if (_meter == null) return false;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint s1 = Volatile.Read(ref _meter->Seq);
            if ((s1 & 1u) != 0) continue;

            var copy = new MeterSnapshot
            {
                Channels = (int)Math.Min(_meter->Channels, Abi.MaxChannels),
                SampleRate = (int)_meter->SampleRate,
                FrameCount = (int)_meter->FrameCount,
                Heartbeat = _meter->Heartbeat,
                GainReductionDb = _meter->GainReductionDb,
                PeakIn = new float[Abi.MaxChannels],
                PeakOut = new float[Abi.MaxChannels]
            };

            for (int c = 0; c < Abi.MaxChannels; c++)
            {
                copy.PeakIn[c] = _meter->PeakIn[c];
                copy.PeakOut[c] = _meter->PeakOut[c];
            }

            uint s2 = Volatile.Read(ref _meter->Seq);
            if (s1 != s2) continue;

            snapshot = copy;
            return true;
        }
        return false;
    }

    private static IntPtr CreateMapping(string name, int size, string sddl, out bool alreadyExisted)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out descriptor, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not build the security descriptor for {name}.");

            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = false
            };

            IntPtr handle = CreateFileMappingW(
                INVALID_HANDLE_VALUE, ref attributes, PAGE_READWRITE, 0, (uint)size, name);

            int error = Marshal.GetLastWin32Error();
            if (handle == IntPtr.Zero)
                throw new Win32Exception(error, $"Could not create the shared block {name}.");

            alreadyExisted = error == ERROR_ALREADY_EXISTS;
            return handle;
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    private static void* MapView(IntPtr handle, int size)
    {
        void* view = MapViewOfFile(handle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, (UIntPtr)size);
        if (view == null)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not map the shared block into this process.");
        return view;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_config != null) { UnmapViewOfFile(_config); _config = null; }
        if (_meter != null) { UnmapViewOfFile(_meter); _meter = null; }
        if (_configHandle != IntPtr.Zero) { CloseHandle(_configHandle); _configHandle = IntPtr.Zero; }
        if (_meterHandle != IntPtr.Zero) { CloseHandle(_meterHandle); _meterHandle = IntPtr.Zero; }
    }

    // ---- native ------------------------------------------------------------

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_READ = 0x0004;
    private const uint FILE_MAP_WRITE = 0x0002;
    private const int ERROR_ALREADY_EXISTS = 183;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(
        IntPtr hFile, ref SECURITY_ATTRIBUTES lpAttributes, uint flProtect,
        uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(void* lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, uint revision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

public struct MeterSnapshot
{
    public int Channels;
    public int SampleRate;
    public int FrameCount;
    public ulong Heartbeat;
    public float GainReductionDb;
    public float[] PeakIn;
    public float[] PeakOut;
}
