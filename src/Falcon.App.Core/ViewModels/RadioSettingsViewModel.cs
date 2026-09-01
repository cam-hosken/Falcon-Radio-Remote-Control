using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// Radio settings page (GUI rejigger E4): two plain sub-tabs with NO mode
/// semantics — <b>Settings</b> (the mode-free-on-the-wire set; Wave-2
/// content, placeholder for now) and <b>Console</b> (the existing Console
/// page relocated whole; its content binds ConsoleViewModel directly).
/// The tab selection is pure app-side view state — sends nothing.
/// </summary>
public partial class RadioSettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool isConsoleOpen;

    [RelayCommand]
    private void OpenSettings() => IsConsoleOpen = false;

    [RelayCommand]
    private void OpenConsole() => IsConsoleOpen = true;
}
