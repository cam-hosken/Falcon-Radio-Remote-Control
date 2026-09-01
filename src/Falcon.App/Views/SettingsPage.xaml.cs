using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(ConnectionSettingsViewModel viewModel, ConnectToggleViewModel connectToggle)
    {
        InitializeComponent();
        BindingContext = viewModel;

        // F2: the Connect ⇄ Disconnect toggle binds its own small VM — the
        // wiring that used to live in AppShell's code-behind, moved with the
        // button (the page's BindingContext is the connection settings).
        ConnectToggleButton.BindingContext = connectToggle;

        // F4: the port poll's two view-side facts.
        //
        // (1) The INTERACTION WINDOW. A timer that reconciles the Picker's
        //     ItemsSource while its dropdown is open is how a selection jumps
        //     under the operator's finger, so the VM holds scans back between
        //     these two events and applies the latest on unfocus.
        PortPicker.Focused += (_, _) => viewModel.BeginPortInteraction();
        PortPicker.Unfocused += (_, _) => viewModel.EndPortInteraction();

        // (2) The OPERATOR'S OWN PICK. The two-way SelectedItem binding cannot
        //     say who set it, and the difference decides whether a vanished
        //     port is re-targeted or left null — so the gesture gets its own
        //     call. This event is NOT a reliable gesture signal on its own:
        //     MAUI's Picker also raises it for the VM's own writes AND for its
        //     own index recalculation when the bound list gains or loses an
        //     item. The VIEW cannot separate those; the VM does, by attributing
        //     a pick only inside the interaction window opened above — which is
        //     why both handlers are wired or neither works.
        PortPicker.SelectedIndexChanged += (_, _) =>
            viewModel.SelectPortByUser(PortPicker.SelectedItem as string);
    }
}
