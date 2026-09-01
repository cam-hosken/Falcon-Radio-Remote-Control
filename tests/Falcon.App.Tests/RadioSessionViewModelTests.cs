using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

public class RadioSessionViewModelTests : SessionTestBase
{
    private readonly TestTime _time = new();

    private RadioSessionViewModel Vm()
        => new(Session, new ConsoleFeed(Radio, Session), _time);

    [Fact]
    public void PhaseAndPort_TrackTheSession()
    {
        var vm = Vm();
        Assert.Equal("Disconnected", vm.PhaseText);
        Assert.Equal("no port", vm.PortDisplay);
        Assert.False(vm.IsReady);

        Session.Connect(TestSettings);
        Assert.Equal(SessionPhase.Connecting, vm.Phase);
        Assert.Equal("COM7 9600", vm.PortDisplay);

        AnswerSentinel();
        Assert.Equal("Ready", vm.PhaseText);
        Assert.True(vm.IsReady);
        AnswerSentinel();

        Session.Close();
        Assert.Equal("Disconnected", vm.PhaseText);
        // Stage 8 (deferred-ledger fix): no stale port next to a grey
        // Disconnected dot — the session is not attached to anything.
        Assert.Equal("no port", vm.PortDisplay);
    }

    [Fact]
    public void PortDisplay_SurvivesFailedAndReconnecting_ClearsOnClose()
    {
        var vm = Vm();
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();
        Assert.Equal("COM7 9600", vm.PortDisplay);

        // Unexpected disconnect (auto-reconnect enabled above): the port is
        // exactly the fact the operator needs while Reconnecting.
        Transport.InjectError(new IOException("USB yanked"));
        Assert.Equal(SessionPhase.Reconnecting, vm.Phase);
        Assert.Equal("COM7 9600", vm.PortDisplay);

        Session.Close();
        Assert.Equal("no port", vm.PortDisplay);
    }

    /// <summary>
    /// F3 (plan-clone-field-round2.md, decision A-5) — the operator-facing half
    /// of the fix: NEITHER zeroize banner toasts.
    ///
    /// <para>Field report, 2026-08-21 item 3: "the Operate screen showed an
    /// ERROR toast saying zeroize complete" — during a wipe the operator had
    /// just authorised, on a page whose only error channel is this one. The
    /// suppression is made in Core (the banners raise no <c>ErrorOccurred</c>);
    /// this pins the CONSEQUENCE, because the toast is what the operator
    /// actually saw and the seam between them is three components long.</para>
    /// </summary>
    [Fact]
    public void ZeroizeBanners_RaiseNoToast_WhileARejectStillDoes()
    {
        var vm = Vm();
        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);

        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Assert.Equal("", vm.ToastText);
        Transport.InjectLine("*** ZEROIZE COMPLETE ***");
        Assert.Equal("", vm.ToastText);
        Assert.False(vm.HasToast);

        // ANTI-VACUITY: this VM does toast, on this feed, at this instant — so
        // the silence above is the banners' and not the fixture's.
        Transport.InjectLine("** ERROR **");
        Assert.Contains("rejected", vm.ToastText, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorToast_RateLimited_OnePerTwoSeconds_WithSuppressedCount()
    {
        var vm = Vm();
        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);

        // Three rejects in the same instant: one toast, two suppressed.
        Transport.InjectLine("** ERROR **");
        var firstToast = vm.ToastText;
        Transport.InjectLine("** ERROR **");
        Transport.InjectLine("** ERROR **");

        Assert.Contains("rejected", firstToast);
        Assert.Equal(firstToast, vm.ToastText);   // unchanged within the window

        // Past the 2 s window: the next error shows, carrying the count.
        _time.Now = _time.Now.AddSeconds(2.5);
        Transport.InjectLine("** ERROR **");
        Assert.Contains("+2 suppressed", vm.ToastText);
    }

    [Fact]
    public void Toast_Clears_WhenReconnectSucceeds()
    {
        var vm = Vm();
        Session.AutoReconnectEnabled = true;   // dormant by default (G1)
        ConnectReady();

        // Unexpected disconnect: the connection-lost toast shows.
        Transport.InjectError(new IOException("USB yanked"));
        Assert.NotEqual("", vm.ToastText);

        // Auto-reconnect succeeds → Ready: no stale red text next to a
        // green Ready dot (F1, audit round 1).
        Session.ReconnectTick();
        AnswerSentinel();
        Assert.Equal(SessionPhase.Ready, vm.Phase);
        Assert.Equal("", vm.ToastText);
        AnswerSentinel();
    }

    [Fact]
    public void Toast_Clears_OnUserClose()
    {
        var vm = Vm();
        ConnectReady();

        Transport.InjectError(new IOException("drop"));
        Assert.NotEqual("", vm.ToastText);

        Session.Close();
        Assert.Equal(SessionPhase.Disconnected, vm.Phase);
        Assert.Equal("", vm.ToastText);
    }

    // ---- round 13 C2: the dismiss control (backlog item 13) -----------------

    [Fact]
    public void TheLimiter_LetsThroughOneToastPerInterval_AndTailsTheRest()
    {
        // The limiter's own contract, pinned at THIS layer because round 13 D1
        // (the RX-only refusal) decided to OBEY it rather than bypass it — two
        // key edges seconds apart must both reach the operator, and a bounce
        // pair must not. Generic error entries, so this pin has no dependency
        // on D1 landing and the two phases stay parallel.
        var vm = Vm();
        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);

