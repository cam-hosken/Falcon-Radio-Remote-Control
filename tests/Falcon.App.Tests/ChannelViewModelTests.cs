using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Tests;

/// <summary>
/// The Channel section (GUI rejigger F6/F7a; ROUND 15 N2): the typed 1–2
/// digit entry + Select that replaced the per-digit spinners, sending CH nn +
/// SH (the re-read) with no optimistic move; the RXONLY pair 00-gated
/// (channel-stored) with highlight only from the confirmed report; and the
/// programmatic-write-sends-nothing constitution pin.
/// </summary>
public class ChannelViewModelTests : SessionTestBase
{
    /// <summary>Verbatim captured DI line (session-23; docs/protocol.md).</summary>
    private const string DiFixture = "CH 00 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO";

    private ChannelViewModel Vm()
        => new(new ChannelSurface(Radio), new SsbSurface(Radio), Session);

    [Fact]
    public void DiDumpLine_ParsesIntoStoredChannel_FieldsVerbatim()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");

        Transport.InjectLine(DiFixture);
        Transport.InjectLine("CHAN 00");     // the dump's trailing current-channel line

        var entry = Assert.Single(vm.Channels);
        Assert.Equal(0, entry.Number);
        Assert.Equal("04123000", entry.RxFrequency);
        Assert.Equal("04123000", entry.TxFrequency);
        Assert.Equal("USB", entry.Mode);
        Assert.Equal("SL", entry.Agc);       // the dump's own abbreviation, verbatim
        Assert.Equal("2.7", entry.Bandwidth);
        Assert.Equal("NO", entry.RxOnly);

