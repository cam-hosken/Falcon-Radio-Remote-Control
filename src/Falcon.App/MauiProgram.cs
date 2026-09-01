using Falcon.App.Controls;
using Falcon.App.Core.Cloning;
using Falcon.App.Core.Demo;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.App.Services;
using Falcon.App.Views;
using Falcon.Core.Radio;
using Falcon.Core.Transport;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

#if ANDROID
using Falcon.App.Platforms.Android;
#endif

namespace Falcon.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // D13 (plan-clone-write-structural, 2026-08-30): the toolkit's
        // initializer, which is what registers `FileSaver.Default` — the SAVE
        // LOCATION PICKER the Cloning card's "Store file…" press opens. MAUI
        // itself ships only an OPEN picker. Nothing else in this app uses the
        // toolkit; the call is a one-line registration, not an opt-in to its
        // controls.
        builder.UseMauiCommunityToolkit();

        // D18(b) (plan-clone-write-structural, 2026-08-30): the console log's
        // text becomes natively selectable, through each platform's own
        // selectable-text switch. SCOPED BY TYPE inside the handler mapping:
        // only ConsoleLogLabel is touched, so no other label in the app changes
        // its tap-and-hold behaviour. See ConsoleLogLabel for the whole
        // argument — including why the scope is a subclass and not a style.
        ConsoleLogLabel.EnableTextSelection();

#if ANDROID
        // N6 (GUI rejigger): bottom-tab labels sit too low, crowding the
        // system navigation buttons — the custom renderer nudges the item
        // content toward the top of the bar (styling only).
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler(typeof(AppShell), typeof(FalconShellRenderer)));
#endif

        // -------------------------------------------------------------------
        // Transport (platform-specific serial port)
        //
        // SendIt DI pattern: the byte-level ISerialPort seam gets a per-platform
        // implementation; everything above it is shared. Android (Stage 7):
        // AndroidUsbSerialPort over the vendored UsbSerialForAndroid.Net fork,
        // VID:PID[:Serial] identifiers (UsbPortIdentifier in Falcon.Core).
        //
        // DemoCapableSerialPort (plan-demo-radio.md) adds the "DEMO" port —
        // a minimal canned responder for radio-less GUI exploration; every
        // real port name still routes to the platform implementation.
        // -------------------------------------------------------------------
#if WINDOWS
        builder.Services.AddSingleton<ISerialPort>(_ => new DemoCapableSerialPort(new WindowsSerialPort()));
#elif ANDROID
        builder.Services.AddSingleton<ISerialPort>(_ => new DemoCapableSerialPort(new AndroidUsbSerialPort()));
        // Foreground-service start/stop rules (Stage 7): a singleton so its
        // believed-running state survives MainActivity recreation (SingleTask
        // activities come and go while process + service live on).
        builder.Services.AddSingleton<ForegroundLinkPolicy>();
