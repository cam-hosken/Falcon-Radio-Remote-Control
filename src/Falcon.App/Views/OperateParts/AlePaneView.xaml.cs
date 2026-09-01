namespace Falcon.App.Views.OperateParts;

/// <summary>
/// ALE pane (plan §4.4), split out of OperatePage.xaml (GUI-rejigger
/// Wave 0), with the Messages and LQA content folded in (Wave 1, W4 —
/// plan N1: Messages on the main tab, LQA a sub-tab). No logic:
/// BindingContext is inherited from the hosting page (OperateViewModel);
/// all view state lives in the ViewModels.
/// </summary>
public partial class AlePaneView : ContentView
{
    public AlePaneView()
    {
        InitializeComponent();
    }
}
