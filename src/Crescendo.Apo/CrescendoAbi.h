//
// CrescendoAbi.h -- Shared-memory contract between the Crescendo UI (C#) and
//                   the Crescendo APO running inside audiodg.exe.
//
// This header is the single source of truth for the binary layout. The managed
// side mirrors it in Crescendo.App/Interop/CrescendoAbi.cs -- keep both in sync.
//
// Concurrency model: a seqlock. Writers bump `seq` to an odd value, write the
// payload, then bump it to the next even value. Readers sample `seq`, copy, and
// re-sample; if the value changed or is odd, they retry. No locks are ever taken
// on the real-time audio thread.
//
#pragma once
#include <stdint.h>

#define CRESCENDO_CONFIG_MAP   L"Global\\Crescendo.Config.v1"
#define CRESCENDO_METER_MAP    L"Global\\Crescendo.Meter.v1"

#define CRESCENDO_CONFIG_MAGIC 0x31535243u  // 'CRS1'
#define CRESCENDO_METER_MAGIC  0x314D5243u  // 'CRM1'

#define CRESCENDO_MAX_BANDS    10
#define CRESCENDO_MAX_CHANNELS 8

// Config flags
#define CRES_FLAG_ENABLED      0x0001u  // master boost engine on
#define CRES_FLAG_LIMITER      0x0002u  // brickwall limiter on
#define CRES_FLAG_EQ           0x0004u  // graphic EQ on
#define CRES_FLAG_TONE         0x0008u  // bass/treble shelves on
#define CRES_FLAG_MONO         0x0010u  // downmix to mono
#define CRES_FLAG_SUBSONIC     0x0020u  // 20 Hz DC/rumble filter
#define CRES_FLAG_SOFTCLIP     0x0040u  // saturate instead of hard clamp
#define CRES_FLAG_SWAP_LR      0x0080u  // swap left/right

#pragma pack(push, 4)

struct CrescendoBand
{
    float freqHz;   // center frequency
    float gainDb;   // -12 .. +12
    float q;        // typically 1.41 for a 10-band graphic EQ
};

struct CrescendoConfig
{
    uint32_t magic;
    uint32_t structSize;
    volatile uint32_t seq;      // seqlock counter
    uint32_t flags;

    float boost;                // linear gain, 1.0 = 100% .. 5.0 = 500%
    float preampDb;             // manual trim applied before the EQ
    float outputTrimDb;         // final trim after the limiter

    float bassDb;               // low shelf @ 100 Hz
    float trebleDb;             // high shelf @ 8 kHz

    float balance;              // -1.0 (full left) .. +1.0 (full right)

    float limiterCeilingDb;     // e.g. -0.3 dBFS
    float limiterReleaseMs;     // e.g. 120 ms
    float limiterLookaheadMs;   // e.g. 5 ms

    uint32_t bandCount;
    CrescendoBand bands[CRESCENDO_MAX_BANDS];

    // Watchdog. The UI stamps GetTickCount (ms) here roughly 30 times a second,
    // outside the seqlock -- a single aligned 32-bit store is atomic. If the
    // stamp goes stale the APO bypasses, so a crashed or killed UI can never
    // leave the whole system stuck at 500%. Zero means "no watchdog" (a UI that
    // predates it) and is ignored.
    volatile uint32_t uiHeartbeatMs;

    uint32_t reserved[7];
};

#define CRESCENDO_UI_TIMEOUT_MS 3000u

struct CrescendoMeter
{
    uint32_t magic;
    uint32_t structSize;
    volatile uint32_t seq;
    uint32_t channels;

    uint32_t sampleRate;
    uint32_t frameCount;        // frames per APO block
    uint64_t heartbeat;         // incremented every processed block

    float peakIn[CRESCENDO_MAX_CHANNELS];   // linear peak before boost
    float peakOut[CRESCENDO_MAX_CHANNELS];  // linear peak after the chain
    float gainReductionDb;                  // current limiter attenuation (>= 0)
    float cpuLoadPercent;                   // rough per-block DSP cost

    uint32_t reserved[8];
};

#pragma pack(pop)