#endif
        builder.Services.AddSingleton<ITransport>(sp => new SerialTransport(sp.GetRequiredService<ISerialPort>()));

        // -------------------------------------------------------------------
        // THE CAMPAIGN SIGNAL, REGISTERED FIRST
        // (plan-clone-write-structural.md §5.2, decision D1).
        //
        // Dependency-free, and registered ahead of everything that consumes it
        // for the reason the plan gives: the coupler policy and the thirteen
        // producer ViewModels take ICampaignSignal, and the coupler policy is
        // required by the surfaces CloneService requires. A producer taking
        // CloneService would close that ring into a DI cycle; a standalone
        // coordinator does not. Both the concrete type and the interface are
        // bound to the SAME instance — CloneService needs Enter(), everyone
        // else needs only the signal — and both bindings are pinned by
        // DiRegistrationGuardTests.
        // -------------------------------------------------------------------
        builder.Services.AddSingleton<CampaignWireCoordinator>();
        builder.Services.AddSingleton<ICampaignSignal>(
            sp => sp.GetRequiredService<CampaignWireCoordinator>());

        // Prc138Radio captures SynchronizationContext.Current at construction
        // (Q10). Singletons resolve lazily on the MAUI main thread (first page
        // construction), so the captured context is the UI dispatcher's.
        builder.Services.AddSingleton<Prc138Radio>(sp => new Prc138Radio(sp.GetRequiredService<ITransport>()));

        // Session layer (Stage 3): lifecycle + auto-reconnect + TransportError-fatal.
        builder.Services.AddSingleton<RadioSession>(sp => new RadioSession(
            sp.GetRequiredService<Prc138Radio>(),
            sp.GetRequiredService<ITransport>()));

        // ROUND 14 C — the internal-coupler CONVERGENCE policy
        // (plan/plan-round14.md §4-C, owner rulings R3/R10). SESSION-SCOPED
        // state, so the SAME singleton lifetime as the surfaces that consume
        // it; it subscribes RadioSession.PhaseChanged and the radio's
        // InternalCoupler property, and issues the once-per-session seeding
        // `INTCOUPLER` read.
        builder.Services.AddSingleton<CouplerPolicy>();

        // Q9 per-domain surfaces — the only radio access ViewModels get.
        //
        // ModeSurface and HopSurface take the coupler policy EXPLICITLY here
        // rather than leaving it to DI's optional-parameter resolution: the
        // policy is a *behaviour* the app's composition switches on, and the
        // two clone-safe raw intents (`ModeSurface.Select`,
        // `HopSurface.SelectNet`) look identical whether it is present or not.
        // Naming it at the registration is the one place that is visible — and
        // it is what CouplerPolicyTests' composition pin reads, so a factory
        // that quietly stopped passing it fails a test instead of shipping a
        // dead feature.
        builder.Services.AddSingleton<ModeSurface>(sp => new ModeSurface(
            sp.GetRequiredService<Prc138Radio>(),
            sp.GetRequiredService<CouplerPolicy>()));
        builder.Services.AddSingleton<PowerSurface>();
        builder.Services.AddSingleton<StatusSurface>();
        builder.Services.AddSingleton<ConsoleFeed>();
        builder.Services.AddSingleton<SsbSurface>();
        builder.Services.AddSingleton<ChannelSurface>();
        builder.Services.AddSingleton<CouplerSurface>();
        builder.Services.AddSingleton<HopSurface>(sp => new HopSurface(
            sp.GetRequiredService<Prc138Radio>(),
            sp.GetRequiredService<CouplerPolicy>()));
        builder.Services.AddSingleton<AleSurface>();
        builder.Services.AddSingleton<PortSurface>();
        // Round 8 (ED): the cross-mode modem slice (the PowerSurface shape).
        builder.Services.AddSingleton<ModemSurface>();

        // ---- Wave 2 (ALE/HOP/device settings panes) — added block ----------
        // DeviceSurface: the mode-free device set (LIG/INT/CONT/BAT ST/TI) for
        // the Radio settings → Settings sub-tab. AleSurface already carries the
        // nine ALE settings; no new HOP surface (the HOP settings pane is empty
        // by construction — net programming + hop crypto are GUI-out).
        builder.Services.AddSingleton<DeviceSurface>();
        // --------------------------------------------------------------------

        // Stage 11: the guarded baud wizard's engine — a session-layer
        // component (owns the radio handle like RadioSession does); the
        // Settings wizard drives it through RadioPortViewModel only.
        builder.Services.AddSingleton<BaudChangeFlow>();

        // Round 11 §9A: the radio-cloning orchestrator — a session-layer
        // component on the BaudChangeFlow precedent (it owns the radio handle
        // for its sentinels and mode switches, and sequences the existing
        // surfaces for everything else). Pinned by DiRegistrationGuardTests,
        // which cannot see it through the derived set: CloneViewModel takes it
        // by CONSTRUCTOR, so nothing calls GetService for it.
        builder.Services.AddSingleton<CloneService>();

        builder.Services.AddSingleton(TimeProvider.System);

        // UI tweaks round 10 (§5): the GUI-owned confirmation seam. The
        // interface lives in the MAUI-free Falcon.App.Core so ViewModels can
        // take it without referencing a page; the MAUI alert implementation
        // lives here in the app head. Pinned by DiRegistrationGuardTests as an
        // EXACT interface→implementation source pin (the derived-set guard
        // only sees explicit GetService calls, and a constructor-injected
        // service has none).
        builder.Services.AddSingleton<IConfirmationPrompt, ConfirmationPrompt>();

        // CLONE ROUND 12 §6 F3: the navigation seam, on exactly the same
        // pattern — the MAUI-free interface in App.Core, the Shell
        // implementation here. Both this binding and the coordinator below are
        // pinned EXPLICITLY in DiRegistrationGuardTests: neither is visible to
        // the derived-set guard (the navigator is constructor-injected, and
        // the coordinator is resolved by AppShell's constructor), so deleting
        // either registration would leave every derived pin green while the
        // shell failed to construct.
        builder.Services.AddSingleton<INavigator, ShellNavigator>();
        // The phase→screen coordinator. A singleton because it must subscribe
        // exactly once; EAGERLY resolved in AppShell's constructor, since
        // nothing else asks for it.
        builder.Services.AddSingleton<SessionNavigationCoordinator>();

        // ROUND 14 G (R18): the settings seam, on the same pattern again — the
        // MAUI-free interface in App.Core, the Preferences implementation
        // here. One key today (the operator's chosen serial port), which is
        // the whole reason the app stops forgetting COM20 between launches.
        builder.Services.AddSingleton<ISettingsStore, PreferencesSettingsStore>();

        // ViewModels — singletons: they subscribe to singleton events in their
        // constructors (a transient VM would leak one subscription per
        // navigation) and hold cross-page state (console scrollback, session
        // status).
        builder.Services.AddSingleton<RadioSessionViewModel>();
        builder.Services.AddSingleton<ModeViewModel>();
        builder.Services.AddSingleton<PowerViewModel>();
        builder.Services.AddSingleton<SpineStatusViewModel>();
        builder.Services.AddSingleton<VfoViewModel>();
        builder.Services.AddSingleton<SignalViewModel>();
        // Round 8 (ED): ONE modem picker VM behind all three panes' rows.
        builder.Services.AddSingleton<ModemViewModel>();
        // Round 8 (EE): the Radio-settings modem preset editor.
        builder.Services.AddSingleton<ModemPresetsViewModel>();
        builder.Services.AddSingleton<ChannelViewModel>();
        builder.Services.AddSingleton<CouplerViewModel>();
        builder.Services.AddSingleton<SsbViewModel>();
        // Wave 2 (SSB): mode-settings pane content VM. Round 14 C names the
        // coupler policy explicitly — same reason as the two surfaces above.
        builder.Services.AddSingleton<SsbSettingsViewModel>(sp => new SsbSettingsViewModel(
            sp.GetRequiredService<SsbSurface>(),
            sp.GetRequiredService<RadioSession>(),
            sp.GetRequiredService<CouplerPolicy>()));
        builder.Services.AddSingleton<HopViewModel>();
        builder.Services.AddSingleton<MessagesViewModel>();
        builder.Services.AddSingleton<LqaViewModel>();
        // GUI rejigger N1 (Wave-1 coordination ruling): Messages/LQA fold
        // into the ALE pane and their shell pages are gone, so AleViewModel's
        // navigation delegate is a NO-OP — the row actions preselect in-pane
        // instead. The vestigial delegate parameter is removed in Wave 2.
        builder.Services.AddSingleton<AleViewModel>(sp => new AleViewModel(
            sp.GetRequiredService<AleSurface>(),
            sp.GetRequiredService<RadioSession>(),
            sp.GetRequiredService<MessagesViewModel>(),
            sp.GetRequiredService<LqaViewModel>(),
            _ => { }));
        builder.Services.AddSingleton<OperateViewModel>();
        builder.Services.AddSingleton<ConsoleViewModel>();
        // ROUND 14 G (R18): the connection page REMEMBERS the operator's port,
        // so its VM is constructed explicitly around the settings seam. The
        // parameterless registration would also compile — the store is a
        // constructor dependency the container can fill — but this spelling is
        // what the composition pin can read, and a seam that silently went
        // missing is the exact class of defect Phase C's guard was built for.
        builder.Services.AddSingleton<ConnectionSettingsViewModel>(sp => new ConnectionSettingsViewModel(
            sp.GetRequiredService<RadioSession>(),
            sp.GetRequiredService<ISerialPort>(),
            sp.GetRequiredService<ISettingsStore>()));
        // N2: the Connect ⇄ Disconnect toggle. Round 12 §6 F2 moved its BUTTON
        // from the shell title bar onto the Connection settings page; the VM
        // and its registration are unchanged.
        builder.Services.AddSingleton<ConnectToggleViewModel>();
        // E4: Radio settings sub-tab state (Settings | Console).
        builder.Services.AddSingleton<RadioSettingsViewModel>();

        // ---- Wave 2 (ALE/HOP/device settings panes) — added block ----------
        // The ALE mode-settings pane VM and the Radio-settings Settings-tab
        // (device) VM. The ALE pane VM is resolved by AleSettingsPaneView's
        // code-behind (inner Body BindingContext); the device VM is
        // constructor-injected into RadioSettingsPage.
        builder.Services.AddSingleton<AleSettingsViewModel>();
        builder.Services.AddSingleton<DeviceSettingsViewModel>();
        // UI-tweaks round 3 (X1): the HOP settings pane is no longer a static
        // note — it is the net-programming editor, resolved the same way as
        // the ALE pane. Hop crypto stays GUI-out.
        // Round 14 C names the coupler policy explicitly — same reason as the
        // two surfaces above.
        builder.Services.AddSingleton<HopSettingsViewModel>(sp => new HopSettingsViewModel(
            sp.GetRequiredService<HopSurface>(),
            sp.GetRequiredService<RadioSession>(),
            sp.GetRequiredService<IConfirmationPrompt>(),
            sp.GetRequiredService<CouplerPolicy>()));
        // ALE programming (plan-ale-programming.md, scope amendment X8): the
        // two ALE-settings-pane programming cards. Self-binding views, so each
        // resolves its own singleton in its code-behind — DiRegistrationGuardTests
        // derives the requirement from those GetService calls.
        builder.Services.AddSingleton<AleProgrammingViewModel>();
        builder.Services.AddSingleton<AleScanGroupsViewModel>();
        builder.Services.AddSingleton<SsbChannelEditorViewModel>();   // round 4 (AK): the SSB channel editor
        // Round 11 §9A: the Radio-settings Cloning card, constructor-injected
        // into RadioSettingsPage like the device VM.
        builder.Services.AddSingleton<CloneViewModel>();
        // --------------------------------------------------------------------
        // G3: registration kept — the wizard UI is gone but the backend
        // (BaudChangeFlow + RadioPortViewModel + tests) stays (owner ruling).
        builder.Services.AddSingleton<RadioPortViewModel>();

        // Shell — singleton so there is exactly one navigation root. Pages are
        // transient; their singleton VMs carry the state.
        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddTransient<OperatePage>();
        builder.Services.AddTransient<ModeSettingsPage>();
        builder.Services.AddTransient<RadioSettingsPage>();
        builder.Services.AddTransient<SettingsPage>();
        // F6: About is a ROUTED page (Routing.RegisterRoute in AppShell), not
        // a tab — but it is resolved through the container like the others.
        builder.Services.AddTransient<AboutPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
