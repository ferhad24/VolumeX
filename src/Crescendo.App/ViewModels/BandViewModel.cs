using Crescendo.Models;

namespace Crescendo.ViewModels;

/// <summary>One equaliser band bound to a vertical slider.</summary>
public sealed class BandViewModel(BandSetting band, Action onChanged) : ObservableObject
{
    private readonly BandSetting _band = band;
    private readonly Action _onChanged = onChanged;

    public float FrequencyHz => _band.FrequencyHz;

    /// <summary>"31", "1k", "16k" — what actually fits under a narrow slider.</summary>
    public string Label => _band.FrequencyHz >= 1000f
        ? $"{_band.FrequencyHz / 1000f:0.#}k"
        : $"{_band.FrequencyHz:0}";

    public double GainDb
    {
        get => _band.GainDb;
        set
        {
            float clamped = (float)Math.Clamp(value, -12.0, 12.0);
            if (Math.Abs(_band.GainDb - clamped) < 0.001f) return;
            _band.GainDb = clamped;
            Raise();
            Raise(nameof(GainText));
            _onChanged();
        }
    }

    public string GainText => _band.GainDb switch
    {
        0f => "0",
        > 0f => $"+{_band.GainDb:0.#}",
        _ => $"{_band.GainDb:0.#}"
    };

    /// <summary>Refreshes the UI after a preset rewrote the underlying model.</summary>
    public void Refresh()
    {
        Raise(nameof(GainDb));
        Raise(nameof(GainText));
    }
}
