using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views;

/// <summary>
/// Code-behind exists ONLY to route platform input to the VM (the old
/// VfoKnob keyboard contract, plan §4.2): while the VFO is armed, Up/Down
/// edit the pointed cursor digit, Left/Right move the cursor, Esc disarms,
/// and the keys are consumed (the WinForms IsInputKey equivalent). Disarm-on-focus-
/// loss maps to window deactivation + navigation away — MAUI has no
/// per-control PreviewKeyDown, so arming is page-scoped (documented in
/// docs/ui.md). All decisions live in <see cref="VfoViewModel"/>; this file
/// only translates events.
/// </summary>
public partial class OperatePage : ContentPage
{
    private readonly OperateViewModel _vm;

    public OperatePage(OperateViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _vm = viewModel;
#if WINDOWS
        Loaded += OnLoadedWindows;
        Unloaded += OnUnloadedWindows;
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Ssb.Vfo.Disarm();   // auto-disarm when the page goes away
    }

#if WINDOWS
    private Microsoft.UI.Xaml.FrameworkElement? _root;
    private Window? _mauiWindow;

    private void OnLoadedWindows(object? sender, EventArgs e)
    {
        _root = Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        if (_root is not null)
        {
            // PreviewKeyDown sees the keys before any focused child — the
            // closest WinUI analog to the old per-control PreviewKeyDown +
            // IsInputKey pattern.
            _root.PreviewKeyDown += OnPreviewKeyDown;
        }
        _mauiWindow = Window;
        if (_mauiWindow is not null)
            _mauiWindow.Deactivated += OnWindowDeactivated;
    }

    private void OnUnloadedWindows(object? sender, EventArgs e)
    {
        if (_root is not null)
        {
            _root.PreviewKeyDown -= OnPreviewKeyDown;
            _root = null;
        }
        if (_mauiWindow is not null)
        {
            _mauiWindow.Deactivated -= OnWindowDeactivated;
            _mauiWindow = null;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
        => _vm.Ssb.Vfo.Disarm();    // focus left the app — auto-disarm

    private void OnPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var vfo = _vm.Ssb.Vfo;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Up:
                e.Handled = vfo.HandleKey(VfoKey.Up);
                break;
            case Windows.System.VirtualKey.Down:
                e.Handled = vfo.HandleKey(VfoKey.Down);
                break;
            case Windows.System.VirtualKey.Left:
                e.Handled = vfo.HandleKey(VfoKey.Left);
                break;
            case Windows.System.VirtualKey.Right:
                e.Handled = vfo.HandleKey(VfoKey.Right);
                break;
            case Windows.System.VirtualKey.Escape:
                if (vfo.IsVfoArmed)
                {
                    vfo.Disarm();
                    e.Handled = true;
                }
                break;
        }
    }
#endif
}
