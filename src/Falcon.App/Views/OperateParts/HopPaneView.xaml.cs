namespace Falcon.App.Views.OperateParts;

/// <summary>
/// HOP pane (plan §4.3), split out of OperatePage.xaml (GUI-rejigger
/// Wave 0 — no behavior change). No logic: BindingContext is inherited
/// from the hosting page (OperateViewModel).
/// </summary>
public partial class HopPaneView : ContentView
{
    public HopPaneView()
    {
        InitializeComponent();
    }
}
