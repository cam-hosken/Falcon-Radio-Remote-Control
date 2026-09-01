using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Falcon.App.Core.Session;

namespace Falcon.App.Platforms.Android;

/// <summary>
/// Foreground service that keeps the process privileged and the CPU awake
/// while the radio link is up (Stage 7, plan §2.5). Adapted DOWN from
/// SendIt's RadioForegroundService: <b>ConnectedDevice type only</b> — no
/// media/mic/location, no audio routing, and exactly ONE notification action
/// (clone round 12 §6 F5's Exit — the availability bound is recorded below).
/// It owns exactly two things:
///
/// <list type="bullet">
///   <item>the foreground notification (so Android's cached-process
///         pressure can't kill the USB serial session while the app is
///         backgrounded / the screen is off), and</item>
///   <item>a PARTIAL_WAKE_LOCK for the lifetime of the link. The FGS alone
///         keeps the process alive but does NOT keep the CPU out of Doze —
///         and Doze stretches <c>System.Threading.Timer</c> ticks (the
///         session's 2 s reconnect poller, the radio's watchdogs) into
///         multi-minute windows once the screen goes off (SendIt lesson,
///         plan-radio-wake-lock).</item>
/// </list>
///
/// Lifecycle: started/stopped by MainActivity executing
/// <see cref="ForegroundLinkPolicy"/> decisions (Ready → start;
/// Disconnected/Failed → stop; runs THROUGH Reconnecting so the poller
/// stays awake). NotSticky: after a process death there is no session and
/// no auto-open on launch, so a sticky restart would resurrect an orphan
/// notification with no link behind it.
///
/// <para><b>AVAILABILITY BOUND of the Exit action (round 12 §6 F5,
/// recorded).</b> The notification exists only while the policy above keeps
/// this service running — Ready THROUGH Reconnecting. That is exactly the
/// window in which a backgrounded live link needs killing, and it is also why
/// Exit is not the app's general quit: with the session down there is no
/// notification, and closing the app normally is the gesture.</para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class RadioForegroundService : Service
{
    public const string ChannelId = "falcon_radio_link";
    public const string ChannelName = "Radio link";
    public const int NotificationId = 1381;

    /// <summary>
    /// CLONE ROUND 12 §6 F5 — the notification's Exit action (owner-ruled over
    /// an in-app control: a backgrounded live link is exactly when the tray is
    /// the only surface the operator has).
    ///
    /// <para>FULLY QUALIFIED and unique to this service. An Intent action is a
    /// process-wide string; a bare "EXIT" could collide with any library's
    /// broadcast, and the branch below decides whether the app dies.</para>
    /// </summary>
    public const string ExitAction = "com.falconrc.app.action.EXIT";

    /// <summary>The Exit PendingIntent's request code — distinct from the
    /// content intent's 0, so <c>UpdateCurrent</c> can never have the two
    /// rewrite each other into one intent.</summary>
    public const int ExitRequestCode = 1382;

    /// <summary>The Exit action's button word (R13: no radio token).</summary>
    public const string ExitActionLabel = "Exit";

    // "FalconRC:" prefix = the Android lint convention for app-attributed
    // wake locks; an untagged lock shows in battery histograms as
    // WakeLock_NoTag with no owner.
    private const string WakeLockTag = "FalconRC:RadioLink";

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();

        // Best-effort: some OEM builds throw on NewWakeLock under
        // hardening (SendIt field note). The link still works without it;
        // background timer cadence just degrades to Doze on those devices.
        try
        {
            var pm = (PowerManager?)GetSystemService(PowerService);
            var wl = pm?.NewWakeLock(WakeLockFlags.Partial, WakeLockTag);
            // Non-ref-counted so Acquire/Release pair cleanly even under a
            // hypothetical double-OnCreate (sticky-restart edge cases).
            wl?.SetReferenceCounted(false);
            wl?.Acquire();
            _wakeLock = wl;
        }
        catch
        {
            _wakeLock = null;
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // F5: the Exit action arrives as an ordinary START carrying our unique
        // action, and it MUST branch BEFORE the StartForeground below. The
        // command path promotes the service unconditionally; doing that first
        // and then tearing down would re-post the very notification the
        // operator just used to quit.
        if (intent?.Action == ExitAction)
        {
            TearDownAndExit();
            return StartCommandResult.NotSticky;
        }

        // StartForeground must run within ~5 s of OnStartCommand. On API
        // 34+ it can throw if the FGS-type contract isn't met; for the
        // ConnectedDevice type our only prerequisite is the (install-time)
        // FOREGROUND_SERVICE_CONNECTED_DEVICE permission, so this is
        // belt-and-braces — but an uncaught Java throw here is a process
        // fatal (SendIt field crash), so catch and stand down cleanly.
        try
        {
            StartForeground(NotificationId, BuildNotification());
            return StartCommandResult.NotSticky;
        }
        catch (Java.Lang.Exception)
        {
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }
    }

    public override void OnDestroy()
    {
        // Release first so the CPU can return to Doze promptly even if a
        // later teardown step throws. IsHeld guards the rare path where
        // Acquire silently failed in OnCreate.
        try
        {
            if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        }
        catch { /* best-effort */ }
        _wakeLock = null;
        base.OnDestroy();
    }

    /// <summary>
    /// The user swiped the app away from recents. The FGS keeps the PROCESS
    /// alive through that, and relaunching into a surviving process whose
    /// activity was destroyed is the known MAUI/Glide crash class
    /// (dotnet/maui #17915 — SendIt lesson; SingleTask alone doesn't cover
    /// task removal). A recents-swipe is treated as a full quit: close the
    /// session best-effort, drop the notification, kill the process so the
    /// next launch is clean. Home / screen-off do NOT call OnTaskRemoved,
    /// so the backgrounded link is unaffected.
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        base.OnTaskRemoved(rootIntent);
        TearDownAndExit();
    }

    /// <summary>
    /// The full-quit sequence, SHARED by the two gestures that mean it: the
    /// recents swipe (<see cref="OnTaskRemoved"/>) and the notification's Exit
    /// action (<see cref="OnStartCommand"/>). F5 extracted it rather than
    /// copying it — two teardown paths that drift apart is how one of them
    /// starts leaving the USB handle or the notification behind.
    ///
    /// <para>The order is load-bearing and unchanged from the recents-swipe
    /// implementation: close the session OFF the main thread, drop the
    /// notification, stop the service, then kill the process a beat later so
    /// StopSelf has dispatched.</para>
    /// </summary>
    private void TearDownAndExit()
    {
        // Best-effort session close off the main thread (Close touches the
        // port and could ANR); process death below releases the USB handle
        // regardless.
        try
        {
            var session = IPlatformApplication.Current?
                .Services.GetService(typeof(RadioSession)) as RadioSession;
            if (session is not null)
                _ = Task.Run(() => { try { session.Close(); } catch { } });
        }
        catch { /* tearing down anyway */ }

        try { StopForeground(StopForegroundFlags.Remove); } catch { }
        try { StopSelf(); } catch { }

        // Post a beat so StopSelf dispatches before the hard exit.
        new Handler(Looper.MainLooper!).PostDelayed(() =>
        {
            try { global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()); }
            catch { /* nothing left to do */ }
        }, 200);
    }

    private Notification BuildNotification()
    {
        // Tap target: resume MainActivity in place (SingleTop + NewTask
        // keeps the existing SingleTask instance and its Shell stack).
        var contentIntent = new Intent(this, typeof(MainActivity));
        contentIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask);
        var contentPi = PendingIntent.GetActivity(
            this, 0, contentIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // F5: the Exit action. GetService (not GetActivity/GetBroadcast) —
        // the work is this service's own teardown, so the intent is delivered
        // straight to OnStartCommand with no activity to route through and no
        // receiver to register. Immutable, like the content intent: nothing
        // outside this app has any business rewriting it.
        var exitIntent = new Intent(this, typeof(RadioForegroundService));
        exitIntent.SetAction(ExitAction);
        var exitPi = PendingIntent.GetService(
            this, ExitRequestCode, exitIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // Static text plus ONE action. A built-in small icon suffices for the
        // contract (SendIt note); an app-specific one is Stage 8 polish.
        // Un-chained calls: the binding's fluent returns are nullable-annotated.
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("FalconRC");
        builder.SetContentText("Radio link active");
        builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysDataBluetooth);
        builder.SetOngoing(true);
        builder.SetContentIntent(contentPi);
        // Icon 0 = no action icon; the notification style shows the word.
        builder.AddAction(0, ExitActionLabel, exitPi);
        // Build() is nullable-annotated in the binding but never returns
        // null for a channel-backed builder.
        return builder.Build()!;
    }

    private void EnsureNotificationChannel()
    {
        // OperatingSystem guard (not SdkInt) so CA1416 recognizes it.
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        if (nm is null) return;
        if (nm.GetNotificationChannel(ChannelId) is not null) return;

        // Low importance: a status indicator, not an alert — no sound, no
        // vibration, no badge.
        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low);
        channel.SetShowBadge(false);
        nm.CreateNotificationChannel(channel);
    }
}
