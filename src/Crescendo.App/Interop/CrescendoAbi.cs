using System.Runtime.InteropServices;

namespace Crescendo.Interop;

/// <summary>
/// Managed mirror of <c>src/Crescendo.Apo/CrescendoAbi.h</c>.
/// Layout changes must be made in both files at once — the APO validates only
/// the magic number, not the field offsets.
/// </summary>
internal static class Abi
{
    public const string ConfigMapName = @"Global\Crescendo.Config.v1";
    public const string MeterMapName = @"Global\Crescendo.Meter.v1";

    public const uint ConfigMagic = 0x31535243; // 'CRS1'
    public const uint MeterMagic = 0x314D5243;  // 'CRM1'

    public const int MaxBands = 10;
    public const int MaxChannels = 8;
}

[Flags]
internal enum EngineFlags : uint
{
    None = 0,
    Enabled = 0x0001,
    Limiter = 0x0002,
    Equalizer = 0x0004,
    Tone = 0x0008,
    Mono = 0x0010,
    Subsonic = 0x0020,
    SoftClip = 0x0040,
    SwapLeftRight = 0x0080
}

// Both structs use fixed buffers rather than marshalled arrays so they stay
// blittable: the writer can drop one onto the mapped page with a single store
// instead of running the marshaller on the UI timer tick.

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct ConfigAbi
{
    public uint Magic;
    public uint StructSize;
    public uint Seq;
    public uint Flags;

    public float Boost;
    public float PreampDb;
    public float OutputTrimDb;

    public float BassDb;
    public float TrebleDb;

    public float Balance;

    public float LimiterCeilingDb;
    public float LimiterReleaseMs;
    public float LimiterLookaheadMs;

    public uint BandCount;

    /// <summary>Flattened <c>CrescendoBand[10]</c>: freqHz, gainDb, q per band.</summary>
    public fixed float Bands[Abi.MaxBands * 3];

    /// <summary>Watchdog stamp (Environment.TickCount, ms); see CrescendoAbi.h.</summary>
    public uint UiHeartbeatMs;

    public fixed uint Reserved[7];

    public void SetBand(int index, float freqHz, float gainDb, float q)
    {
        fixed (float* p = Bands)
        {
            p[index * 3 + 0] = freqHz;
            p[index * 3 + 1] = gainDb;
            p[index * 3 + 2] = q;
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct MeterAbi
{
    public uint Magic;
    public uint StructSize;
    public uint Seq;
    public uint Channels;

    public uint SampleRate;
    public uint FrameCount;
    public ulong Heartbeat;

    public fixed float PeakIn[Abi.MaxChannels];
    public fixed float PeakOut[Abi.MaxChannels];

    public float GainReductionDb;
    public float CpuLoadPercent;

    public fixed uint Reserved[8];
}
