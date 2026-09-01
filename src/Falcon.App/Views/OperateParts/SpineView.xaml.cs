namespace Falcon.App.Views.OperateParts;

/// <summary>
/// Operate spine (plan §4.1), split out of OperatePage.xaml (GUI-rejigger
/// Wave 0 — no behavior change). No logic: BindingContext is inherited
/// from the hosting page (OperateViewModel).
/// </summary>
public partial class SpineView : ContentView
{
    public SpineView()
    {
        InitializeComponent();
    }
}
