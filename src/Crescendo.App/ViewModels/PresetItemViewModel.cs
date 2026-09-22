using Crescendo.Models;

namespace Crescendo.ViewModels;

/// <summary>
/// Wraps a <see cref="Preset"/> with its selection state.
/// </summary>
/// <remarks>
/// <para>
/// Presets themselves are immutable and shared, so selection cannot live on
/// them. Wrapping keeps the chips bindable without a converter that would have
/// to compare object identity inside a template.
/// </para>
/// <para>
/// Selection is applied from the setter rather than from a click command,
/// because a radio button can also be chosen with the keyboard or by assistive
/// technology — neither of which raises Click.
/// </para>
/// </remarks>
public sealed class PresetItemViewModel(Preset preset, Action<Preset> onSelected) : ObservableObject
{
    private readonly Action<Preset> _onSelected = onSelected;
    private bool _isSelected;
    private bool _suppress;

    public Preset Preset { get; } = preset;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            if (value && !_suppress) _onSelected(Preset);
        }
    }

    /// <summary>
    /// Updates the state without re-applying the preset. Used when the view
    /// model is the one deciding what is selected.
    /// </summary>
    public void SetSelectedQuietly(bool selected)
    {
        _suppress = true;
        try
        {
            IsSelected = selected;
        }
        finally
        {
            _suppress = false;
        }
    }
}