        Assert.Equal("CH 00", vm.CurrentChannelText);
        Assert.Empty(Transport.SentLines);   // programmatic writes send nothing
    }

    [Fact]
    public void UnreportedChannel_RendersDash_AndTheEntryIsStillLive()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Assert.Equal("CH —", vm.CurrentChannelText);
        Assert.Equal("—", vm.TensText);
        Assert.Equal("—", vm.UnitsText);

        // N2: the entry does NOT compute from the confirmed channel the way a
        // spin did, so an unreported channel is no reason to refuse a select —
        // typing a number is exactly how the operator finds out where they are.
        Assert.True(vm.SelectEnteredCommand.CanExecute(null));
        vm.ChannelInput = "7";
        vm.SelectEnteredCommand.Execute(null);
        Assert.Equal(["CH 7", "SH"], Transport.SentLines);
        Assert.Equal("CH —", vm.CurrentChannelText);     // no optimism
    }

    // ---- N2: the typed entry + Select ---------------------------------------

    [Fact]
    public void OneDigit_Selects_ThatChannel_AndTheDigitsMoveOnlyOnTheChanAnswer()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.ClearSent();

        vm.ChannelInput = "7";
        vm.SelectEnteredCommand.Execute(null);

        // "7" IS 07 — the radio's own CH form is unpadded (SsbController
        // .SelectChannel writes the integer), so the wire reads `CH 7`.
        Assert.Equal(["CH 7", "SH"], Transport.SentLines);
        Assert.Equal("", vm.InputError);
        Assert.False(vm.HasInputError);
        Assert.Equal("CH 00", vm.CurrentChannelText);    // no optimism
        Assert.Equal("0", vm.TensText);
        Assert.Equal("0", vm.UnitsText);

        // …and the buffer CLEARS on the selection (owner 2026-08-23; it used
        // to be kept, the RF-gain idiom — a refusal still keeps it, pinned in
        // the refusal theory below).
        Assert.Equal("", vm.ChannelInput);

        Transport.InjectLine("CHAN 07");
        Assert.Equal("CH 07", vm.CurrentChannelText);
        Assert.Equal("0", vm.TensText);
        Assert.Equal("7", vm.UnitsText);
    }

    [Theory]
    [InlineData("07", "CH 7")]
    [InlineData("99", "CH 99")]
    [InlineData("0", "CH 0")]
    public void EveryAcceptedForm_SendsChNnPlusSh(string typed, string wire)
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 55");     // a confirmed channel none of these is
        Transport.ClearSent();

        vm.ChannelInput = typed;
        vm.SelectEnteredCommand.Execute(null);

        Assert.Equal([wire, "SH"], Transport.SentLines);
        Assert.Equal("", vm.InputError);
    }

    [Theory]
    [InlineData("")]            // nothing typed
    [InlineData("100")]         // three digits
    [InlineData("-1")]          // a sign is not a digit
    [InlineData(" 7")]          // D7: NO trim — an entry is not a free-text field
    [InlineData("7 ")]
    [InlineData("a7")]
    [InlineData("1 2")]
    [InlineData("٧")]           // ARABIC-INDIC DIGIT SEVEN — char.IsDigit says yes, ASCII says no
    public void EveryRefusedForm_SaysSoInProse_AndSendsNOTHING(string typed)
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.ClearSent();

        vm.ChannelInput = typed;
        vm.SelectEnteredCommand.Execute(null);

        Assert.Equal("Channel must be a whole number 00-99.", vm.InputError);
        Assert.True(vm.HasInputError);
        Assert.Empty(Transport.SentLines);                // I-2: prose, and no wire
    }

    [Fact]
    public void SelectingTheConfirmedCurrentChannel_SendsNothing_AndIsNoError()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 07");
        Transport.ClearSent();

        // The standing re-click guard: both spellings of the current channel.
        foreach (var same in new[] { "7", "07" })
        {
            vm.ChannelInput = same;
            vm.SelectEnteredCommand.Execute(null);
            Assert.Empty(Transport.SentLines);
            Assert.Equal("", vm.InputError);
            Assert.False(vm.HasInputError);
        }

        // ANTI-VACUITY: any OTHER channel does go out.
        vm.ChannelInput = "8";
        vm.SelectEnteredCommand.Execute(null);
        Assert.Equal(["CH 8", "SH"], Transport.SentLines);
    }

    [Fact]
    public void TheError_ClearsOnTheNextValidSelect()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");
        Transport.ClearSent();

        vm.ChannelInput = "abc";
        vm.SelectEnteredCommand.Execute(null);
        Assert.True(vm.HasInputError);
        Assert.Equal("abc", vm.ChannelInput);   // a refusal KEEPS the buffer (fix the typo in place)

        vm.ChannelInput = "12";
        vm.SelectEnteredCommand.Execute(null);
        Assert.Equal("", vm.InputError);
        Assert.False(vm.HasInputError);
        Assert.Equal(["CH 12", "SH"], Transport.SentLines);
        Assert.Equal("", vm.ChannelInput);      // a valid selection CLEARS it (owner 2026-08-23)

        // …and the re-click guard's silent path clears it too: a valid select
        // that sends nothing is still a valid select.
        vm.ChannelInput = "xx";
        vm.SelectEnteredCommand.Execute(null);
        Assert.True(vm.HasInputError);
        Transport.InjectLine("CHAN 12");
        Transport.ClearSent();
        vm.ChannelInput = "12";
        vm.SelectEnteredCommand.Execute(null);
        Assert.Equal("", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheError_ClearsOnASessionDrop()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.ChannelInput = "999";
        vm.SelectEnteredCommand.Execute(null);
        Assert.True(vm.HasInputError);

        // A stale note dies with the session (the SsbSettings idiom).
        Session.Close();
        Assert.NotEqual(Falcon.App.Core.Session.SessionPhase.Ready, Session.Phase);
        Assert.Equal("", vm.InputError);
        Assert.False(vm.HasInputError);
    }

    [Fact]
    public void EveryBoundProperty_RaisesPropertyChanged()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ChannelInput = "1";
        vm.SelectEnteredCommand.Execute(null);      // a valid select: no error
        vm.ChannelInput = "zz";
        vm.SelectEnteredCommand.Execute(null);      // …then a refusal

        Assert.Contains(nameof(ChannelViewModel.ChannelInput), raised);
        Assert.Contains(nameof(ChannelViewModel.InputError), raised);
        // The warn label binds HasInputError, so it MUST be raised with it.
        Assert.Contains(nameof(ChannelViewModel.HasInputError), raised);

        raised.Clear();
        Transport.InjectLine("CHAN 04");
        Assert.Contains(nameof(ChannelViewModel.TensText), raised);
        Assert.Contains(nameof(ChannelViewModel.UnitsText), raised);
        Assert.Contains(nameof(ChannelViewModel.CurrentChannelText), raised);
    }

    [Fact]
    public void OutsideSsb_TheSelectCommandIsDisabled_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        Assert.False(vm.AreControlsEnabled);
        Assert.NotEqual("", vm.DisabledReason);
        Assert.False(vm.SelectEnteredCommand.CanExecute(null));

        vm.ChannelInput = "5";
        vm.SelectEnteredCommand.Execute(null);      // Execute never consults CanExecute
        vm.SetRxOnlyCommand.Execute("No");
        Assert.Empty(Transport.SentLines);
        Assert.Equal("", vm.InputError);            // …and it is not the operator's mistake
    }

    // ---- RXONLY (channel-stored, F6-gated) -----------------------------------

    [Fact]
    public void RxOnly_UnreportedHighlightsNothing_SetSendsRxon_AnswerMoves()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.InjectLine("CHAN 00");     // the F6 gate: RXONLY edits need CH 00
        Transport.ClearSent();

        // Enum ordinal 0 is Yes — a default leak would light YES here.
        Assert.False(vm.IsRxOnlyYes);
        Assert.False(vm.IsRxOnlyNo);

        vm.SetRxOnlyCommand.Execute("Yes");
        Assert.Equal(["RXON YES"], Transport.SentLines);
        Assert.False(vm.IsRxOnlyYes);                    // no optimism

        Transport.InjectLine("RXONLY YES");
        Assert.True(vm.IsRxOnlyYes);
        Assert.False(vm.IsRxOnlyNo);
    }

    [Fact]
    public void RxOnly_00Gated_NotZeroOrUnconfirmed_NothingSent()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        // Unconfirmed channel counts as NOT 00 (never enable on a default).
        Assert.False(vm.SetRxOnlyCommand.CanExecute("Yes"));
        vm.SetRxOnlyCommand.Execute("Yes");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("CHAN 05");                 // confirmed non-00
        Assert.False(vm.SetRxOnlyCommand.CanExecute("Yes"));
        vm.SetRxOnlyCommand.Execute("Yes");
        Assert.Empty(Transport.SentLines);

        Transport.InjectLine("CHAN 00");                 // gate opens
        Assert.True(vm.SetRxOnlyCommand.CanExecute("Yes"));
        vm.SetRxOnlyCommand.Execute("Yes");
        Assert.Equal(["RXON YES"], Transport.SentLines);
    }
}
