namespace Falcon.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    /// <summary>
    /// Takes IServiceProvider instead of AppShell directly (SendIt pattern) so
    /// InitializeComponent() merges application resources before any page is
    /// constructed.
    /// </summary>
    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();

        // Bench hook (Stage 8): the launch check screenshots both themes
        // headlessly â€” FALCON_UI_THEME=Dark|Light forces the theme for that
        // run. Unset (the normal case) the app follows the OS theme.
        var forced = Environment.GetEnvironmentVariable("FALCON_UI_THEME");
        if (string.Equals(forced, "Dark", StringComparison.OrdinalIgnoreCase))
            UserAppTheme = AppTheme.Dark;
        else if (string.Equals(forced, "Light", StringComparison.OrdinalIgnoreCase))
            UserAppTheme = AppTheme.Light;
    }

    /// <summary>
    /// UI tweaks round 11 Â§9 â€” the Windows window's ONE width, in dp.
    /// <b>LAYOUT-PROVISIONAL</b> (plan Â§1/invariant 7): a MEASURED change moves
    /// this constant and every pin derived from it in the same commit.
    ///
    /// <para><b>Why 548 (OWNER 2026-08-23; was 540).</b> Two floors, and the
    /// width is the higher of them. (1) The ALE station row: a six-column
    /// line whose fixed columns take 364 dp before the station name gets a
    /// pixel, plus Â§4's minimum readable name width of 120 â€” the page needs
    /// â‰¥ 484 dp of CONTENT, and content is the window less page padding (32)
    /// and scrollbar (16): 548 âˆ’ 48 = 500 â‰¥ 484. StyleVocabularyGuardTests
    /// evaluates that inequality against this constant. (2) The TOP TAB
    /// STRIP: at 540 the four tab labels did not fit beside the title-view
    /// About block, so WinUI pushed one into a "More" overflow (the owner:
    /// "connection settings lands in a triple dot menu"). MEASURED by UIA on
    /// the dev box, 100% scale, 2026-08-23: 540 overflows, 548 renders all
    /// four tabs with no overflow button â€” 548 is the measured just-enough
    /// width, and it matches Â§BA's old "~548" no-clip note independently.</para>
    ///
    /// <para><b>Why fixed at all.</b> Every width in the display constitution
    /// is a fixed dp sized to its widest content â€” a resizable desktop window
    /// re-opens a responsive-layout question the phone budget already answers.
    /// Fixing the width also closes the owner's "the connection settings don't
    /// resize" observation BY CONSTRUCTION: nothing resizes.</para>
    /// </summary>
    public const double WindowFixedWidth = 548;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<AppShell>());
#if WINDOWS
        // WIDTH ONLY. Height is deliberately free: the panes scroll, and a
        // fixed height would fight the title bar and the taskbar on every
        // display the owner uses.
        window.Width = WindowFixedWidth;
        window.MinimumWidth = WindowFixedWidth;
        window.MaximumWidth = WindowFixedWidth;
#endif
        return window;
    }
}
