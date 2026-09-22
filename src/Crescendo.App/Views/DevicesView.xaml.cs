using System.Windows.Controls;
using Crescendo.Interop;
using Crescendo.ViewModels;

namespace Crescendo.Views;

public partial class DevicesView : UserControl
{
    public DevicesView() => InitializeComponent();

    /// <summary>
    /// Records the chosen graph position. It only takes effect on the next
    /// install, which the UI states plainly beneath the chips.
    /// </summary>
    private void OnSlotChecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (sender is not RadioButton { Tag: EffectSlot slot }) return;
        if (viewModel.Settings.EffectSlot == slot) return;

        viewModel.Settings.EffectSlot = slot;
        viewModel.SaveNow();
    }
}
