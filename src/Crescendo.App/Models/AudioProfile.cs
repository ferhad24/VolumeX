using System.Text.Json.Serialization;

namespace Crescendo.Models;

/// <summary>One band of the 10-band graphic equaliser.</summary>
public sealed class BandSetting
{
    public float FrequencyHz { get; set; }
    public float GainDb { get; set; }
    public float Q { get; set; } = 1.41f;

    public BandSetting() { }

    public BandSetting(float frequencyHz, float gainDb = 0f, float q = 1.41f)
    {
        FrequencyHz = frequencyHz;
        GainDb = gainDb;
        Q = q;
    }

    public BandSetting Clone() => new(FrequencyHz, GainDb, Q);
}

/// <summary>A per-application boost, keyed by executable name.</summary>
public sealed class AppBoost
{
    /// <summary>Executable name without the path, lower-cased, e.g. "chrome.exe".</summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>Display name captured the first time the session was seen.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>1.0 = 100%. Bounded by the engine ceiling, not by this value.</summary>
    public float Boost { get; set; } = 1.0f;

    public bool Muted { get; set; }
}

/// <summary>
/// The complete state of the audio chain for one playback device.
/// </summary>
/// <remarks>
/// Profiles are stored per endpoint id. Switching devices loads that device's
/// profile, which is why headphones can sit at 300% while speakers stay at 100%
/// without either one clobbering the other.
/// </remarks>
public sealed class AudioProfile
{
    public static readonly float[] DefaultBandFrequencies =
        [31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    public string EndpointId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;

    public bool Enabled { get; set; }
    public float Boost { get; set; } = 1.0f;

    public bool LimiterEnabled { get; set; } = true;
    public float LimiterCeilingDb { get; set; } = -0.3f;
    public float LimiterReleaseMs { get; set; } = 120f;
    public float LimiterLookaheadMs { get; set; } = 5f;
    public bool SoftClip { get; set; }

    public bool EqEnabled { get; set; } = true;
    public bool ToneEnabled { get; set; } = true;
    public float BassDb { get; set; }
    public float TrebleDb { get; set; }

    public bool SubsonicFilter { get; set; } = true;
    public bool Mono { get; set; }
    public bool SwapChannels { get; set; }
    public float Balance { get; set; }

    public float PreampDb { get; set; }
    public float OutputTrimDb { get; set; }

    public string PresetName { get; set; } = "Flat";

    public List<BandSetting> Bands { get; set; } = CreateFlatBands();

    public List<AppBoost> AppBoosts { get; set; } = [];

    [JsonIgnore]
    public bool IsDefaultDevice { get; set; }

    public static List<BandSetting> CreateFlatBands() =>
        [.. DefaultBandFrequencies.Select(f => new BandSetting(f))];

    public AudioProfile Clone()
    {
        return new AudioProfile
        {
            EndpointId = EndpointId,
            DeviceName = DeviceName,
            Enabled = Enabled,
            Boost = Boost,
            LimiterEnabled = LimiterEnabled,
            LimiterCeilingDb = LimiterCeilingDb,
            LimiterReleaseMs = LimiterReleaseMs,
            LimiterLookaheadMs = LimiterLookaheadMs,
            SoftClip = SoftClip,
            EqEnabled = EqEnabled,
            ToneEnabled = ToneEnabled,
            BassDb = BassDb,
            TrebleDb = TrebleDb,
            SubsonicFilter = SubsonicFilter,
            Mono = Mono,
            SwapChannels = SwapChannels,
            Balance = Balance,
            PreampDb = PreampDb,
            OutputTrimDb = OutputTrimDb,
            PresetName = PresetName,
            Bands = [.. Bands.Select(b => b.Clone())],
            AppBoosts = [.. AppBoosts.Select(a => new AppBoost
            {
                Executable = a.Executable,
                DisplayName = a.DisplayName,
                Boost = a.Boost,
                Muted = a.Muted
            })]
        };
    }
}
