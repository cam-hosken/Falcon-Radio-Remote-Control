#if WINDOWS
using Falcon.App.Core.ViewModels;
#endif

namespace Falcon.App.Views.OperateParts;

/// <summary>
/// SSB pane (GUI rejigger W3). BindingContext is inherited from the hosting
/// page (OperateViewModel). The only logic here is F3a: on Windows, the
/// keyboard-VFO arming gesture is attached to the frequency readout digit
/// strips (the digit chevron Buttons swallow their own taps; a tap on the
/// digit text or the gaps toggles arming). Android has no keyboard path —
/// no gesture is added there (chevrons only). The keyboard routing itself
/// stays in OperatePage.xaml.cs; all decisions live in VfoViewModel.
/// </summary>
public partial class SsbPaneView : ContentView
{
    public SsbPaneView()
    {
        InitializeComponent();
#if WINDOWS
        RxDigitStrip.GestureRecognizers.Add(ArmTap());
        TxDigitStrip.GestureRecognizers.Add(ArmTap());
#endif
    }

#if WINDOWS
    private static TapGestureRecognizer ArmTap()
    {
        var tap = new TapGestureRecognizer();
        tap.SetBinding(TapGestureRecognizer.CommandProperty,
            static (OperateViewModel vm) => vm.Ssb.Vfo.ToggleArmCommand);
        return tap;
    }
#endif
}
