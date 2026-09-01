using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// CLONE ROUND 12 P1 — the new <see cref="SsbSurface"/> seam the write
/// campaign consumes in P2: the X12 lockout wrappers, the X13 zeroize wrapper
/// and its settle observables, the FM-squelch-cycle projection the
/// <c>AnalogSquelch</c> ordering depends on, and the FORCE WAKEUP session
/// latch bridge.
/// </summary>
public class Round12SsbSurfaceTests : SessionTestBase
{
    private SsbSurface Surface() => new(Radio);

    // ---- X12: the lockout wrappers --------------------------------------

    [Fact]
    public void RequestLockouts_SendsBothReportsAndItsSentinel_AndReturnsTheReadId()
    {
        var surface = Surface();
        ConnectReady();

        long id = surface.RequestLockouts();

        Assert.Equal(["PROGRAM", "SELECT", "BAT ST"], Transport.SentLines);
        Assert.True(id > 0);
        Assert.Equal(LockoutReadState.InFlight, surface.Lockouts.State);
    }

    [Fact]
    public void TheLockoutMirror_IsProjectedVerbatimFromCore_WithItsCompletionRecord()
    {
        var surface = Surface();
        ConnectReady();
        Assert.Equal(LockoutReadState.Unknown, surface.Lockouts.State);

        long id = surface.RequestLockouts();
        Transport.InjectLine(">>SSB_Programmable_Parameters");
        Transport.InjectLine("PROGRAM CHAN LOCK");
        AnswerSentinel();

        Assert.Equal(new AleReadCompletion(id, true), surface.LastLockoutRead);
        Assert.Equal(LockoutReadState.Completed, surface.Lockouts.State);
        var row = Assert.Single(surface.Lockouts.Rows);
        Assert.Equal(new LockoutRow(LockoutFamily.Program, LockoutSection.Ssb, "CHAN", LockState.Lock), row);
    }

    [Fact]
    public void SetLockout_SendsTheCapturedForm_AndTheSectionNeverReachesTheWire()
    {
        var surface = Surface();
        ConnectReady();

        surface.SetLockout(LockoutFamily.Program, LockoutSection.Eam, "CHGROUP", LockState.Unlock);

        var line = Assert.Single(Transport.SentLines);
        Assert.Equal("PROGRAM CHGROUP UNLOCK", line);
        Assert.DoesNotContain("EAM", line, StringComparison.Ordinal);
    }

    // ---- X13: zeroize ---------------------------------------------------

    [Fact]
    public void ZeroizeRadio_SendsZero_AndProjectsTheSettleObservables()
    {
        var surface = Surface();
        ConnectReady();
        Radio.ZeroizeSettlePollMs = 10_000;
        Radio.ZeroizeSettleTimeoutMs = 10_000;

        Assert.False(surface.IsZeroizeSettling);
        surface.ZeroizeRadio();

        Assert.Equal(["ZERO"], Transport.SentLines);
        Assert.True(surface.IsZeroizeSettling);
        Assert.False(surface.ZeroizeSettled);

        // In the CAPTURED order: the wipe's own banner opens the settle window,
        // and the prompt that follows it closes them (corrected 2026-08-19 —
        // prompts BEFORE the banner are the tail of whatever preceded the wipe;
        // see the ALE-context capture in CommandSurfaceTests).
        Transport.InjectLine("*** ZEROIZING RAM -- PLEASE WAIT ***");
        Transport.InjectLine("SSB>");

        Assert.True(surface.ZeroizeSettled);
        Assert.False(surface.IsZeroizeSettling);
        Assert.False(surface.ZeroizeFaulted);
    }

    // ---- §3 leg 6: the FM-squelch cycle projection ----------------------

    [Fact]
    public void IsFmSquelchCyclePending_TracksCoresCompensation_AndRaisesItsOwnEvent()
    {
        // The campaign writes AnalogSquelch only once this is FALSE — the cycle
        // would otherwise overwrite exactly what was just written. A flag with
        // no change event would leave the campaign polling or waiting forever,
        // which is why the event is part of the contract.
        var surface = Surface();
        ConnectReady();
        int raised = 0;
        surface.FmSquelchCyclePendingChanged += (_, _) => raised++;

        Transport.InjectLine("SSB>");
        Transport.InjectLine("SQUELCH ON");           // the arming precondition
        Assert.False(surface.IsFmSquelchCyclePending);

        Transport.InjectLine("FMDEV 8.0");            // an FM-property report arms it
        Assert.True(surface.IsFmSquelchCyclePending);
        Assert.Equal(1, raised);

        // The cycle completes on the modulation report and its OFF answer.
        Transport.InjectLine("MODE USB");
        Transport.InjectLine("SQUELCH OFF");
        Assert.False(surface.IsFmSquelchCyclePending);
        Assert.Equal(2, raised);
    }

    // ---- §9 C3: the FORCE WAKEUP bridge ---------------------------------

    [Fact]
    public void ForceWakeup_IsProjected_AndTheSurfaceRaisesChangedForIt()
    {
        // The bridge P3's highlight consumes: before round 12 FORCE_W was in
        // NEITHER the surface's watched set nor its read list, so the pane
        // could never highlight Enable even after the radio said it was on.
        var surface = Surface();
        ConnectReady();
        int changed = 0;
        surface.Changed += (_, _) => changed++;

        Assert.False(surface.ForceWakeup.IsConfirmed);

        Transport.InjectLine("FORCE WAKEUP ENABLED");

        Assert.True(surface.ForceWakeup.IsConfirmed);
        Assert.Equal(EnabledDisabled.Enabled, surface.ForceWakeup.Value);
        Assert.True(changed > 0);
    }

    [Fact]
    public void RequestSettings_IncludesTheCompressionRead_AndStillOmitsForceW()
    {
        // The EIGHTEENTH read (§9 B3 PRIMARY). FORCE_W stays out for its own
        // reason — a bare query answers NOTHING — and RWAS_KEY for its own
        // (a bare query answers ** ERROR **); round 12 changed what happens
        // when the radio VOLUNTEERS its ENABLED line, not whether it can be
        // polled.
        var surface = Surface();
        ConnectReady();

        surface.RequestSettings();

        Assert.Contains("COM", Transport.SentLines);
        Assert.DoesNotContain("FORCE_W", Transport.SentLines);
        Assert.DoesNotContain("RWAS_KEY", Transport.SentLines);
    }
}
