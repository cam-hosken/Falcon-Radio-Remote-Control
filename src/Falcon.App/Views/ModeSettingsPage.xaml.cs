using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views;

public partial class ModeSettingsPage : ContentPage
{
    public ModeSettingsPage(ModeViewModel mode)
    {
        InitializeComponent();
        // N4: the SAME singleton ModeViewModel as the Operate spine — the
        // mode buttons genuinely switch the radio, the pane follows the
        // confirmed mode (Wave-1 coordination ruling: bind, don't modify).
        BindingContext = mode;
    }
}
