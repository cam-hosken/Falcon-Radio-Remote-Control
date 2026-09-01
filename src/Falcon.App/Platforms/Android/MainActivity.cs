using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Falcon.App.Core.Session;
using Falcon.App.Platforms.Android;

namespace Falcon.App;

// SingleTask launch mode from the start (plan §2.5, SendIt-proven): the Stage 7
// foreground service keeps the process alive after the activity is destroyed;
// SingleTask reuses the single activity instance on relaunch (under SingleTop,
// Android can build a second activity against the surviving process and MAUI
// crashes loading images for the destroyed one — dotnet/maui #17915).
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int RequestCodePostNotifications = 0x0138;

    private RadioSession? _session;
    private ForegroundLinkPolicy? _policy;

    /// <summary>
    /// Stage 7 wiring — the foreground service follows the session phase
    /// via <see cref="ForegroundLinkPolicy"/> (the rules are unit-tested in
    /// Falcon.App.Tests; this class only executes the returned actions).
    ///
    /// <para>Why resolving here is safe (the SendIt startup-gate lesson
    /// does NOT recur): SendIt's OnCreate deadlock came from resolving a
    /// singleton whose constructor chain performed the first (blocking,
    /// prompting) database open on the main thread. Falcon has no database
    /// and no settings store — the RadioSession chain (session → radio →
    /// transport → AndroidUsbSerialPort) is allocation, event subscription
    /// and one USB broadcast-receiver registration; nothing blocks, nothing
    /// prompts. Resolving on the UI thread here is also exactly what Q10
    /// wants: Prc138Radio/RadioSession capture this thread's
    /// SynchronizationContext at construction.</para>
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // POST_NOTIFICATIONS is a runtime permission on Android 13+; the
        // FGS runs without it but its notification is suppressed and the
        // service may be downgraded. One bundled prompt at startup (only
        // permission we ask for — USB permission is per-device, system-owned).
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
                ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.PostNotifications)
                    != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(
                    this,
                    [global::Android.Manifest.Permission.PostNotifications],
                    RequestCodePostNotifications);
            }
        }
        catch
        {
            // Best-effort — denial degrades the notification, never crashes.
        }

        try
        {
            var services = IPlatformApplication.Current?.Services;
            _session = services?.GetService(typeof(RadioSession)) as RadioSession;
            _policy = services?.GetService(typeof(ForegroundLinkPolicy)) as ForegroundLinkPolicy;
            if (_session is not null)
            {
                _session.PhaseChanged += OnSessionPhaseChanged;
                // Catch-up: the policy is a singleton but the subscription is
                // per-activity, so a phase change during an activity-less gap
                // (relaunch after the old instance was destroyed) would be
                // missed. Reconciling against the current phase here closes
                // that gap (e.g. stops an orphaned service).
                OnSessionPhaseChanged(this, EventArgs.Empty);
            }
        }
        catch
        {
            // DI not up (early-boot race) — the app still functions; the
            // link just won't be FGS-protected this process.
            _session = null;
            _policy = null;
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Now TOP — safe to start an FGS. Completes any start the policy
        // deferred because Ready landed while we were backgrounded
        // (Android 12+/14 forbids FGS starts from the background).
        Apply(_policy?.OnActivityForegroundChanged(true));
    }

    protected override void OnPause()
    {
        Apply(_policy?.OnActivityForegroundChanged(false));
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (_session is not null)
            _session.PhaseChanged -= OnSessionPhaseChanged;
        base.OnDestroy();
    }

    private void OnSessionPhaseChanged(object? sender, EventArgs e)
    {
        // Marshalled per Q10 — this runs on the UI thread, like the
        // lifecycle callbacks, so the policy needs no locking.
        if (_session is not null)
            Apply(_policy?.OnPhaseChanged(_session.Phase));
    }

    private void Apply(LinkServiceAction? action)
    {
        switch (action)
        {
            case LinkServiceAction.Start:
                try
                {
                    var intent = new Intent(this, typeof(RadioForegroundService));
                    if (OperatingSystem.IsAndroidVersionAtLeast(26))
                        StartForegroundService(intent);
                    else
                        StartService(intent);
                }
                catch
                {
                    // Best-effort: the app still functions without the FGS;
                    // just no background-survival guarantee for the link.
                }
                break;

            case LinkServiceAction.Stop:
                try { StopService(new Intent(this, typeof(RadioForegroundService))); }
                catch { }
                break;
        }
    }
}
