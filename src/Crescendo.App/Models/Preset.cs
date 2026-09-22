namespace Crescendo.Models;

/// <summary>
/// A named voicing that can be applied on top of whatever boost is set.
/// </summary>
/// <remarks>
/// Presets deliberately leave <see cref="AudioProfile.Boost"/> alone: choosing
/// "Movie" should change how the system sounds, not how loud it suddenly gets.
/// The limiter release differs per preset because the right recovery time is
/// programme-dependent — fast on speech, slow on music, or the limiter starts
/// breathing audibly.
/// </remarks>
public sealed class Preset
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required float[] BandGainsDb { get; init; }

    public float BassDb { get; init; }
    public float TrebleDb { get; init; }
    public float LimiterReleaseMs { get; init; } = 120f;
    public bool Mono { get; init; }

    /// <summary>Glyph shown on the preset chip (Segoe Fluent Icons).</summary>
    public required string Glyph { get; init; }

    public bool IsCustom { get; init; }

    public static readonly Preset Flat = new()
    {
        Name = "Flat",
        Description = "No colouration. Boost and limiter only.",
        Glyph = "",
        BandGainsDb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        LimiterReleaseMs = 120f
    };

    public static readonly Preset Music = new()
    {
        Name = "Music",
        Description = "Gentle smile curve. Open highs, firm low end.",
        Glyph = "",
        BandGainsDb = [3.0f, 2.0f, 0.5f, -1.0f, -1.0f, 0f, 1.0f, 2.0f, 3.0f, 2.0f],
        BassDb = 2.0f,
        TrebleDb = 1.0f,
        LimiterReleaseMs = 180f
    };

    public static readonly Preset Movie = new()
    {
        Name = "Movie",
        Description = "Cinematic weight with dialogue kept forward.",
        Glyph = "",
        BandGainsDb = [4.0f, 3.0f, 0f, -1.5f, 1.0f, 2.5f, 2.5f, 1.0f, 2.0f, 1.0f],
        BassDb = 3.0f,
        TrebleDb = 2.0f,
        LimiterReleaseMs = 250f
    };

    public static readonly Preset Gaming = new()
    {
        Name = "Gaming",
        Description = "Footsteps and cues lifted out of the mix.",
        Glyph = "",
        BandGainsDb = [2.0f, 1.0f, -1.0f, -2.0f, 0f, 2.0f, 4.0f, 4.0f, 3.0f, 1.0f],
        BassDb = 1.0f,
        TrebleDb = 2.0f,
        LimiterReleaseMs = 80f
    };

    public static readonly Preset Voice = new()
    {
        Name = "Voice",
        Description = "Speech band pushed, rumble and hiss pulled down.",
        Glyph = "",
        BandGainsDb = [-6.0f, -4.0f, -1.0f, 2.0f, 4.0f, 4.0f, 3.0f, 1.0f, -1.0f, -3.0f],
        BassDb = -3.0f,
        TrebleDb = -1.0f,
        LimiterReleaseMs = 60f
    };

    public static readonly Preset Custom = new()
    {
        Name = "Custom",
        Description = "Your own curve.",
        Glyph = "",
        BandGainsDb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        IsCustom = true
    };

    public static readonly IReadOnlyList<Preset> All = [Flat, Music, Movie, Gaming, Voice, Custom];

    public static Preset? ByName(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes this preset's voicing into a profile, leaving boost untouched.</summary>
    public void ApplyTo(AudioProfile profile)
    {
        profile.PresetName = Name;
        if (IsCustom) return;

        for (int i = 0; i < profile.Bands.Count && i < BandGainsDb.Length; i++)
            profile.Bands[i].GainDb = BandGainsDb[i];

        profile.BassDb = BassDb;
        profile.TrebleDb = TrebleDb;
        profile.LimiterReleaseMs = LimiterReleaseMs;
        if (Mono) profile.Mono = true;
    }

    /// <summary>
    /// True when the profile still matches this preset exactly; used to decide
    /// whether moving a slider should flip the selection to Custom.
    /// </summary>
    public bool Matches(AudioProfile profile)
    {
        if (IsCustom) return false;
        if (Math.Abs(profile.BassDb - BassDb) > 0.01f) return false;
        if (Math.Abs(profile.TrebleDb - TrebleDb) > 0.01f) return false;

        for (int i = 0; i < profile.Bands.Count && i < BandGainsDb.Length; i++)
        {
            if (Math.Abs(profile.Bands[i].GainDb - BandGainsDb[i]) > 0.01f) return false;
        }
        return true;
    }
}