        Transport.InjectLine("** ERROR **");
        var first = vm.ToastText;
        Assert.NotEqual("", first);

        // SIX SECONDS later — well past the 2 s interval: a second toast, and
        // no suppressed tail, because nothing was suppressed between them.
        _time.Now = _time.Now.AddSeconds(6);
        Transport.InjectLine("** ERROR **");
        Assert.NotEqual("", vm.ToastText);
        Assert.DoesNotContain("suppressed", vm.ToastText, StringComparison.Ordinal);

        // …and INSIDE the interval: one toast, the rest counted onto the next.
        _time.Now = _time.Now.AddMilliseconds(30);
        Transport.InjectLine("** ERROR **");
        Assert.DoesNotContain("suppressed", vm.ToastText, StringComparison.Ordinal);

        _time.Now = _time.Now.AddSeconds(6);
        Transport.InjectLine("** ERROR **");
        Assert.Contains("(+1 suppressed)", vm.ToastText, StringComparison.Ordinal);
    }

    [Fact]
    public void HasToast_TracksTheText_SoTheDismissControlOnlyExistsWhenThereIsSomethingToClear()
    {
        var vm = Vm();
        Assert.False(vm.HasToast);

        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);
        Transport.InjectLine("** ERROR **");

        Assert.True(vm.HasToast);
        Assert.NotEqual("", vm.ToastText);

        // The property has to CHANGE-NOTIFY, or the button binds once and never
        // moves again — invisible forever, or worse, a ✕ beside an empty line.
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        vm.DismissToastCommand.Execute(null);

        Assert.False(vm.HasToast);
        Assert.Contains(nameof(RadioSessionViewModel.HasToast), seen);
    }

    [Fact]
    public void Dismiss_ClearsTheTextAndTheSuppressedCount()
    {
        var vm = Vm();
        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);

        // One toast plus two suppressed behind it.
        Transport.InjectLine("** ERROR **");
        Transport.InjectLine("** ERROR **");
        Transport.InjectLine("** ERROR **");
        Assert.NotEqual("", vm.ToastText);

        vm.DismissToastCommand.Execute(null);
        Assert.Equal("", vm.ToastText);

        // The COUNT went with it. A "(+N suppressed)" tail annotates a toast,
        // and the operator has just said they read the toast — carrying the
        // count onto an unrelated later error would attribute suppressions to
        // a message they were never behind. Proven from the outside: the next
        // error must arrive CLEAN.
        _time.Now = _time.Now.AddSeconds(6);
        Transport.InjectLine("** ERROR **");
        Assert.NotEqual("", vm.ToastText);
        Assert.DoesNotContain("suppressed", vm.ToastText, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshError_WithinTwoSecondsOfADismissal_ShowsImmediately()
    {
        // THE point of the phase. Dismissing is "I have read this" — it must
        // not also mean "and I will not be told about the next one for two
        // seconds". Without the clock wind-back this is precisely what the ✕
        // would do: swallow the operator's next failure.
        var vm = Vm();
        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);

        Transport.InjectLine("** ERROR **");
        Assert.NotEqual("", vm.ToastText);

        vm.DismissToastCommand.Execute(null);
        Assert.Equal("", vm.ToastText);

        // 200 ms later — deep inside the 2 s window that would otherwise
        // suppress it.
        _time.Now = _time.Now.AddMilliseconds(200);
        Transport.InjectLine("** ERROR **");

        Assert.NotEqual("", vm.ToastText);
        Assert.DoesNotContain("suppressed", vm.ToastText, StringComparison.Ordinal);
    }

    [Fact]
    public void Dismiss_DoesNotDisarmTheLimiterForEverythingAfterIt()
    {
        // The other side of the wind-back, and the reason it is a wind-back
        // rather than a reset-to-never: one error gets through immediately
        // after a dismissal, and the limiter then resumes normally. A ✕ that
        // permanently disarmed rate limiting would turn a burst back into the
        // flood the limiter exists to prevent.
        var vm = Vm();
        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);

        Transport.InjectLine("** ERROR **");
        vm.DismissToastCommand.Execute(null);

        _time.Now = _time.Now.AddMilliseconds(100);
        Transport.InjectLine("** ERROR **");            // shows (wind-back)
        var afterDismiss = vm.ToastText;
        Assert.NotEqual("", afterDismiss);

        _time.Now = _time.Now.AddMilliseconds(100);
        Transport.InjectLine("** ERROR **");            // suppressed again
        Assert.Equal(afterDismiss, vm.ToastText);

        _time.Now = _time.Now.AddSeconds(6);
        Transport.InjectLine("** ERROR **");
        Assert.Contains("(+1 suppressed)", vm.ToastText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuppressedTail_IsUnaffectedByAnUndismissedToast()
    {
        // The control for the two dismiss facts above: the SAME sequence
        // without the ✕ press must still carry its tail. Otherwise "the count
        // cleared" could be true because the count never accumulated.
        var vm = Vm();
        ConnectReady();
        _time.Now = DateTimeOffset.UnixEpoch.AddMinutes(1);

        Transport.InjectLine("** ERROR **");
        Transport.InjectLine("** ERROR **");
        Transport.InjectLine("** ERROR **");

        _time.Now = _time.Now.AddSeconds(6);
        Transport.InjectLine("** ERROR **");
        Assert.Contains("(+2 suppressed)", vm.ToastText, StringComparison.Ordinal);
    }
}
