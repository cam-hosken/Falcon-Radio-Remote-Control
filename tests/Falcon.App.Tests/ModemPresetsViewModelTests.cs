using Falcon.App.Core.Demo;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.App.Core.ViewModels;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Tests;

/// <summary>
/// The Radio settings "Modem presets" card, REBUILT round 9 on the
/// channel-editor model: the UNIFIED two-tier read path (editor landings read
/// fresh, the list tab is lazy), the short-token vocabulary with its
/// per-segment dirty guards, the type-switch map (including "a hidden row's
/// value is never sent"), the baud wheel, the read-back row beside the
/// picker, and the ONE-line Store with the round-7 empty-field fallback.
/// </summary>
public class ModemPresetsViewModelTests : SessionTestBase
{
    private const string T39Listing =
        "MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long";

    // The STALE-PICK fixtures (round-1 audit, MAJOR-1). Each lands a value that
    // ONE type offers and another does not. Every baud here except the Voice
    // case is 2400 on purpose — offered by both 39-tone and Serial — so the
    // BAUD check cannot fire first and mask the interleave one.
    private const string VoiceListing =
        "MODEM PRESET 1 T39  ASYNC DATA   BAUD Voice  TYPE 39tone  INTER long";
    private const string AltShortListing =
        "MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER alts";
    private const string AltLongListing =
        "MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER altl";
    private const string ZeroListing =
        "MODEM PRESET 1 SER  ASYNC DATA   BAUD 2400  TYPE serial  INTER zero";

    private ModemPresetsViewModel Vm()
        => new(new ModemSurface(Radio), Session);

    /// <summary>
    /// AUDIT ROUND 1, MAJOR 2 — <b>connected AND at the SSB prompt.</b> The
    /// card is gated on a CONFIRMED MODE now, because the scope IS the mode;
    /// the base helper only reaches Ready, and this fake transport replays no
    /// prompt during connect (a real radio's connect `SH` answer carries one).
    ///
    /// <para>Every pre-existing test in this file already MEANT "connected, at
    /// SSB" — that is the shape they all assert. Hiding the base helper says so
    /// once instead of ninety times, and the two tests that genuinely need the
    /// unconfirmed window build their session by hand.</para></summary>
    private new void ConnectReady()
    {
        base.ConnectReady();
        Transport.InjectLine("SSB>");
        Transport.ClearSent();
        // Deliberately NOT drained: the landing the prompt provokes leaves the
        // same outstanding targeted-plus-queued-presence pair the Ready arrival
        // used to leave before the mode gate existed, which is what the
        // LandOnWithPresence fixture opens its window through.
    }

    private void Pick(ModemPresetsViewModel vm, int preset)
    {
        // BOUNDED. The picker wraps within its band, so `PresetCount` presses
        // reach every position; a loop that has not arrived by then is a picker
        // that cannot move — which is a defect to REPORT, not to spin on.
        // (It span for real: MAJOR 2's confirmed-mode gate made the picker
        // inert without a prompt, and this `while` hung the whole suite.)
        for (int press = 0; press <= ModemPresetsViewModel.PresetCount; press++)
        {
            if (vm.PickedPreset == preset) return;
            vm.PresetUpCommand.Execute(null);
        }
        Assert.Fail($"the picker never reached preset {preset} (stuck at {vm.PickedPreset}) — "
            + "is the card editable? it needs Ready AND a confirmed mode");
    }

    // ---- ROUND 11 §6: the two read tiers, as wire ---------------------------
    // The EDITOR reads ONE preset targeted per landing/spin; the LIST tab's
    // once-per-session landing runs the seven-preset FIELD batch (one
    // operation, ONE closing sentinel) and then queues the bulk PRESENCE read
    // BEHIND it — which the single-slot modem queue will not dispatch until
    // the batch's sentinel has answered.

    /// <summary>One EDITOR landing's wire: the picked preset, plus the
    /// operation's closing sentinel.</summary>
    private static string[] EditorRead(int preset) => [$"MODEM PRE {preset}", "BAT ST"];

    /// <summary>The LIST tab's FIELD batch — everything it sends before its
    /// sentinel is answered. The presence read is NOT here on purpose: it
    /// cannot be on the wire yet.</summary>
    private static readonly string[] ListFieldBatch =
    [
        "MODEM PRE 0", "MODEM PRE 1", "MODEM PRE 2", "MODEM PRE 3",
        "MODEM PRE 4", "MODEM PRE 5", "MODEM PRE 6", "BAT ST",
    ];

    /// <summary>The PRESENCE read, dispatched only after the batch
    /// completes.</summary>
    private static readonly string[] PresenceRead = ["MODEM PRE", "BAT ST"];

    /// <summary>How many TARGETED reads have gone out — one per editor
    /// landing, seven per list batch. A targeted line carries a preset number;
    /// the bulk presence line is the bare command.</summary>
    private int TargetedReadCount
        => Transport.SentLines.Count(l => l.StartsWith("MODEM PRE ", StringComparison.Ordinal));

    private int PresenceReadCount => Transport.CountSent("MODEM PRE");

    /// <summary>Answer every outstanding sentinel so the modem read queue goes
    /// idle. Reads serialize on ONE single-slot queue, so a landing while one
    /// is in flight COALESCES — draining is what "the radio answered" looks
    /// like, and it is bounded: a queue that will not drain is a defect, not a
    /// reason to spin.</summary>
    private void DrainReads()
    {
        for (int i = 0; i < 20 && Radio.PendingPingCount > 0; i++) AnswerSentinel();
        Assert.Equal(0, Radio.PendingPingCount);
    }

    /// <summary>Land on a preset and let the radio answer the landing read
    /// with one listing line — the ordinary sequence on this card, since
    /// every landing re-reads.</summary>
    private void LandOn(ModemPresetsViewModel vm, int preset, string? listing = null)
    {
        Pick(vm, preset);
        if (listing is not null) Transport.InjectLine(listing);
        DrainReads();
    }

    /// <summary>ROUND 13 B1 — an EDITOR landing whose PRESENCE window is
    /// answered too, so the picked preset has a real ENABLED/DISABLED fact
    /// behind it. <see cref="LandOn"/> drains without injecting anything into
    /// the presence window, which commits an EMPTY enabled set (everything
    /// listed reads "Disabled"); a test that needs the OTHER state has to name
    /// the bulk listing.
    ///
    /// <para>The windows are serialized by the single-slot modem queue, so the
    /// bare <c>MODEM PRE</c> appearing on the wire is what says the presence
    /// window — and not a targeted one — is the one now open.</para></summary>
    private void LandOnWithPresence(
        ModemPresetsViewModel vm, int preset, string listing, string[] enabledListing)
    {
        Pick(vm, preset);
        Transport.InjectLine(listing);
        for (int i = 0; i < 20 && PresenceReadCount == 0; i++) AnswerSentinel();
        Assert.Equal(1, PresenceReadCount);                 // the presence window is open
        foreach (var line in enabledListing) Transport.InjectLine(line);
        AnswerSentinel();                                   // its sentinel → the set commits
        DrainReads();
    }

    /// <summary>Drive the LIST tab's full landing to completion: the field
    /// batch answered with <paramref name="fields"/>, then the presence window
    /// answered with <paramref name="enabledListing"/> (the bulk listing, which
    /// names exactly the ENABLED presets).</summary>
    private void LandOnListTab(
        ModemPresetsViewModel vm, string[] fields, string[] enabledListing)
    {
        vm.OpenListTabCommand.Execute(null);
        foreach (var line in fields) Transport.InjectLine(line);
        AnswerSentinel();                       // the batch's sentinel → presence dispatches
        foreach (var line in enabledListing) Transport.InjectLine(line);
        AnswerSentinel();                       // the presence sentinel → the set commits
        DrainReads();
    }

    // ---- Read path: the unified two-tier doctrine ----------------------------

    [Fact]
    public void ReadyArrival_SendsOneTargetedRead_UnderAnOpenCard()
    {
        // The surface first becoming readable in a session IS an editor
        // landing. Driven by hand because ConnectReady()'s trailing
        // ClearSent would wipe the very send under test.
        var vm = Vm();
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(Falcon.App.Core.Session.SessionPhase.Ready, Session.Phase);

        // AUDIT ROUND 1, MAJOR 2: the landing is OWED at Ready and PAID when
        // the radio names its mode — the scope IS the mode, and this fake
        // transport replays no prompt during connect (a real radio's connect
        // `SH` answer carries one, which is why the ordinary order is
        // prompt-then-Ready and the debt is usually settled instantly). No
        // MODEM line is on the wire until it lands (the connect ritual owns the
        // rest of the traffic)…
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("MODEM", StringComparison.Ordinal));
        Transport.InjectLine("SSB>");

        // …and then it is EXACTLY the landing read it always was.
        // ONE preset, not seven: the editor tier reads the preset the operator
        // is looking at (§6). The card opens on preset 0.
        Assert.Single(Transport.SentLines, l => l == "MODEM PRE 0");
        Assert.Equal(1, TargetedReadCount);
        Assert.Equal(0, PresenceReadCount);
        Assert.NotNull(vm);
    }

    [Fact]
    public void EveryEditorLanding_ReadsFresh_NotOncePerSession()
    {
        // Round 9 reverses round 8's once-per-session latch on the EDITOR:
        // a landing on a programming surface re-reads, because a cached
        // listing can be older than the last write from any source.
        var vm = Vm();
        ConnectReady();

        DrainReads();          // the Ready-ARRIVAL read owns the queue first

        vm.EnsureLoaded();                                   // the view's Loaded
        Assert.Equal(1, TargetedReadCount);
        DrainReads();
        vm.EnsureLoaded();                                   // …and again
        Assert.Equal(2, TargetedReadCount);
        DrainReads();

        vm.PresetUpCommand.Execute(null);                    // a picker spin
        Assert.Equal(3, TargetedReadCount);
        DrainReads();

        vm.OpenProgrammingTabCommand.Execute(null);          // a landing: fresh
        Assert.Equal(4, TargetedReadCount);
        // …and nothing else PER LANDING: four landings, each ONE targeted read
        // plus its own sentinel — plus, ONCE, the §9 A3 presence operation the
        // FIRST landing of the session paid for (its own command + sentinel).
        Assert.Equal(10, Transport.SentLines.Count);
        Assert.Equal(1, PresenceReadCount);                  // once, not once per landing
    }

    // ---- CLONE ROUND 12 §9 A3: the FOUR landing sequences ------------------
    // Presence is whole-card state behind its OWN once-per-session gate, split
    // from the list tab's seven-read field batch (critic-12b F5: one shared
    // flag either starves a later list landing or breaks once-per-session).
    // Each sequence is pinned by the EXACT SentLines it produces, sentinels
    // included, because "one extra read" is precisely the sort of cost that
    // creeps in unnoticed on a single-slot queue.

    /// <summary>SEQUENCE 1 — EDITOR-FIRST landing: ONE targeted read, then the
    /// presence operation behind it. The presence op cannot be on the wire
    /// while the targeted read's window is open (the §8 single-slot queue), so
    /// the pin drains between the two halves.</summary>
    [Fact]
    public void EditorFirstLanding_SendsOneTargetedRead_ThenOnePresenceOp_A3()
    {
        // The VM is built AFTER Ready so the only landing on the wire is the
        // one under test (the Ready-ARRIVAL landing has its own pins above).
        ConnectReady();
        var vm = Vm();
        Assert.Empty(Transport.SentLines);

        vm.EnsureLoaded();
        Assert.Equal(EditorRead(0), Transport.SentLines);     // presence is QUEUED, not sent

        AnswerSentinel();                                     // the targeted window closes
        Assert.Equal([.. EditorRead(0), .. PresenceRead], Transport.SentLines);
        DrainReads();
    }

    /// <summary>SEQUENCE 2 — a LATER LIST landing (editor landed first): the
    /// SEVEN-read field batch ONLY. Presence is already loaded, so the list
    /// tab does not re-run it. With round 11's single shared flag this landing
    /// would have sent NOTHING and the list would have stayed empty.</summary>
    [Fact]
    public void ALaterListLanding_SendsTheSevenReadBatchOnly_A3()
    {
        ConnectReady();
        var vm = Vm();
        vm.EnsureLoaded();                                    // editor first
        DrainReads();
        Transport.ClearSent();

        vm.OpenListTabCommand.Execute(null);
        Assert.Equal(ListFieldBatch, Transport.SentLines);

        DrainReads();
        Assert.Equal(ListFieldBatch, Transport.SentLines);     // …and no second presence op
        Assert.Equal(0, PresenceReadCount);
    }

    /// <summary>SEQUENCE 3 — LIST-FIRST landing: the seven-read batch plus ONE
    /// presence operation, in that order. This is round 11's contract,
    /// unchanged, and it is here so the split gates cannot quietly cost the
    /// list tab its presence read.</summary>
    [Fact]
    public void ListFirstLanding_SendsTheBatchThenOnePresenceOp_A3()
    {
        ConnectReady();
        var vm = Vm();
        Assert.Empty(Transport.SentLines);

        vm.OpenListTabCommand.Execute(null);
        Assert.Equal(ListFieldBatch, Transport.SentLines);

        AnswerSentinel();                                     // the batch's window closes
        Assert.Equal([.. ListFieldBatch, .. PresenceRead], Transport.SentLines);
        DrainReads();
    }

    /// <summary>SEQUENCE 4 — a LATER EDITOR landing (list landed first): ONE
    /// targeted read and nothing else. The editor tier stays fresh-every-
    /// landing; presence does not repeat.</summary>
    [Fact]
    public void ALaterEditorLanding_SendsOneTargetedReadOnly_A3()
    {
        ConnectReady();
        var vm = Vm();
        LandOnListTab(vm, [], []);                            // list first, presence loaded
        vm.OpenProgrammingTabCommand.Execute(null);
        DrainReads();
        Transport.ClearSent();

        vm.EnsureLoaded();
        Assert.Equal(EditorRead(0), Transport.SentLines);
        DrainReads();
        Assert.Equal(EditorRead(0), Transport.SentLines);      // still nothing more
        Assert.Equal(0, PresenceReadCount);
    }

    /// <summary>Both gates reset on RECONNECT, and whichever tab lands first
    /// repeats its first-landing shape. A new session may be a different
    /// radio — a presence set carried across would be another radio's.</summary>
    [Fact]
    public void BothLoadGatesReset_OnReconnect_A3()
    {
        ConnectReady();
        var vm = Vm();
        LandOnListTab(vm, [], []);                            // batch + presence spent
        Transport.ClearSent();

        Session.Close();
        // Driven by hand: ConnectReady's trailing ClearSent would wipe the
        // very reconnect landing under test.
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(Falcon.App.Core.Session.SessionPhase.Ready, Session.Phase);
        // MAJOR 2: the new session's mode is unconfirmed until its prompt, and
        // the owed landing is paid then.
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("MODEM", StringComparison.Ordinal));
        Transport.InjectLine("SSB>");

        // The list tab is still open, so the landing is a LIST landing: the
        // batch runs again, and presence follows it again. Only the MODEM
        // lines are asserted — the connect ritual owns the rest of the wire.
        Assert.Equal(ModemLines(), Transport.SentLines.Where(IsModemLine));
        AnswerSentinel();
        Assert.Equal(
            [.. ModemLines(), "MODEM PRE"],
            Transport.SentLines.Where(IsModemLine));
        DrainReads();

        static bool IsModemLine(string l) => l.StartsWith("MODEM", StringComparison.Ordinal);
        static IEnumerable<string> ModemLines() => ListFieldBatch.Where(IsModemLine);
    }

    [Fact]
    public void AnEditorLanding_ReadsTHEPICKEDPreset_NotTheWholeSet()
    {
        // The whole point of the targeted tier: the command names the preset
        // on screen. A batch here would be seven commands to look at one row.
        var vm = Vm();
        ConnectReady();
        DrainReads();
        Transport.ClearSent();

        vm.PresetUpCommand.Execute(null);                    // → preset 1
        Assert.Equal(EditorRead(1), Transport.SentLines);
        DrainReads();
        Transport.ClearSent();

        vm.PresetDownCommand.Execute(null);                  // → preset 0
        Assert.Equal(EditorRead(0), Transport.SentLines);
    }

    [Fact]
    public void EnsureLoaded_NotReady_SendsNothing()
    {
        var vm = Vm();
        vm.EnsureLoaded();
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void ListTab_RunsTheFieldBatchThenThePresenceRead_InThatOrder()
    {
        // §6's ordering contract, on the wire. The presence read is NOT on the
        // wire while the batch is open — the single-slot queue holds it — and
        // it goes out the moment the batch's sentinel answers. That order is
        // what stops a TARGETED row being counted as "listed by the bulk", i.e.
        // as ENABLED.
        ConnectReady();
        var vm = Vm();
        Assert.Empty(Transport.SentLines);

        vm.OpenListTabCommand.Execute(null);
        Assert.Equal(ListFieldBatch, Transport.SentLines);
        Assert.Equal(0, PresenceReadCount);

        AnswerSentinel();                                    // the batch completes
        Assert.Equal([.. ListFieldBatch, .. PresenceRead], Transport.SentLines);
        Assert.Equal(1, PresenceReadCount);
        DrainReads();
    }

    [Fact]
    public void ListTab_IsTheLazyTier_ItReadsOnlyOncePerSession()
    {
        // The VM is built AFTER Ready here — the one shape in which the list
        // tab can genuinely be the first thing visited (no Ready arrival to
        // catch, no view Loaded yet).
        ConnectReady();
        var vm = Vm();

        LandOnListTab(vm, [], []);
        var afterFirst = Transport.SentLines.ToArray();

        vm.OpenListTabCommand.Execute(null);                 // renders from the mirrors
        Assert.Equal(afterFirst, Transport.SentLines);
    }

    [Fact]
    public void AnEditorRead_DoesNotSatisfyTheListTabsGate()
    {
        // Round 11 §6 narrows round 9's gate. An editor landing reads ONE
        // preset and never runs the presence operation, so it cannot stand in
        // for the batch-plus-presence pair the list needs — landing on the list
        // after using the editor must still read.
        var vm = Vm();
        ConnectReady();
        DrainReads();                                        // the Ready-arrival editor read
        Transport.ClearSent();

        vm.OpenListTabCommand.Execute(null);

        Assert.Equal(ListFieldBatch, Transport.SentLines);
    }

    [Fact]
    public void Reconnect_ReadsWithoutClearingTyping_TheStandingPin()
    {
        // The two axes are orthogonal: the Ready-arrival read is a READ, not
        // a populate GESTURE. A drop and its reconnect must never eat the
        // operator's typing.
        var vm = Vm();
        ConnectReady();
        vm.NameInput = "XYZ";

        Session.Close();
        Transport.ClearSent();
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();                                    // (plus the session ritual)
        // MAJOR 2: a reconnect resets the mode mirror too, so the owed landing
        // is paid when the NEW session's prompt names the scope.
        Transport.InjectLine("SSB>");

        Assert.Equal(1, TargetedReadCount);                  // it read
        Assert.Equal("XYZ", vm.NameInput);                   // …and kept the typing
    }

    [Fact]
    public void PickerSpin_IsAPopulateGesture_AndAnEditorLanding()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        vm.NameInput = "ABC";
        vm.SelectedType = "FSKW";                            // operator override (dirty)
        Transport.ClearSent();

        vm.PresetUpCommand.Execute(null);                    // → preset 2

        Assert.Equal(EditorRead(2), Transport.SentLines);    // the landing read
        Assert.Equal("", vm.NameInput);                      // gesture cleared typing
        Assert.Null(vm.SelectedType);                        // …and reset the dirty pick
    }

    [Fact]
    public void TabSwitch_ClearsTypedText_OnTheProgrammingLanding()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        vm.NameInput = "XYZ";

        vm.OpenListTabCommand.Execute(null);
        Assert.True(vm.IsListTabOpen);
        Assert.Equal("XYZ", vm.NameInput);                   // leaving is not a gesture

        vm.OpenProgrammingTabCommand.Execute(null);
        Assert.False(vm.IsListTabOpen);
        Assert.Equal("", vm.NameInput);
    }

    // ---- Listing → rows, read-back and prefill -------------------------------

    [Fact]
    public void ListingLine_PopulatesRow_ParsedNumberAndName_VerbatimParameters()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine(T39Listing);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("1", row.NumberText);
        Assert.Equal("T39", row.NameText);
        Assert.Equal("ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long", row.ParametersText);
        Assert.False(vm.HasNoPresets);
    }

    [Fact]
    public void NumericPresetName_SplitsTheRowCorrectly()
    {
        // R8-review MAJOR 4: a name equal to the number token must not fool
        // the Parameters split ("1 1  ASYNC …" — IndexOf found the first "1").
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("MODEM PRESET 1 1  ASYNC DATA   BAUD 2400  TYPE fskws  INTER long");

        var row = Assert.Single(vm.Rows);
        Assert.Equal("1", row.NumberText);
        Assert.Equal("1", row.NameText);
        Assert.Equal("ASYNC DATA   BAUD 2400  TYPE fskws  INTER long", row.ParametersText);
    }

    [Fact]
    public void TheReadBackRow_IsThePickedPreset_InTheListsOwnProjection()
    {
        // The BF2 contract, ported: the editor's read-back and the list row
        // are ONE projection, so two views of one preset cannot disagree.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);

        Assert.Equal("1", vm.PickedRow.NumberText);
        Assert.Equal("T39", vm.PickedRow.NameText);
        Assert.Equal(vm.Rows.Single().ParametersText, vm.PickedRow.ParametersText);
    }

    [Fact]
    public void AnUnlistedPreset_ReadsBackAsDashes_AndPrefillsNothing()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2, T39Listing);                           // nothing listed for 2

        Assert.Equal("2", vm.PickedRow.NumberText);
        Assert.Equal("—", vm.PickedRow.NameText);
        Assert.Equal("—", vm.PickedRow.ParametersText);
        Assert.Null(vm.SelectedType);
        Assert.Null(vm.SelectedDataMode);
        Assert.Null(vm.SelectedInterleave);
        Assert.Null(vm.SelectedBaud);
        Assert.Equal("—", vm.BaudText);
    }

    [Fact]
    public void PickedPreset_PrefillsEverySelectionThroughTheVocabulary()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);

        Assert.Equal("39TONE", vm.SelectedType);             // "39tone"
        Assert.Equal("ASYNC DAT", vm.SelectedDataMode);      // "ASYNC DATA"
        Assert.Equal("2400", vm.SelectedBaud);               // "2400"
        Assert.Equal("2400", vm.BaudText);
        Assert.Equal("LO", vm.SelectedInterleave);           // "INTER long"
        // ROUND 13 B1 (item 7, plan §4 B1, owner 2026-08-19). This asserted
        // Assert.Null "never echoed" through round 12: the state prefill read
        // LISTING tokens, and no listing carries a preset's state, so the
        // selection was structurally always empty — the owner's report was
        // that picking a preset never lights its Enabled/Disabled button.
        // The prefill now comes from the PRESENCE store, which IS captured.
        // LandOn drains the landing's reads; the drained presence window
        // listed nothing, so preset 1 has fields and is absent from a
        // COMPLETED enabled set — the one captured "Disabled" signal, and the
        // same one PickedRow.PresenceText renders (pinned together below, so
        // the button and the cell cannot drift apart again).
        Assert.Equal("DIS", vm.SelectedState);
        Assert.Equal("Disabled", vm.PickedRow.PresenceText);
        Assert.Equal("", vm.NameInput);                      // X5: entries stay the operator's
    }

    [Fact]
    public void AnUnmappedListingToken_LeavesTheRowEmpty_TheRowStillShowsItVerbatim()
    {
        // The AGC precedent. A type nobody has captured must not be guessed
        // into a selection — it blocks Store until the operator picks.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, "MODEM PRESET 1 T57  ASYNC DATA   BAUD 2400  TYPE 57tone  INTER long");

        Assert.Null(vm.SelectedType);
        Assert.Contains("57tone", vm.PickedRow.ParametersText, StringComparison.Ordinal);

        Transport.ClearSent();
        vm.StoreCommand.Execute(null);
        Assert.True(vm.HasInputError);
        Assert.Contains("type", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void OperatorPicks_SurviveALaterReport_ThePerSegmentDirtyGuard()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);

        vm.SelectedType = "FSKW";                            // the operator's picks
        vm.SelectedDataMode = "SYNC DAT";
        vm.SelectedBaud = "75";
        vm.SelectedState = "EN";
        Transport.InjectLine(T39Listing);                    // a fresh report lands

        Assert.Equal("FSKW", vm.SelectedType);
        Assert.Equal("SYNC DAT", vm.SelectedDataMode);
        Assert.Equal("75", vm.SelectedBaud);
        Assert.Equal("EN", vm.SelectedState);
        // …and the untouched row still followed the radio.
        Assert.Equal("T39", vm.PickedRow.NameText);
    }

    // ---- The type-switch map -------------------------------------------------

    [Fact]
    public void NoTypeSelected_RendersNothingTypeDependent()
    {
        var vm = Vm();
        ConnectReady();
        Assert.Null(vm.SelectedType);
        Assert.False(vm.ShowInterleave);
        Assert.False(vm.ShowMarkSpace);
    }

    [Fact]
    public void InterleaveRendersUnderToneWaveforms_MarkSpaceAtFskVftOnly_NeverBoth()
    {
        // ROUND 11 §6 narrows the round-9 map on BOTH sides: interleave still
        // belongs to the tone waveforms, but MARK/SPACE now render at fsk-v
        // ALONE — the other three FSK types store them and never read them
        // back, so the card would be offering an unverifiable write.
        var vm = Vm();
        ConnectReady();

        foreach (var tone in new[] { "39TONE", "SE" })
        {
            vm.SelectedType = tone;
            Assert.True(vm.ShowInterleave);
            Assert.False(vm.ShowMarkSpace);
        }
        foreach (var fsk in new[] { "FSKW", "FSKN", "FSK-A" })
        {
            vm.SelectedType = fsk;
            Assert.False(vm.ShowInterleave);
            Assert.False(vm.ShowMarkSpace);
        }

        vm.SelectedType = "FSK-V";
        Assert.False(vm.ShowInterleave);
        Assert.True(vm.ShowMarkSpace);
    }

    [Fact]
    public void SerialAt4800_HidesTheInterleaveRow_OnTheBaudAlone()
    {
        // The one type-switch that is NOT about the type: Serial at 4800 is
        // `uncoded`, a spelling with no write token at all (VERIFIED
        // 2026-08-16 — writing BAUD 4800 at SE replaced a stored `zero` with
        // it). The row has to follow the BAUD as well as the type.
        var vm = Vm();
        ConnectReady();

        vm.SelectedType = "SE";
        vm.SelectedBaud = "2400";
        Assert.True(vm.ShowInterleave);
        Assert.Equal(["Long", "Short", "Zero"], vm.InterleaveChoices.Select(c => c.Value));

        vm.SelectedBaud = "4800";
        Assert.False(vm.ShowInterleave);
        Assert.Empty(vm.InterleaveChoices);

        vm.SelectedBaud = "2400";
        Assert.True(vm.ShowInterleave);
    }

    [Fact]
    public void TheInterleaveOffer_IsTypeScoped_NotTheWholeColumn()
    {
        // Round 10 offered all five values on both tone types, so two of the
        // five drew ** ERROR ** on each. §6 offers what the type takes.
        var vm = Vm();
        ConnectReady();

        vm.SelectedType = "39TONE";
        Assert.Equal(["Long", "Short", "Alt short", "Alt long"],
            vm.InterleaveChoices.Select(c => c.Value));

        vm.SelectedType = "SE";
        Assert.Equal(["Long", "Short", "Zero"], vm.InterleaveChoices.Select(c => c.Value));
    }

    [Fact]
    public void TheBaudWheel_IsTypeScoped_AndCyclesOnlyWhatTheTypeStores()
    {
        // Every baud past a type's ceiling is SILENTLY CLAMPED, and the echo
        // reports success either way — so the only defence is to never offer
        // it. Walked as a full cycle, because "the wheel wraps early" is the
        // observable an operator would meet first.
        var vm = Vm();
        ConnectReady();
        vm.SelectedType = "FSKW";                            // fskws: ≤ 300
        vm.SelectedBaud = "75";

        var seen = new List<string?>();
        for (int i = 0; i < 3; i++)
        {
            seen.Add(vm.SelectedBaud);
            vm.BaudUpCommand.Execute(null);
        }
        Assert.Equal(["75", "150", "300"], seen);
        Assert.Equal("75", vm.SelectedBaud);                 // wrapped at the ceiling

        vm.BaudDownCommand.Execute(null);
        Assert.Equal("300", vm.SelectedBaud);

        // …and Voice, which exists at 39-tone only, is not on this wheel.
        vm.SelectedType = "39TONE";
        vm.SelectedBaud = "2400";
        vm.BaudUpCommand.Execute(null);
        Assert.Equal("VO", vm.SelectedBaud);
    }

    // ---- The STALE-PICK family (§6, at the SENDING surface) -----------------
    //
    // A re-scope deliberately does not CLEAR a selection (that would discard
    // the operator's pick silently), so every scoped row can hold a value its
    // current offer no longer contains. The ONLY thing between a stale pick and
    // the wire is Store's re-check — so every row that can go stale gets a pin,
    // as a FAMILY rather than one case.
    //
    // ROUND-1 AUDIT, MAJOR-1: the interleave member of this family did not
    // exist. Store checked only `!ShowInterleave`, so a stale ALTS survived a
    // 39-tone → Serial switch (the row STAYS VISIBLE offering LO/SH/ZE) and
    // went out as a radio-invalid INTERLEAV. The theory below is the fix's
    // red-check: every case fails against the pre-fix code.

    /// <summary>Each case: the listing landed on, the operator's pick, the
    /// type switched to, and a fragment of the offender the refusal must
    /// NAME. Every row here stays VISIBLE after the switch except where the
    /// case says otherwise — an invisible row was already covered by the
    /// round-9 hidden-row rule, and it is the VISIBLE ones that were the
    /// hole.</summary>
    public static TheoryData<string, string, string, string, string> StalePicks => new()
    {
        // BAUD, ceiling case: 39-tone 2400 → fskns, which stores only 75.
        { "baud", T39Listing, "2400", "FSKN", "2400" },
        // BAUD, the NON-ceiling case the round-1 wording missed: Voice is not
        // "above" anything — it simply does not exist off 39-tone.
        { "baud", VoiceListing, "VO", "SE", "Voice" },
        // INTERLEAVE, the MAJOR-1 repro: 39-tone ALTS → Serial. Serial's row is
        // VISIBLE and offers LO/SH/ZE; ALTS is not among them.
        { "interleave", AltShortListing, "ALTS", "SE", "Alt short" },
        // …and its inverse, so the fix cannot be a one-directional special case.
        { "interleave", ZeroListing, "ZE", "39TONE", "Zero" },
        // INTERLEAVE, ALTL — the second 39-tone-only value, so a fix that
        // hard-coded ALTS would still fail here.
        { "interleave", AltLongListing, "ALTL", "SE", "Alt long" },
    };

    [Theory]
    [MemberData(nameof(StalePicks))]
    public void AStalePick_SurvivesTheSwitch_AndStoreRefusesItByName(
        string row, string listing, string picked, string switchedTo, string offender)
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, listing);

        // The pick really landed from the listing — otherwise "it survived"
        // would be asserting nothing.
        Assert.Equal(picked, row == "baud" ? vm.SelectedBaud : vm.SelectedInterleave);

        vm.SelectedType = switchedTo;

        // It SURVIVES the re-scope (the deliberate half of the contract)…
        Assert.Equal(picked, row == "baud" ? vm.SelectedBaud : vm.SelectedInterleave);
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        // …and the press refuses, NAMING the offender in its display word —
        // never the wire token (R13).
        Assert.True(vm.HasInputError, $"the stale {row} pick went out unrefused");
        Assert.Contains(offender, vm.InputError, StringComparison.Ordinal);
        if (picked != offender)   // …and where the two differ, the WIRE token is ABSENT (R13)
            Assert.DoesNotContain(picked, vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheStalePickTheory_CoversEveryROW_ThatCanGoStale()
    {
        // Anti-vacuity for the family claim. The scoped rows are exactly baud,
        // interleave and MARK/SPACE; the first two are theory cases above and
        // MARK/SPACE has its own test below (its staleness HIDES the row, so it
        // is a different shape). Type, Port and Preset state offer their whole
        // columns always and cannot go stale — if one of them ever becomes
        // type-scoped, this count fails and the family has to be re-derived.
        var rows = StalePicks.Select(c => (string)c[0]!).Distinct().Order().ToList();

        Assert.Equal(["baud", "interleave"], rows);
        Assert.Equal(6, ModemPresetVocabulary.Types.Count);
        Assert.Equal(3, ModemPresetVocabulary.DataModes.Count);
        Assert.Equal(2, ModemPresetVocabulary.States.Count);
    }

    [Fact]
    public void StaleMarkSpace_HidesItsRow_AndStoreStillRefusesIt()
    {
        // The third family member. MARK/SPACE go stale by the row DISAPPEARING
        // (fsk-v is the only type that offers them), so the entries stay filled
        // and invisible — which is precisely when an operator would not know
        // they were still there.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2);
        vm.NameInput = "FSKV";
        vm.SelectedType = "FSK-V";
        vm.SelectedDataMode = "ASYNC DAT";
        vm.SelectedBaud = "600";
        vm.MarkInput = "1575";
        vm.SpaceInput = "1425";

        vm.SelectedType = "39TONE";                          // the row hides…
        Assert.False(vm.ShowMarkSpace);
        Assert.Equal("1575", vm.MarkInput);                  // …the values survive
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("FSK VFT", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheStalePickChecks_ReadTheVALUESBeingSent_NotTheShowFlags()
    {
        // The reason MAJOR-1 existed: `ShowInterleave` answers "is the row on
        // screen", and Store needs "will the radio store this value". They
        // diverge exactly when a row stays VISIBLE with its offer changed
        // underneath. Pinned as the OBSERVABLE — the row is up, its offer does
        // not contain the pick, and the press still refuses.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, AltShortListing);

        vm.SelectedType = "SE";

        Assert.True(vm.ShowInterleave);                      // the row IS on screen
        Assert.DoesNotContain(vm.InterleaveChoices, c => c.Value == "Alt short");
        Assert.Equal("ALTS", vm.SelectedInterleave);         // …and holds a value it does not offer
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void AnInScopePick_AfterASwitch_StillGoesOut()
    {
        // The mutation partner for the whole family: a refusal that fired on
        // every type switch would satisfy every pin above. Switch to a type
        // that DOES offer the held values, and the line goes out unchanged.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);                           // 39-tone, 2400, LO
        Assert.Equal("LO", vm.SelectedInterleave);

        vm.SelectedType = "SE";                              // serial: stores 2400, takes LO
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains(Transport.SentLines,
            l => l == "MODEM PRESET 1 NAME T39 TYPE SE ASYNC DAT BAUD 2400 INTERLEAV LO");
    }

    [Fact]
    public void InterleavePrefill_IsSkippedWhereTheRowDoesNotApply()
    {
        // A hidden row must not be handed a value Store would then refuse to
        // send. An FSK preset listing an interleave prefills no interleave.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, "MODEM PRESET 1 FSK1  ASYNC DATA   BAUD 2400  TYPE fskws  INTER long");

        Assert.Equal("FSKW", vm.SelectedType);
        Assert.False(vm.ShowInterleave);
        Assert.Null(vm.SelectedInterleave);
    }

    // ---- CLONE ROUND 12 §9 A2: an EMPTY offer CLEARS the pick --------------

    /// <summary>THE FIRST OF TWO PATHS, and the re-pin. Round 9 through
    /// round 11 asserted the opposite here: a hidden interleave SURVIVED and
    /// Store refused it, naming a value the operator could no longer see or
    /// clear. §9 A2 narrows that rule — an EMPTY offer renders NO row at all,
    /// so there is nothing for "visibility is a rendering fact" to protect and
    /// the pick becomes invisible state whose only future is a confusing
    /// refusal. It clears, and Store then sends the rest of the line.</summary>
    [Fact]
    public void AnEmptyInterleaveOffer_ClearsThePick_AndStoreThenSends_A2()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        Assert.Equal("LO", vm.SelectedInterleave);
        Assert.True(vm.ShowInterleave);

        vm.SelectedType = "FSKW";                            // FSK offers NO interleave…
        vm.SelectedBaud = "300";                             // (in fskws's scope, so the
                                                             //  BAUD guard stays quiet)

        Assert.False(vm.ShowInterleave);                     // …the row is GONE…
        Assert.Null(vm.SelectedInterleave);                  // …and so is the pick (§9 A2)
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        // No refusal, and the line goes out WITHOUT an interleave clause — the
        // cleared pick is genuinely gone, not merely un-refused.
        Assert.False(vm.HasInputError);
        Assert.Contains(Transport.SentLines,
            l => l == "MODEM PRESET 1 NAME T39 TYPE FSKW ASYNC DAT BAUD 300");
        Assert.DoesNotContain(Transport.SentLines,
            l => l.Contains("INTERLEAV", StringComparison.Ordinal));
    }

    /// <summary>The DIRTY FLAG clears with the pick — the other half of "a
    /// pick cannot outlive its offer". A cleared pick is not an operator
    /// preference, so the next prefill must be allowed to fill the row again;
    /// leaving the flag set would strand the row empty for the rest of the
    /// session.</summary>
    [Fact]
    public void AnEmptyInterleaveOffer_AlsoClearsTheDirtyFlag_SoAPrefillWorksAgain_A2()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        vm.SelectedInterleave = "SH";                        // a deliberate operator pick — DIRTY

        vm.SelectedType = "FSKW";                            // empty offer → cleared
        Assert.Null(vm.SelectedInterleave);

        // A later report on a type that DOES offer interleave prefills again.
        // With the dirty flag still set, PopulateEditor would skip it forever.
        vm.SelectedType = "39TONE";
        Transport.InjectLine(T39Listing);
        DrainReads();

        Assert.True(vm.ShowInterleave);
        Assert.Equal("LO", vm.SelectedInterleave);
    }

    /// <summary>The A2 clear fires from INSIDE PopulateEditor's populating
    /// window (a type prefill raises the change that lands in the rebuild), so
    /// it must SAVE AND RESTORE that window rather than end it. If it ended it,
    /// every prefill after the type — baud, interleave, state — would mark
    /// itself DIRTY on a REPORT, and the editor would freeze on one listing's
    /// values for the rest of the session.
    /// <para>Driven through a real report so the window is genuinely open: a
    /// listing whose type offers no interleave, landing while a pick is
    /// held.</para></summary>
    [Fact]
    public void TheEmptyOfferClear_DoesNotEndTheSurroundingPopulateWindow_A2()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);                           // 39-tone / 2400 / LO
        Assert.Equal("LO", vm.SelectedInterleave);

        // A REPORT (not a gesture) for the same preset, on an FSK type: the
        // prefill window opens, the type prefill empties the interleave offer,
        // and the clear runs mid-window.
        Transport.InjectLine(
            "MODEM PRESET 1 FSK1  ASYNC DATA   BAUD 300   TYPE fskws");
        DrainReads();

        Assert.Null(vm.SelectedInterleave);
        Assert.Equal("FSKW", vm.SelectedType);
        Assert.Equal("300", vm.SelectedBaud);                // the baud prefill still ran…

        // …and it did NOT mark itself dirty: a LATER report still prefills it.
        Transport.InjectLine(
            "MODEM PRESET 1 FSK1  ASYNC DATA   BAUD 600   TYPE fskws");
        DrainReads();
        Assert.Equal("600", vm.SelectedBaud);
    }

    /// <summary>THE SECOND PATH — the BELT stays. A NON-EMPTY offer that does
    /// not contain the pick keeps round 9's rule: the row is on screen, the
    /// operator can see and change it, and Store refuses by name. This is what
    /// makes A2 a narrowing rather than a reversal, and it is asserted right
    /// beside the clear so the two can never be confused for each other.
    /// <para>(The full offender-naming matrix is the StalePicks theory
    /// above; this pin is about the two paths being DIFFERENT.)</para></summary>
    [Fact]
    public void ANonEmptyOfferKeepsThePick_AndStoreStillRefusesIt_A2()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, AltShortListing);                      // 39-tone, ALTS
        Assert.Equal("ALTS", vm.SelectedInterleave);

        vm.SelectedType = "SE";                              // Serial at 2400 offers LO/SH/ZE

        Assert.True(vm.ShowInterleave);                      // NON-empty: the row stays…
        Assert.Equal("ALTS", vm.SelectedInterleave);         // …and so does the pick
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("Alt short", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Store_MarkSpaceOnANonFskType_Refuses()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2);
        vm.NameInput = "T40";
        vm.SelectedType = "39TONE";
        vm.SelectedDataMode = "ASYNC DAT";
        vm.SelectedBaud = "2400";
        vm.MarkInput = "1575";
        vm.SpaceInput = "1425";
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("FSK", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Store_MarkWithoutSpace_Refuses()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2);
        vm.NameInput = "FSK1";
        vm.SelectedType = "FSK-V";                           // §6: the one type that offers them
        vm.SelectedDataMode = "ASYNC DAT";
        vm.SelectedBaud = "600";                             // fsk-v's ceiling
        vm.MarkInput = "1575";
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("together", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Store_MarkSpaceOutsideTheCapturedBounds_Refuses()
    {
        // 350-3250 — the MEASURED accepted extremes (2026-08-18). VALUE-ONLY
        // re-base from clone round 12 P2, which moved the vocabulary constants
        // off the INTERPOLATED 500-3200 pair; outside the window the radio
        // SILENTLY keeps the old values, which is the reason the client draws a
        // bound at all. Round 10 accepted any 1-6 digits, which let 9 and
        // 999999 onto the wire to be stored as something else.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2);
        vm.NameInput = "FSKV";
        vm.SelectedType = "FSK-V";
        vm.SelectedDataMode = "ASYNC DAT";
        vm.SelectedBaud = "600";
        vm.SpaceInput = "1425";

        foreach (var outOfBounds in new[] { "349", "3251" })
        {
            vm.MarkInput = outOfBounds;
            Transport.ClearSent();
            vm.StoreCommand.Execute(null);

            Assert.True(vm.HasInputError);
            Assert.Contains("350-3250", vm.InputError, StringComparison.Ordinal);
            Assert.Empty(Transport.SentLines);
        }

        // Both bounds are INCLUSIVE — the other half of a bound pin.
        foreach (var inBounds in new[] { "350", "3250" })
        {
            vm.MarkInput = inBounds;
            Transport.ClearSent();
            vm.StoreCommand.Execute(null);

            Assert.False(vm.HasInputError);
            Assert.Contains(Transport.SentLines, l => l.Contains("MARK " + inBounds, StringComparison.Ordinal));
        }
    }

    // ---- The baud wheel ------------------------------------------------------

    [Fact]
    public void BaudWheel_StartsAtDash_AndTheFirstSpinTakesTheReportedValue()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2);                                       // nothing listed
        Assert.Equal("—", vm.BaudText);

        vm.BaudUpCommand.Execute(null);
        Assert.Equal("75", vm.SelectedBaud);                 // no report → the set's first

        LandOn(vm, 1, T39Listing);                           // a listed preset prefills
        Assert.Equal("2400", vm.SelectedBaud);
    }

    [Fact]
    public void BaudWheel_SpinFromDash_LandsOnTheReportedBaud_NotPastIt()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, "MODEM PRESET 1 T39  ASYNC DATA   BAUD 600  TYPE 39tone  INTER long");
        vm.SelectedBaud = null;                              // as if never prefilled
        Assert.Equal("—", vm.BaudText);

        vm.BaudDownCommand.Execute(null);
        Assert.Equal("600", vm.SelectedBaud);
    }

    [Fact]
    public void BaudWheel_CyclesTheDiscreteSet_AndWrapsBothWays()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2);
        vm.SelectedBaud = "75";

        var seen = new List<string?>();
        for (int i = 0; i < 8; i++)
        {
            seen.Add(vm.SelectedBaud);
            vm.BaudUpCommand.Execute(null);
        }
        Assert.Equal(["75", "150", "300", "600", "1200", "2400", "4800", "VO"], seen);
        Assert.Equal("75", vm.SelectedBaud);                 // wrapped forward
        Assert.Equal("75", vm.BaudText);

        vm.BaudDownCommand.Execute(null);
        Assert.Equal("VO", vm.SelectedBaud);                 // wrapped back
        Assert.Equal("Voice", vm.BaudText);                  // …in the display word
    }

    [Fact]
    public void BaudWheel_HonoursTheDirtyGuard()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        Assert.Equal("2400", vm.SelectedBaud);

        // 39-tone's wheel ends at 2400 and then Voice (§6) — not at 4800,
        // which this type does not store.
        vm.BaudUpCommand.Execute(null);
        Assert.Equal("VO", vm.SelectedBaud);
        Transport.InjectLine(T39Listing);                    // a fresh report lands
        Assert.Equal("VO", vm.SelectedBaud);                 // the operator's spin survives
    }

    // ---- Store ---------------------------------------------------------------

    [Fact]
    public void Store_NothingTyped_RoundTripsTheReportedRecord_InShortTokens()
    {
        // Nothing typed: name falls back to the reported name and every
        // selection rides its prefill — the whole stored record goes back in
        // the round-9 vocabulary (the round-7 fallback rule).
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Equal(
            [
                "MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400 INTERLEAV LO",
                // §6: the echo cannot show a CLAMP, so the written preset is
                // re-read targeted. No EN/DIS on the line, so no presence read.
                "MODEM PRE 1", "BAT ST",
            ],
            Transport.SentLines);
    }

    [Fact]
    public void Store_TypedAndOptionalValues_ComposeTheFullLine()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 2);
        vm.NameInput = "fsk1";
        vm.SelectedType = "FSK-V";                           // §6: MARK/SPACE live here
        vm.SelectedDataMode = "SYNC DAT";
        vm.SelectedBaud = "75";
        vm.MarkInput = "1575";
        vm.SpaceInput = "1425";
        vm.SelectedState = "DIS";
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Equal(
            [
                "MODEM PRESET 2 NAME FSK1 TYPE FSK-V SYNC DAT BAUD 75 MARK 1575 SPACE 1425 DIS",
                // §6's post-write verify: the written preset targeted, and —
                // because the line carried DIS — the presence read queued
                // behind it (dispatched when this sentinel answers).
                "MODEM PRE 2", "BAT ST",
            ],
            Transport.SentLines);
    }

    [Fact]
    public void Store_TappingTheLitSegmentClearsIt_AndTheValueLeavesTheLine()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);

        // Tap the lit Interleave and State segments: an optional row cleared
        // is a row OMITTED, not a row sent empty.
        Selected(vm.InterleaveChoices, "Long").SelectCommand.Execute(null);
        Assert.Null(vm.SelectedInterleave);
        Selected(vm.StateChoices, "Enabled").SelectCommand.Execute(null);
        Assert.Equal("EN", vm.SelectedState);
        Selected(vm.StateChoices, "Enabled").SelectCommand.Execute(null);
        Assert.Null(vm.SelectedState);
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.Equal(
            ["MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400", "MODEM PRE 1", "BAT ST"],
            Transport.SentLines);
    }

    [Fact]
    public void TheChoiceButtonsShowWords_AndSelectWireTokens()
    {
        // Ruling 1, end to end: what the operator reads and what the line
        // carries are two different vocabularies from one map.
        var vm = Vm();
        ConnectReady();

        // ROUND 11 §3's display words.
        Assert.Equal(
            ["39 tone", "FSK wide", "FSK narrow", "FSK ASCII", "FSK VFT", "Serial"],
            vm.TypeChoices.Select(c => c.Value));
        Assert.Equal(
            ["Remote port (async)", "Data port (async)", "Data port (sync)"],
            vm.DataModeChoices.Select(c => c.Value));
        Assert.Equal(["Enabled", "Disabled"], vm.StateChoices.Select(c => c.Value));

        // Interleave is TYPE-SCOPED now (§6), so its words are read under a
        // type — there is no such thing as "the interleave row" without one.
        vm.SelectedType = "39TONE";
        Assert.Equal(
            ["Long", "Short", "Alt short", "Alt long"],
            vm.InterleaveChoices.Select(c => c.Value));

        Selected(vm.TypeChoices, "FSK narrow").SelectCommand.Execute(null);
        Assert.Equal("FSKN", vm.SelectedType);
        Selected(vm.DataModeChoices, "Remote port (async)").SelectCommand.Execute(null);
        Assert.Equal("ASYNC REM", vm.SelectedDataMode);
        // …and the pick lights the button the operator pressed.
        Assert.True(Selected(vm.TypeChoices, "FSK narrow").IsActive);
    }

    [Fact]
    public void Store_UnstoredPresetWithNothingTyped_RefusesAndNamesTheField()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 3);
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("Preset 3", vm.InputError);
        Assert.Contains("name", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void Store_NoBaudPicked_Refuses()
    {
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 3);
        vm.NameInput = "T39";
        vm.SelectedType = "39TONE";
        vm.SelectedDataMode = "ASYNC DAT";
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("baud", vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void StoreEcho_UpsertsTheRow_AndTheTargetedReReadStillGoesOut()
    {
        // RENAMED round 11 (audit round 2). This pinned "the echo IS the
        // read-back" — the round-9 doctrine §6 retired, and the name outlived
        // it by a whole phase. What it ACTUALLY pins is the upsert: the echo
        // replaces that preset's row rather than duplicating it. The second
        // half is the correction: the echo is not the whole read-back, so the
        // press also re-reads the preset TARGETED (a clamped baud is invisible
        // in the echo), and this test says so rather than leaving the retired
        // claim standing in a test name.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);
        Assert.Equal(["MODEM PRE 1", "BAT ST"], Transport.SentLines.Skip(1));

        // The programming echo (same listing form, new baud) REPLACES the row.
        Transport.InjectLine("MODEM PRESET 1 T39  ASYNC DATA   BAUD 1200  TYPE 39tone  INTER long");

        var row = Assert.Single(vm.Rows);
        Assert.Contains("BAUD 1200", row.ParametersText);
        Assert.Contains("BAUD 1200", vm.PickedRow.ParametersText);
        Assert.Equal("1200", vm.SelectedBaud);
        DrainReads();
    }

    // ==== ROUND 11 §6: the STATE column, from the presence store ============

    /// <summary>One preset's line in the shape both the targeted read and the
    /// bulk listing answer in — which is exactly why their windows must never
    /// overlap.</summary>
    private static string Listing(int preset, string name)
        => $"MODEM PRESET {preset} {name}  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long";

    [Fact]
    public void TheStateColumn_RendersEnabledDisabledAndDash_TheThreeStates()
    {
        // The whole §6 read model, end to end. Three presets have FIELDS (a
        // targeted read saw them); the bulk listing names two of them, so the
        // third is DISABLED — absence from the bulk is the only captured
        // disabled signal there is.
        ConnectReady();
        var vm = Vm();

        // BEFORE any presence read completes, every row is "—". Not
        // "Disabled": nothing has been read, and calling an unread preset
        // disabled is inventing a radio answer.
        vm.OpenListTabCommand.Execute(null);
        Transport.InjectLine(Listing(0, "P0"));
        Transport.InjectLine(Listing(1, "P1"));
        Transport.InjectLine(Listing(2, "P2"));
        Assert.Equal(["—", "—", "—"], vm.Rows.Select(r => r.PresenceText));

        AnswerSentinel();                                    // batch done → presence out
        Transport.InjectLine(Listing(0, "P0"));              // the bulk listing:
        Transport.InjectLine(Listing(2, "P2"));              // 0 and 2 are ENABLED
        AnswerSentinel();                                    // → the set commits

        Assert.Equal(["Enabled", "Disabled", "Enabled"], vm.Rows.Select(r => r.PresenceText));
        // …and the FIELDS of the disabled preset survived: the bulk listing
        // that omitted it did not clear its row (§8's keyed mirror).
        Assert.Equal("P1", vm.Rows.Single(r => r.NumberText == "1").NameText);
        DrainReads();
    }

    [Fact]
    public void TheStateColumn_GoesBackToDash_WhileAPresenceReadIsInFlight()
    {
        // IN-FLIGHT is not "the last answer": a fresh presence read is on the
        // wire precisely because the previous set may be out of date, so the
        // column cannot keep vouching for it.
        ConnectReady();
        var vm = Vm();
        LandOnListTab(vm, [Listing(0, "P0"), Listing(1, "P1")], [Listing(0, "P0")]);
        Assert.Equal(["Enabled", "Disabled"], vm.Rows.Select(r => r.PresenceText));

        // A state write re-runs the presence op (below) — here, driven
        // directly through the surface to isolate the RENDERING.
        new ModemSurface(Radio).QueryPresetPresence();

        Assert.Equal(["—", "—"], vm.Rows.Select(r => r.PresenceText));
        DrainReads();
    }

    [Fact]
    public void AFaultedPresenceRead_KeepsThePriorAnswer_ItDoesNotBlankIt()
    {
        // Fault-preserves-prior, seen from the display: a swallowed listing
        // must never read as "nothing is enabled", and it must not throw away
        // an answer the radio really gave.
        ConnectReady();
        var vm = Vm();
        LandOnListTab(vm, [Listing(0, "P0"), Listing(1, "P1")], [Listing(0, "P0")]);

        // DE-FLAKED, clone round 12 §4. This used to wait on
        // `PendingPingCount == 0` and then assert the completion — a RACE by
        // construction: Core removes a timed-out sentinel from the queue INSIDE
        // the lock and POSTS its callback afterwards, so the queue reads empty
        // for a moment while `LastModemRead` still holds the PREVIOUS (answered)
        // completion. Under a loaded back-to-back suite run that window is wide
        // enough to lose. The fix is to wait on the thing actually being
        // asserted — THIS read's own completion record, matched by id — which
        // is deterministic rather than merely likelier. The 80 ms timeout is
        // kept (it is the subject: a swallowed listing), and the wait budget is
        // the helper's, not a sleep.
        Radio.Ssb.ModemReadTimeoutMs = 80;
        long readId = new ModemSurface(Radio).QueryPresetPresence();
        Assert.True(WaitUntil(
            () => Radio.State.LastModemRead.ReadId == readId && !Radio.State.LastModemRead.Answered,
            timeoutMs: 5_000));

        Assert.False(Radio.State.LastModemRead.Answered);
        Assert.Equal(["Enabled", "Disabled"], vm.Rows.Select(r => r.PresenceText));
    }

    [Fact]
    public void AnEnableWrite_ReRunsThePresenceRead_AFieldWriteDoesNot()
    {
        // §6's post-write contract, both halves. EN/DIS is invisible in the
        // echo, so the only way to know is to re-run the presence op; a plain
        // field write has nothing for it to say.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);

        Transport.ClearSent();
        vm.SelectedState = "EN";
        vm.StoreCommand.Execute(null);
        Assert.False(vm.HasInputError);
        Assert.Equal(0, PresenceReadCount);                  // still queued behind…
        AnswerSentinel();                                    // …the targeted re-read
        Assert.Equal(1, PresenceReadCount);
        DrainReads();

        Transport.ClearSent();
        vm.SelectedState = null;                             // a plain FIELD write
        vm.StoreCommand.Execute(null);
        Assert.False(vm.HasInputError);
        DrainReads();
        Assert.Equal(0, PresenceReadCount);
        Assert.Equal(1, TargetedReadCount);                  // …but it DID re-read the preset
    }

    // ==== ROUND 11 §6, RE-KEYED clone round 12 §4: the ALE-prompt guard ====
    // Round 11 keyed this guard on INTERLEAV. The 2026-08-18 §14 bench session
    // isolated the swallow to the **DIS token** instead: at an ALE> prompt a
    // DIS-carrying write answers as though it stored and nothing changes, while
    // an INTERLEAV-carrying write is fine. So the key moved, the wording moved
    // with it, and the pins below moved from one token to the other.

    [Fact]
    public void ADisableWriteAtAnAlePrompt_IsRefused_AndNothingIsSent()
    {
        // ROUND 13 B1 (item 7): the guard is scoped to an OPERATOR-DIRTY DIS,
        // so the fixture has to produce one. It lands on a preset the presence
        // read reports ENABLED — which prefills "EN" — and the tap to
        // "Disabled" is then a real change. (Through round 12 the prefill was
        // structurally null and a bare assignment was enough; against a
        // preset already prefilled "DIS" the same assignment is a no-op that
        // never marks the field dirty, which is the correct reading: there is
        // no disable to request.)
        var vm = Vm();
        ConnectReady();
        LandOnWithPresence(vm, 1, T39Listing, [Listing(1, "T39")]);
        Assert.Equal("EN", vm.SelectedState);                // prefilled from presence
        vm.SelectedState = "DIS";                            // the operator's tap

        Transport.InjectLine("ALE>");                        // the prompt the radio reported
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.Equal(
            "Disabling a preset is ignored at an ALE prompt — leave ALE first.",
            vm.InputError);
        Assert.Equal(ModemPresetsViewModel.AleDisableRefusal, vm.InputError);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheAlePromptGuard_StopsAtDIS_NotAtEveryWrite()
    {
        // THE WOULD-SEND MUTATION PIN. Delete the `state is false` half of the
        // guard and this passes vacuously; keep the OLD interleave key and it
        // fails — because this write carries INTERLEAV and no DIS, at the same
        // ALE prompt, and must go out.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        Assert.Equal("LO", vm.SelectedInterleave);           // the line WILL carry INTERLEAV
        // ROUND 13 B1 (item 7): this read Assert.Null through round 12. The
        // state now PREFILLS — LandOn's presence window listed nothing, so
        // preset 1 prefills "DIS" — and the pin gets STRONGER for it: an
        // auto-prefilled DIS is a REPORT, not a disable request, so it neither
        // trips the ALE guard nor reaches the wire. Both halves are asserted
        // below (no input error; no " DIS" tail on any sent line).
        Assert.Equal("DIS", vm.SelectedState);
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains(Transport.SentLines,
            l => l.Contains("INTERLEAV LO", StringComparison.Ordinal));
        Assert.DoesNotContain(Transport.SentLines, l => l.EndsWith(" DIS", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEnableWriteAtAnAlePrompt_IsNotRefused()
    {
        // The swallow is the DIS token specifically — an ENABLE carries a
        // state token too and is NOT what the bench observed being lost.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);
        vm.SelectedState = "EN";
        Transport.InjectLine("ALE>");
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains(Transport.SentLines, l => l.EndsWith(" EN", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameDisableWrite_GoesOutAtAnSsbPrompt()
    {
        // The guard is about the PROMPT, not about disabling. Without this the
        // refusal above could be satisfied by a card that never disables at all.
        var vm = Vm();
        ConnectReady();
        // ROUND 13 B1: same fixture move as the refusal pin above — an
        // ENABLED preset, so the tap to "Disabled" is an operator-dirty
        // change and the line really carries DIS.
        LandOnWithPresence(vm, 1, T39Listing, [Listing(1, "T39")]);
        vm.SelectedState = "DIS";
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains(Transport.SentLines, l => l.EndsWith(" DIS", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPresetWrite_IsIssuedAtAnSsbPrompt_ByEveryExistingCaller()
    {
        // THE BELT (§4). The guard above refuses the one captured swallow; this
        // pins the surrounding fact it depends on — the card's own writes go
        // out while the session prompt is SSB, which is where the radio
        // actually stores them. A caller that started programming from ALE
        // would be relying on the guard alone.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        LandOn(vm, 1, T39Listing);
        Transport.ClearSent();

        vm.SelectedState = "DIS";
        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError);
        Assert.Contains(Transport.SentLines, l => l.StartsWith("MODEM PRESET 1", StringComparison.Ordinal));
    }

    // ---- Gate + the round-9 deletions ---------------------------------------

    [Fact]
    public void Gate_NotReady_NothingSends()
    {
        var vm = Vm();
        vm.NameInput = "T39";
        vm.SelectedType = "39TONE";
        vm.SelectedDataMode = "ASYNC DAT";
        vm.SelectedBaud = "2400";

        Assert.False(vm.AreControlsEnabled);
        vm.StoreCommand.Execute(null);
        vm.OpenProgrammingTabCommand.Execute(null);
        vm.OpenListTabCommand.Execute(null);
        vm.PresetUpCommand.Execute(null);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void TheDeletedMembers_AreGone()
    {
        // The blue per-field displays left with the read-back row (ruling 3),
        // and the Refresh button left with the read doctrine. A binding to a
        // missing path resolves to nothing SILENTLY in MAUI, so the names
        // have to be pinned absent here as well as unbound in the markup.
        var t = typeof(ModemPresetsViewModel);
        Assert.Null(t.GetProperty("NameDisplayText"));
        Assert.Null(t.GetProperty("BaudDisplayText"));
        Assert.Null(t.GetProperty("InterleaveDisplayText"));
        Assert.Null(t.GetProperty("BaudInput"));
        Assert.Null(t.GetProperty("RefreshPresetsCommand"));
        Assert.Null(t.GetProperty("SelectedEnabled"));
        Assert.Null(t.GetProperty("EnabledChoices"));

        // Anti-vacuity: the replacements ARE there.
        Assert.NotNull(t.GetProperty("PickedRow"));
        Assert.NotNull(t.GetProperty("BaudText"));
        Assert.NotNull(t.GetProperty("SelectedState"));
        Assert.NotNull(t.GetProperty("StateChoices"));
    }

    // ==== ROUND 10 §3: the wide rows split across two lines ================

    [Fact]
    public void TheWideRows_SplitIntoTheStructuresTheBudgetAllows()
    {
        // The per-row contracts: ROUND 11 §3 makes Type 2+2+2 and Port 2+1 at
        // SegmentWidthPort; Interleave stays 3+2 at SegmentWidthWide. The
        // split is a VM fact (the markup renders one BindableLayout per slice),
        // so it is pinned here as well as structurally in the markup guard — a
        // slice that silently emptied would render a blank row and no XML scan
        // would notice which items were in it.
        var vm = Vm();

        Assert.Equal(2, vm.TypeChoicesRow1.Count);
        Assert.Equal(2, vm.TypeChoicesRow2.Count);
        Assert.Equal(2, vm.TypeChoicesRow3.Count);
        Assert.Equal(2, vm.DataModeChoicesRow1.Count);
        Assert.Single(vm.DataModeChoicesRow2);

        // Interleave is type-scoped (§6), so its rows are read under the type
        // with the most values — 39-tone's four, which is 3+1.
        vm.SelectedType = "39TONE";
        Assert.Equal(3, vm.InterleaveChoicesRow1.Count);
        Assert.Single(vm.InterleaveChoicesRow2);

        // The slices are a PARTITION of the one row, in order — not copies.
        Assert.Equal(
            vm.TypeChoices.Select(c => c.Value),
            vm.TypeChoicesRow1.Concat(vm.TypeChoicesRow2).Concat(vm.TypeChoicesRow3)
                .Select(c => c.Value));
        Assert.Equal(
            vm.InterleaveChoices.Select(c => c.Value),
            vm.InterleaveChoicesRow1.Concat(vm.InterleaveChoicesRow2).Select(c => c.Value));
        Assert.Equal(
            vm.DataModeChoices.Select(c => c.Value),
            vm.DataModeChoicesRow1.Concat(vm.DataModeChoicesRow2).Select(c => c.Value));
    }

    [Fact]
    public void APickOnASplitRow_LightsExactlyOneButton_AcrossEveryLine()
    {
        // The reason the rows are SLICES rather than built lists: a pick must
        // light one button, whichever line it is on. Independently built rows
        // would each carry their own IsActive.
        var vm = Vm();
        ConnectReady();

        Selected(vm.TypeChoices, "Serial").SelectCommand.Execute(null);   // row 3's last

        Assert.Equal("SE", vm.SelectedType);
        var lit = vm.TypeChoicesRow1.Concat(vm.TypeChoicesRow2).Concat(vm.TypeChoicesRow3)
            .Where(c => c.IsActive).ToList();
        Assert.Single(lit);
        Assert.Equal("Serial", lit[0].Value);
    }

    // ==== ROUND 10 §8: the ENRICHED read-back projection ===================
    // Fixtures, exactly the five §8 names: known 39TONE, known FSK (which MUST
    // carry Mark AND Space), optional-absent, the unmapped-mandatory THREE-CASE
    // theory, and a partially-malformed line.

    /// <summary>The session-15 capture's own shape — the one VERIFIED
    /// listing.</summary>
    [Fact]
    public void AKnown39ToneListing_ProjectsEveryMandatoryColumn_AndItsInterleave()
    {
        var row = new ModemPresetRow("1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");

        Assert.True(row.IsParsed);
        Assert.False(row.IsNotParsed);
        Assert.Equal("1", row.NumberText);
        Assert.Equal("T39", row.NameText);
        Assert.Equal("39 tone", row.TypeText);
        Assert.Equal("Data port (async)", row.DataModeText);
        Assert.Equal("2400", row.BaudText);
        Assert.Equal("Long", row.InterleaveText);

        // The round-9 TYPE MAP decides which optional cell line 2 carries.
        Assert.True(row.ShowsInterleave);
        Assert.False(row.ShowsMarkSpace);

        // CLONE ROUND 12 §9 A3: there is no listing-derived STATE cell any
        // more — the projection carries PRESENCE, which a bare construction
        // like this one has never been given, so it reads the third state.
        Assert.Equal("—", row.PresenceText);

        // …and the verbatim cell the LIST tab renders is UNCHANGED by §8.
        Assert.Equal("ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long", row.ParametersText);
    }

    /// <summary>An FSK listing — and it MUST carry Mark AND Space, because
    /// that is the whole reason line 2 is type-switched.</summary>
    [Fact]
    public void AKnownFskListing_CarriesMarkAndSpace_NotInterleave()
    {
        var row = new ModemPresetRow(
            "2 FSK1  SYNC DATA   BAUD 1200  TYPE FSKNS  MARK 1615  SPACE 1785");

        Assert.True(row.IsParsed);
        Assert.Equal("FSK narrow", row.TypeText);
        Assert.Equal("Data port (sync)", row.DataModeText);
        Assert.Equal("1200", row.BaudText);
        Assert.Equal("1615", row.MarkText);
        Assert.Equal("1785", row.SpaceText);

        Assert.True(row.ShowsMarkSpace);
        Assert.False(row.ShowsInterleave);
        Assert.Equal("—", row.InterleaveText);      // an FSK preset has none
    }

    /// <summary>OPTIONAL-ABSENT: the mandatory trio maps, nothing else is on
    /// the line. Every optional reads "—", and the row still PARSES.</summary>
    [Fact]
    public void AListingWithNoOptionals_StillParses_AndEveryOptionalReadsADash()
    {
        var row = new ModemPresetRow("3 BARE  ASYNC REMOTE   BAUD 75  TYPE SERIAL");

        Assert.True(row.IsParsed);
        Assert.Equal("Serial", row.TypeText);
        Assert.Equal("Remote port (async)", row.DataModeText);
        Assert.Equal("75", row.BaudText);

        Assert.Equal("—", row.InterleaveText);
        Assert.Equal("—", row.MarkText);
        Assert.Equal("—", row.SpaceText);
        Assert.Equal("—", row.PresenceText);        // §9 A3: presence, not a parsed state
    }

    /// <summary>The UNMAPPED-MANDATORY three-case theory: each of Type, Data
    /// mode and Baud ALONE forces the verbatim fallback. One case per column,
    /// so a parser that stopped checking one of them cannot pass by satisfying
    /// the other two.</summary>
    public static TheoryData<string, string> UnmappedMandatoryLines => new()
    {
        { "unknown Type", "4 X1  ASYNC DATA   BAUD 2400  TYPE WHATEVER  INTER long" },
        { "unknown Data mode", "4 X2  QUASI DATA   BAUD 2400  TYPE 39tone  INTER long" },
        { "unknown Baud", "4 X3  ASYNC DATA   BAUD 9999  TYPE 39tone  INTER long" },
    };

    [Theory]
    [MemberData(nameof(UnmappedMandatoryLines))]
    public void AnyUnmappedMandatoryToken_ForcesTheVerbatimFallback(string which, string listing)
    {
        var row = new ModemPresetRow(listing);

        Assert.False(row.IsParsed);
        Assert.True(row.IsNotParsed);

        // The fallback renders the radio's OWN text — nothing is guessed into
        // a typed column (the AGC precedent).
        Assert.NotEqual("—", row.ParametersText);
        Assert.Contains("BAUD", row.ParametersText, StringComparison.Ordinal);

        // …and neither optional cell claims a type map it does not have.
        Assert.False(row.ShowsInterleave && row.ShowsMarkSpace, which);
    }

    [Fact]
    public void TheThreeUnmappedCases_EachBreakADifferentColumn()
    {
        // Anti-vacuity for the theory above: the three lines must differ in
        // WHICH column fails, or the theory is one case run three times.
        Assert.Equal("—", new ModemPresetRow(
            "4 X1  ASYNC DATA   BAUD 2400  TYPE WHATEVER  INTER long").TypeText);
        Assert.Equal("—", new ModemPresetRow(
            "4 X2  QUASI DATA   BAUD 2400  TYPE 39tone  INTER long").DataModeText);
        Assert.Equal("—", new ModemPresetRow(
            "4 X3  ASYNC DATA   BAUD 9999  TYPE 39tone  INTER long").BaudText);

        // …and each of those lines still maps the OTHER two, so the failure is
        // isolated to the column named.
        Assert.Equal("Data port (async)", new ModemPresetRow(
            "4 X1  ASYNC DATA   BAUD 2400  TYPE WHATEVER  INTER long").DataModeText);
        Assert.Equal("39 tone", new ModemPresetRow(
            "4 X2  QUASI DATA   BAUD 2400  TYPE 39tone  INTER long").TypeText);
        Assert.Equal("39 tone", new ModemPresetRow(
            "4 X3  ASYNC DATA   BAUD 9999  TYPE 39tone  INTER long").TypeText);
    }

    /// <summary>A PARTIALLY-MALFORMED line: the shape breaks down mid-way.
    /// Nothing throws, the row falls back, and the verbatim text still shows
    /// whatever the radio actually said.</summary>
    [Fact]
    public void APartiallyMalformedLine_FallsBackWithoutThrowing()
    {
        var row = new ModemPresetRow("5 T39  ASYNC DATA   BAUD");

        Assert.False(row.IsParsed);
        Assert.Equal("5", row.NumberText);
        Assert.Equal("T39", row.NameText);
        Assert.Equal("ASYNC DATA   BAUD", row.ParametersText);
        Assert.Equal("—", row.BaudText);            // BAUD with no value after it
        Assert.Equal("—", row.TypeText);
        Assert.Equal("Data port (async)", row.DataModeText);
    }

    [Fact]
    public void AnUnlistedPreset_IsNotParsed_AndSaysNothingButItsNumber()
    {
        var row = ModemPresetRow.Unlisted(6);

        Assert.False(row.IsParsed);
        Assert.Equal("6", row.NumberText);
        foreach (var cell in new[]
        {
            row.NameText, row.ParametersText, row.TypeText, row.DataModeText,
            row.BaudText, row.InterleaveText, row.MarkText, row.SpaceText, row.PresenceText,
        })
            Assert.Equal("—", cell);
    }

    [Fact]
    public void ThePickedRow_CarriesTheEnrichedProjection_FromTheRadiosOwnLine()
    {
        // End to end through the real mirror: the read-back BESIDE the picker
        // is the same projection, so a listing the radio actually sent renders
        // the parsed columns.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);

        Assert.True(vm.PickedRow.IsParsed);
        Assert.Equal("39 tone", vm.PickedRow.TypeText);
        Assert.Equal("Data port (async)", vm.PickedRow.DataModeText);
        Assert.Equal("2400", vm.PickedRow.BaudText);
        Assert.Equal("Long", vm.PickedRow.InterleaveText);
        // CLONE ROUND 12 §9 A3: the read-back's state cell is PRESENCE, and
        // the editor landing now runs the presence op. LandOn drains it, and
        // the drained window listed nothing — so preset 1 has fields and is
        // NOT in a completed enabled set, which is the one captured
        // "Disabled" signal. Round 11 rendered "—" here forever.
        Assert.Equal("Disabled", vm.PickedRow.PresenceText);
    }

    [Fact]
    public void StateBeingUncaptured_IsNotAParseFailure_AndStaysVocabularyDriven()
    {
        // §8 spelled this out because it is the easy mistake: State has NO
        // listing forms, so making it mandatory would fail EVERY row the radio
        // has ever sent.
        //
        // CLONE ROUND 12 §9 A3 acts on the same fact from the other end: the
        // projection's listing-derived State cell is DELETED, because a cell
        // that can only ever read "—" is not a read.
        //
        // ROUND 13 B1 (plan §4 B1, ruling 2026-08-20) finishes the move. Round
        // 12 kept the vocabulary's StateFromListing "as the EDITOR's prefill
        // hook, still written as a lookup so a future capture is one
        // vocabulary line" — but the prefill's real source turned out to be
        // the PRESENCE store, not a future listing capture, so the hook is
        // deleted and this pin asserts its ABSENCE. What is unchanged: empty
        // listing forms, and a row without a state still parses.
        Assert.All(ModemPresetVocabulary.States, v => Assert.Empty(v.ListingForms));
        Assert.Null(typeof(ModemPresetVocabulary).GetMethod("StateFromListing"));
        Assert.True(new ModemPresetRow(T39Listing.Replace("MODEM PRESET ", "")).IsParsed);
        Assert.Null(typeof(ModemPresetRow).GetProperty("StateText"));
        Assert.NotNull(typeof(ModemPresetRow).GetProperty(nameof(ModemPresetRow.PresenceText)));
    }

    // ==== ROUND 13 B1 (item 7, owner 2026-08-19): the STATE prefill ==========
    // "Selecting a new preset to program does not highlight the current
    // Enabled/Disabled button even though the state shows in the list entry."
    // It never could: the prefill read LISTING tokens and no listing carries a
    // preset's state. It now reads the PRESENCE store — the same source the
    // read-back cell renders — and the Store line's state token is gated on the
    // OPERATOR's tap, so the wire is byte-identical to round 12's.

    [Fact]
    public void TheStatePrefill_LightsEnabled_WhenACompletedReadListedThePreset_R13B1()
    {
        var vm = Vm();
        ConnectReady();
        LandOnWithPresence(vm, 1, T39Listing, [Listing(1, "T39")]);

        Assert.Equal("EN", vm.SelectedState);                // the wire token…
        Assert.True(Selected(vm.StateChoices, "Enabled").IsActive);   // …and the lit BUTTON
        Assert.False(Selected(vm.StateChoices, "Disabled").IsActive);
        // The owner's complaint in one line: the button and the read-back cell
        // now say the same thing, because they read the same store.
        Assert.Equal("Enabled", vm.PickedRow.PresenceText);
    }

    [Fact]
    public void TheStatePrefill_LightsDisabled_WhenACompletedReadOmittedAPresetWithFields_R13B1()
    {
        // LandOn drains without naming anything in the presence window, so the
        // committed enabled set is EMPTY and preset 1 — which HAS fields — is
        // absent from it. That absence is the only captured disabled signal.
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);

        Assert.Equal("DIS", vm.SelectedState);
        Assert.True(Selected(vm.StateChoices, "Disabled").IsActive);
        Assert.False(Selected(vm.StateChoices, "Enabled").IsActive);
        Assert.Equal("Disabled", vm.PickedRow.PresenceText);
    }

    [Fact]
    public void TheStatePrefill_StaysEmpty_InBothIndeterminateCases_R13B1()
    {
        // The THIRD state, reached both ways — the honesty half. Neither case
        // may guess a value into a segment the operator would then Store.
        var vm = Vm();
        ConnectReady();

        // (a) No presence read has COMPLETED: the landing's read is still on
        // the wire, and an in-flight answer may differ from the last one.
        Pick(vm, 1);
        Transport.InjectLine(T39Listing);
        Assert.NotEqual(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);
        Assert.Null(vm.SelectedState);
        Assert.Equal("—", vm.PickedRow.PresenceText);
        Assert.DoesNotContain(vm.StateChoices, c => c.IsActive);

        // (b) A COMPLETED read that omits a preset the radio has NEVER LISTED
        // says nothing about it: absence only means "disabled" for a preset
        // that exists. Preset 5 has no fields here.
        DrainReads();                                        // presence commits (empty set)
        Assert.Equal(RadioState.PresenceState.Completed, Radio.State.ModemPresetPresence.State);
        LandOn(vm, 5);
        Assert.Null(vm.SelectedState);
        Assert.Equal("—", vm.PickedRow.PresenceText);

        // Anti-vacuity: the very same completed read DOES answer for preset 1,
        // which has fields — so "null" is about the missing fields, not about
        // a presence store that never says anything.
        LandOn(vm, 1);
        Assert.Equal("DIS", vm.SelectedState);
    }

    [Fact]
    public void Store_OmitsAPrefilledState_TheWireIsByteIdenticalToRound12_R13B1()
    {
        // Constitution §3.5. Round 12's Store carried EN/DIS exactly when
        // SelectedState was non-null, and non-null could ONLY mean "the
        // operator tapped it". The prefill breaks that identity, so the SENDING
        // surface reads the dirty flag — the fact it always meant.
        var vm = Vm();
        ConnectReady();
        LandOnWithPresence(vm, 1, T39Listing, [Listing(1, "T39")]);
        Assert.Equal("EN", vm.SelectedState);                // prefilled, NOT tapped

        Transport.ClearSent();
        vm.StoreCommand.Execute(null);
        Assert.False(vm.HasInputError, vm.InputError);

        var write = Assert.Single(
            Transport.SentLines.Where(l => l.StartsWith("MODEM PRESET ", StringComparison.Ordinal)));
        Assert.Equal("MODEM PRESET 1 NAME T39 TYPE 39TONE ASYNC DAT BAUD 2400 INTERLEAV LO", write);

        // …and the state-write's PRESENCE re-read is not bought either: §6
        // re-runs it only for a line that carried EN/DIS.
        DrainReads();
        Assert.Equal(0, PresenceReadCount);
    }

    [Fact]
    public void Store_CarriesTheStateTheOperatorTapped_AndReRunsPresence_R13B1()
    {
        // The other side of the gate, driven through the SEGMENT the operator
        // presses rather than by assigning the property — the tap is the
        // gesture that marks the field dirty.
        var vm = Vm();
        ConnectReady();
        LandOnWithPresence(vm, 1, T39Listing, [Listing(1, "T39")]);   // prefills EN
        Transport.InjectLine("SSB>");
        Transport.ClearSent();

        Selected(vm.StateChoices, "Disabled").SelectCommand.Execute(null);
        Assert.Equal("DIS", vm.SelectedState);

        vm.StoreCommand.Execute(null);
        Assert.False(vm.HasInputError, vm.InputError);

        var write = Assert.Single(
            Transport.SentLines.Where(l => l.StartsWith("MODEM PRESET ", StringComparison.Ordinal)));
        Assert.EndsWith(" DIS", write, StringComparison.Ordinal);

        DrainReads();
        Assert.Equal(1, PresenceReadCount);                  // the only source the cell has
    }

    [Fact]
    public void APrefilledDis_DoesNotTripTheAlePromptGuard_R13B1()
    {
        // The guard's scoping, stated positively. A preset that is ALREADY
        // disabled prefills "DIS"; a field write on it at an ALE prompt is not
        // a disable request, so refusing it would be the app inventing a
        // refusal the radio never made. (Its partner — a TAPPED DIS at the same
        // prompt IS refused — is ADisableWriteAtAnAlePrompt_IsRefused.)
        var vm = Vm();
        ConnectReady();
        LandOn(vm, 1, T39Listing);                           // empty enabled set → prefills DIS
        Assert.Equal("DIS", vm.SelectedState);

        Transport.InjectLine("ALE>");
        Transport.ClearSent();
        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError, vm.InputError);
        var write = Assert.Single(
            Transport.SentLines.Where(l => l.StartsWith("MODEM PRESET ", StringComparison.Ordinal)));
        Assert.DoesNotContain(" DIS", write, StringComparison.Ordinal);
    }

    private static ChoiceItem Selected(IReadOnlyList<ChoiceItem> row, string text)
        => row.Single(c => c.Value == text);

    // ========================================================================
    // CLONE-FIELD ROUND 2 F11 (owner ruling R-D, decision A-9) — THE CARD
    // FOLLOWS THE CONFIRMED MODE.
    //
    // A `HOP>` prompt owns modem presets 7-9, in a shorter line with NO type,
    // interleave or mark/space field and a three-value baud set (P5/P5b/P5c).
    // Under a confirmed HOP the card's 0-6 reads would answer INVALID MODEM
    // PRESET and its Store line would carry a TYPE the prompt refuses, so the
    // card wears one of two shapes. Under SSB and ALE it is byte-for-byte the
    // card it has always been — every pin above this block is that half.
    // ========================================================================

    /// <summary>The card landed at a confirmed <c>HOP&gt;</c> prompt on the
    /// picked preset, with its listing answered. The prompt line is injected
    /// BEFORE the connect's landing read so the card lands in the HOP scope
    /// from the start.</summary>
    private ModemPresetsViewModel HopCard(int preset = 7, string? listing = null)
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");            // the scope change re-lands the card
        DrainReads();
        Pick(vm, preset);
        Transport.InjectLine(listing ?? HopListing(preset));
        DrainReads();
        return vm;
    }

    /// <summary>The SHORT `HOP&gt;` preset line, P5's shape.</summary>
    private static string HopListing(int preset, string name = "DAT9", string mode = "ASYNC REMOTE",
        string baud = "300")
        => $"MODEM PRESET {preset} {name} {mode} BAUD {baud}   ";

    [Fact]
    public void UnderAConfirmedHop_TheWheelIsSevenToNine_AndWrapsWithinTheBand_F11()
    {
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");
        DrainReads();

        Assert.True(vm.IsHopScope);
        Assert.False(vm.IsSsbScope);
        Assert.Equal(7, vm.PickedPreset);                // the band's FIRST, not 0

        var walked = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            vm.PresetUpCommand.Execute(null);
            DrainReads();
            walked.Add(vm.PickedPreset);
        }
        Assert.Equal([8, 9, 7, 8], walked);               // wraps within 7-9

        // …and DOWN from the band's first wraps to its last, not to 6.
        Pick(vm, 7);
        vm.PresetDownCommand.Execute(null);
        Assert.Equal(9, vm.PickedPreset);
    }

    [Fact]
    public void UnderAConfirmedHop_TheOffersAreTheHopSet_AndTheTypeInterleaveMarkRowsAreGONE_F11()
    {
        var vm = HopCard(9, HopListing(9));

        // The two HOP columns are offered, in the captured words.
        Assert.Equal(["Async", "Sync"], vm.SyncChoices.Select(c => c.Value));
        Assert.Equal(["Data port", "Remote port"], vm.PortChoices.Select(c => c.Value));

        // Prefilled from the SHORT line: `9 DAT9 ASYNC REMOTE BAUD 300`.
        Assert.Equal("ASYNC", vm.SelectedSync);
        Assert.Equal("REMOTE", vm.SelectedPort);
        Assert.Equal("300", vm.SelectedBaud);

        // The SSB-only rows are ABSENT, not merely unselected: no type is
        // picked, so nothing offers interleave or mark/space at all.
        Assert.Null(vm.SelectedType);
        Assert.False(vm.ShowInterleave);
        Assert.False(vm.ShowMarkSpace);
        Assert.Empty(vm.InterleaveChoices);

        // The BAUD wheel offers exactly the three P5c values, in order, and
        // wraps within them — 600 and Voice are not reachable at all.
        var seen = new List<string?>();
        for (int i = 0; i < 4; i++)
        {
            vm.BaudUpCommand.Execute(null);
            seen.Add(vm.SelectedBaud);
        }
        Assert.Equal(["75", "150", "300", "75"], seen);
    }

    [Fact]
    public void AHopStore_SendsTheShortLine_AndReReadsThePresetTargeted_F11()
    {
        var vm = HopCard(9, HopListing(9));
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError, vm.InputError);
        // EXACTLY the line P5b applied — no TYPE, no INTERLEAV, the mode words
        // spelled out — and NO state token, because the operator tapped none
        // (the prefill is a report; the dirty gate is unchanged from the SSB
        // half).
        Assert.Equal(
            ["MODEM PRESET 9 NAME DAT9 ASYNC REMOTE BAUD 300", "MODEM PRE 9", "BAT ST"],
            Transport.SentLines);
    }

    [Fact]
    public void AHopStore_WithADIRTYState_PutsTheTokenOnItsOWNLineLAST_AndReRunsPresence_F11()
    {
        var vm = HopCard(9, HopListing(9));
        // The presence window closed EMPTY, so the state segment PREFILLED to
        // Disabled — a report, not a request, and the Store above proved it
        // carries no token. An operator TAP on Enabled is the request.
        Assert.Equal("DIS", vm.SelectedState);
        Selected(vm.StateChoices, "Enabled").SelectCommand.Execute(null);
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError, vm.InputError);
        Assert.Equal(
            [
                "MODEM PRESET 9 NAME DAT9 ASYNC REMOTE BAUD 300",
                "MODEM PRESET 9 EN",        // LAST, and on its OWN line — any field
                                            // write RE-ENABLES a disabled preset (P5b),
                                            // so the state has to follow the fields
                "MODEM PRE 9", "BAT ST",    // the targeted re-read…
            ],
            Transport.SentLines.Take(4));
        // …and the PRESENCE operation QUEUED BEHIND it, because the line carried
        // a state. The §8 single-slot queue will not dispatch it until the
        // targeted read's sentinel answers — which is the ordering, not a
        // missing send.
        Assert.Equal(0, Transport.CountSent("MODEM PRE"));
        DrainReads();
        Assert.Equal(1, Transport.CountSent("MODEM PRE"));

        // The other direction, from a DIRTY Disabled (tapped back to it): the
        // token is DIS, still last, still on its own line.
        Selected(vm.StateChoices, "Disabled").SelectCommand.Execute(null);
        DrainReads();
        Transport.ClearSent();
        vm.StoreCommand.Execute(null);
        Assert.Equal("MODEM PRESET 9 DIS", Transport.SentLines[1]);
    }

    [Fact]
    public void AHopStore_RefusesABaudTheHopPresetCannotStore_F11()
    {
        // The stale-pick belt for this shape. There is no UI path to a
        // non-vocabulary baud (the wheel offers three), so the refusal is
        // reached by the LISTING prefill — a preset a front panel left at 1200.
        var vm = HopCard(9, HopListing(9, baud: "1200"));
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);

        Assert.True(vm.HasInputError);
        Assert.Contains("not a baud a hop preset stores", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);
    }

    [Fact]
    public void AHopStore_RefusesUntilBothModeColumnsArePicked_F11()
    {
        // A preset the radio has not listed has nothing to prefill from, so
        // the two REQUIRED columns are empty and Store refuses BY NAME rather
        // than composing half a line.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");
        DrainReads();
        Pick(vm, 8);
        DrainReads();                                       // no listing answered
        vm.NameInput = "TST8";
        Transport.ClearSent();

        vm.StoreCommand.Execute(null);
        Assert.Contains("pick async or sync", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);

        Selected(vm.SyncChoices, "Sync").SelectCommand.Execute(null);
        vm.StoreCommand.Execute(null);
        Assert.Contains("pick a port", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);

        Selected(vm.PortChoices, "Data port").SelectCommand.Execute(null);
        vm.StoreCommand.Execute(null);
        Assert.Contains("pick a baud", vm.InputError, StringComparison.Ordinal);
        Assert.Empty(Transport.SentLines);

        vm.BaudUpCommand.Execute(null);                     // "—" → the offer's first, 75
        vm.StoreCommand.Execute(null);
        Assert.False(vm.HasInputError, vm.InputError);
        Assert.Equal("MODEM PRESET 8 NAME TST8 SYNC DATA BAUD 75", Transport.SentLines[0]);
    }

    [Fact]
    public void AModeChangeMidEdit_RELANDS_TheCard_AndDiscardsTheDirtyFields_F11()
    {
        // The card is open on the SSB shape, mid-edit, with typing and picks
        // the operator has made…
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        LandOn(vm, 1, T39Listing);
        vm.NameInput = "TYPD";
        Selected(vm.TypeChoices, "Serial").SelectCommand.Execute(null);
        Selected(vm.StateChoices, "Enabled").SelectCommand.Execute(null);
        Assert.Equal("SE", vm.SelectedType);
        Transport.ClearSent();

        // …and the radio moves to HOP under it.
        Transport.InjectLine("HOP>");
        DrainReads();

        Assert.True(vm.IsHopScope);
        Assert.Equal(7, vm.PickedPreset);           // the new band's FIRST
        Assert.Equal("", vm.NameInput);             // typing discarded
        Assert.Null(vm.SelectedType);               // nothing carries across scopes —
        Assert.Null(vm.SelectedState);              // a HOP row has no type to carry
        Assert.Null(vm.SelectedBaud);
        Assert.Equal("", vm.InputError);
        // …and the new scope was READ: the targeted read for its first preset.
        Assert.Contains("MODEM PRE 7", Transport.SentLines);
        Assert.DoesNotContain("MODEM PRE 1", Transport.SentLines);

        // And back again: the SSB shape returns, on ITS band's first preset.
        Transport.ClearSent();
        Transport.InjectLine("SSB>");
        DrainReads();
        Assert.False(vm.IsHopScope);
        Assert.True(vm.IsSsbScope);
        Assert.Equal(0, vm.PickedPreset);
        Assert.Contains("MODEM PRE 0", Transport.SentLines);
    }

    [Fact]
    public void TheScopeSwitch_RaisesPropertyChangedForBothHalvesOfThePair_F11()
    {
        // The markup binds BOTH — one stack per shape — so a scope change that
        // told nobody would leave both stacks as they were.
        var vm = Vm();
        ConnectReady();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        Transport.InjectLine("HOP>");
        DrainReads();

        Assert.Contains(nameof(ModemPresetsViewModel.IsHopScope), raised);
        Assert.Contains(nameof(ModemPresetsViewModel.IsSsbScope), raised);
        Assert.Contains(nameof(ModemPresetsViewModel.PickedPresetText), raised);
        Assert.Contains(nameof(ModemPresetsViewModel.SyncChoices), raised);
        Assert.Contains(nameof(ModemPresetsViewModel.PortChoices), raised);
    }

    [Fact]
    public void UnderSsbAndAle_TheCardIsUnchanged_F11()
    {
        // The OTHER half of the two-shape contract, said out loud: nothing
        // about the SSB/ALE card moved. Its band, its scope flags and its
        // landing read are what they always were, at both of its prompts.
        foreach (var prompt in new[] { "SSB>", "ALE>" })
        {
            var vm = Vm();
            Transport.InjectLine(prompt);
            Assert.False(vm.IsHopScope);
            Assert.True(vm.IsSsbScope);
            Assert.Equal(0, vm.PickedPreset);
            Assert.Equal(7, ModemPresetsViewModel.PresetCount);
        }
    }

    // ========================================================================
    // AUDIT ROUND 1, MAJOR 3 — A HOP ROW READS BACK TYPED.
    //
    // The previous round pinned the OPPOSITE and called it a recorded
    // consequence: the read-back's mandatory set was the SSB TRIO (type · port
    // · baud), a HOP row has no type, so every one of them fell to the raw
    // `Parameters` cell — "ASYNC REMOTE BAUD 300" in front of the operator,
    // which is both a narrowed deliverable and wire text on screen (I-5). The
    // mandatory set is SHAPE-SCOPED now; the SSB rule is untouched.
    // ========================================================================

    [Theory]
    [InlineData(7, "DAT7", "ASYNC REMOTE", "Async", "Remote port", "300")]
    [InlineData(8, "DAT8", "SYNC  DATA", "Sync", "Data port", "150")]
    [InlineData(9, "DAT9", "ASYNC DATA", "Async", "Data port", "75")]
    public void AHopRowReadsBackTYPED_NotAsARawCell_MAJOR3(
        int preset, string name, string mode, string sync, string port, string baud)
    {
        var vm = HopCard(preset, HopListing(preset, name, mode, baud));
        var row = vm.PickedRow;

        Assert.True(row.IsParsed, "the HOP shape is first-class, not 'unparsed'");
        Assert.False(row.IsNotParsed);
        Assert.True(row.IsHopShape);

        Assert.Equal(preset.ToString(System.Globalization.CultureInfo.InvariantCulture), row.NumberText);
        Assert.Equal(name, row.NameText);
        Assert.Equal(sync, row.SyncText);
        Assert.Equal(port, row.PortText);
        Assert.Equal(baud, row.BaudText);

        // The SSB-only cells are OFF, so nothing renders a column this row has
        // no field for — and the raw cell never appears.
        Assert.False(row.ShowsType);
        Assert.False(row.ShowsInterleave);
        Assert.False(row.ShowsMarkSpace);
        Assert.Equal("—", row.TypeText);
    }

    [Theory]
    [InlineData(true, "Enabled")]
    [InlineData(false, "Disabled")]
    public void AHopRowsSTATE_ComesFromTheHopScopedPresence_MAJOR3(bool enabled, string expected)
    {
        // Landed and settled first, then a presence window opened DELIBERATELY:
        // after the landing the queue is idle, so this dispatches immediately
        // and there is exactly one window for the listing to land in. (Riding
        // the landing's own queued read made the fixture depend on how many
        // targeted reads the picker had coalesced ahead of it.)
        var vm = HopCard(9, HopListing(9));
        Transport.ClearSent();
        new ModemSurface(Radio).QueryPresetPresence();
        Assert.Equal(1, PresenceReadCount);

        if (enabled) Transport.InjectLine(HopListing(9));    // the bulk listing names it
        AnswerSentinel();
        DrainReads();

        Assert.True(Radio.State.ModemPresetPresence.Covers(Falcon.Core.Protocol.OperatingMode.Hop));
        Assert.Equal(expected, vm.PickedRow.PresenceText);
        Assert.True(vm.PickedRow.IsParsed);
    }

    [Fact]
    public void AnSsbRowsReadBack_IsUNCHANGED_MAJOR3()
    {
        // The other half of "scope it by shape, do not loosen the SSB rule":
        // a full SSB row still parses through the TRIO…
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        LandOn(vm, 1, T39Listing);

        Assert.True(vm.PickedRow.IsParsed);
        Assert.False(vm.PickedRow.IsHopShape);
        Assert.True(vm.PickedRow.ShowsType);
        Assert.Equal("39 tone", vm.PickedRow.TypeText);
        Assert.Equal("Data port (async)", vm.PickedRow.DataModeText);

        // …and an SSB row whose TYPE token does NOT map still falls to the raw
        // cell, exactly as before. The shape rule keys on the TYPE COLUMN being
        // PRESENT, so a line that HAS one and cannot read it is not silently
        // promoted to the HOP shape.
        LandOn(vm, 2, "MODEM PRESET 2 X2  ASYNC DATA   BAUD 2400  TYPE 40tone  INTER long");
        Assert.False(vm.PickedRow.IsParsed);
        Assert.False(vm.PickedRow.IsHopShape);
        Assert.True(vm.PickedRow.IsNotParsed);
    }

    // ========================================================================
    // AUDIT ROUND 1, MAJOR 2 — THE CARD NEEDS A CONFIRMED MODE.
    // ========================================================================

    [Fact]
    public void AnUnconfirmedMode_DisablesTheCard_SendsNothing_AndRefusesStore_MAJOR2()
    {
        // Driven by hand: ConnectReady's trailing ClearSent would wipe the very
        // absence under test. Ready arrives with NO prompt line, which is the
        // real window — the connect ritual's `SH` answer carries the prompt,
        // and until it lands the app has not been told which presets exist.
        var vm = Vm();
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(Falcon.App.Core.Session.SessionPhase.Ready, Session.Phase);

        Assert.False(vm.AreControlsEnabled);
        Assert.True(vm.HasDisabledReason);
        Assert.Equal(ModemPresetsViewModel.ModeUnconfirmedReason, vm.DisabledReason);
        // NO read of any kind: not the targeted landing read, not presence.
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("MODEM PRE", StringComparison.Ordinal));

        // …and Store is refused, so a TYPE-bearing SSB line can never reach a
        // prompt the app has not been told about.
        Assert.False(vm.StoreCommand.CanExecute(null));
        vm.NameInput = "TST";
        vm.StoreCommand.Execute(null);
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("MODEM PRESET", StringComparison.Ordinal));

        // The picker cannot move either — there is no band to wrap in.
        vm.PresetUpCommand.Execute(null);
        Assert.Equal(0, vm.PickedPreset);

        // The moment the radio reports its mode, the card lands on THAT scope.
        Transport.InjectLine("HOP>");
        Assert.True(vm.AreControlsEnabled);
        Assert.Equal("", vm.DisabledReason);
        Assert.True(vm.IsHopScope);
        Assert.Equal(7, vm.PickedPreset);
        Assert.Contains("MODEM PRE 7", Transport.SentLines);
    }

    [Fact]
    public void AModeThatUNCONFIRMS_DarkensTheCardUntilReconfirmed_ThenRelands_MAJOR2()
    {
        // The reachable unconfirming event on this radio is a SESSION DROP:
        // RadioState.ResetForConnect clears the mode mirror, so the next Ready
        // has no confirmed mode until its own prompt line lands. (A `SS`/`HO`
        // mode change does NOT unconfirm the mirror today — stated rather than
        // pinned, because pinning a mechanism that does not exist would be
        // pinning a hope.)
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("HOP>");
        DrainReads();
        Assert.True(vm.IsHopScope);
        Assert.True(vm.AreControlsEnabled);

        Session.Close();
        Assert.False(vm.AreControlsEnabled);

        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(Falcon.App.Core.Session.SessionPhase.Ready, Session.Phase);

        // Ready, but the mode is unconfirmed again — dark, and silent.
        Assert.False(vm.AreControlsEnabled);
        Assert.Equal(ModemPresetsViewModel.ModeUnconfirmedReason, vm.DisabledReason);
        Transport.ClearSent();

        // Re-confirmed — and it RE-LANDS even onto the same scope it showed
        // before, because it never read while it could not name it.
        Transport.InjectLine("HOP>");
        Assert.True(vm.AreControlsEnabled);
        Assert.True(vm.IsHopScope);
        Assert.Equal(7, vm.PickedPreset);
        Assert.Contains("MODEM PRE 7", Transport.SentLines);
    }

    [Fact]
    public void TheCard_NeverIssuesAnUNSCOPEDRead_OnItsReadyArrivalEither_MAJOR1()
    {
        // AUDIT ROUND 2 asked for this half explicitly: the wheel's Ready
        // handler was issuing a null-scope presence read, and the card's path
        // had to be shown to be clean too. It is — its landing is OWED at Ready
        // and paid when the mode confirms — and this pin is what says so rather
        // than the source comment.
        var vm = Vm();
        Session.Connect(TestSettings);
        AnswerSentinel();
        AnswerSentinel();
        Assert.Equal(Falcon.App.Core.Session.SessionPhase.Ready, Session.Phase);

        // Not the targeted read, not the bulk listing, not anything.
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("MODEM", StringComparison.Ordinal));
        Assert.Null(Radio.State.ModemPresenceReadScope);

        // Every card gesture is equally silent while the band is unknown.
        vm.EnsureLoaded();
        vm.OpenListTabCommand.Execute(null);
        vm.OpenProgrammingTabCommand.Execute(null);
        Assert.DoesNotContain(Transport.SentLines,
            l => l.StartsWith("MODEM", StringComparison.Ordinal));

        // …and the moment a band is named, the reads it owes go out FOR THAT
        // BAND.
        Transport.InjectLine("HOP>");
        Assert.Contains("MODEM PRE 7", Transport.SentLines);
        Assert.DoesNotContain("MODEM PRE 0", Transport.SentLines);
    }

    [Fact]
    public void ANonCoveringCompletedPresence_LabelsNOTHING_MAJOR1()
    {
        // AUDIT ROUND 1, MAJOR 1, consumer half: `Completed` is not authority.
        // A set read at `SSB>` names the enabled 0-6 and CANNOT name 7-9, so
        // under HOP it must render the THIRD state — not "Disabled", which
        // would be a report invented out of a silence.
        var vm = Vm();
        ConnectReady();
        Transport.InjectLine("SSB>");
        LandOnWithPresence(vm, 1, T39Listing, [Listing(1, "T39")]);
        Assert.Equal("Enabled", vm.PickedRow.PresenceText);      // it really does label

        Transport.InjectLine("HOP>");
        Transport.InjectLine(HopListing(9));
        Pick(vm, 9);
        Transport.InjectLine(HopListing(9));
        // Deliberately DO NOT answer the HOP presence window: the only
        // committed set is the SSB one.
        Assert.True(Radio.State.ModemPresetPresence.Covers(Falcon.Core.Protocol.OperatingMode.Ssb)
                    || Radio.State.ModemPresetPresence.State != RadioState.PresenceState.Completed);
        Assert.False(Radio.State.ModemPresetPresence.Covers(Falcon.Core.Protocol.OperatingMode.Hop));

        Assert.Equal("—", vm.PickedRow.PresenceText);
        Assert.Null(vm.SelectedState);
    }
}

/// <summary>
/// CLONE ROUND 12 §9 A4 — the END-TO-END enable/disable test, over the DEMO
/// radio and the production stack (DemoSerialPort → SerialTransport →
/// Prc138Radio → RadioSession → ModemSurface → the card's own ViewModel).
///
/// <para><b>Why this test exists.</b> A4 is a COMPOSITE defect, not one bug:
/// the mis-keyed ALE guard could silently swallow a write's fields (P1's
/// re-key), a field write RE-ENABLES a disabled preset on this radio (P1's
/// demo fidelity), and §9 A3's missing presence on the read-back meant the
/// operator could not see the outcome either way. Each half now has its own
/// pin; NONE of them, alone or together, answers the question the owner
/// actually asked — "does enabling a preset from this card take?". That
/// question is only answerable by driving the card and reading the cell the
/// operator reads, which is what this does.</para>
///
/// <para>The rest of the card's pins run against the line-injecting transport
/// (the suite doctrine — replay, never a scripted double). This one is
/// deliberately the exception: the whole point is that nothing here is
/// scripted by the test, so a regression in the guard, in the surface, in
/// the presence store or in the projection all land in the same assertion.</para>
/// </summary>
public sealed class ModemPresetsViewModelEnableFlowTests : IDisposable
{
    private readonly DemoSerialPort _demo = new() { ResponseDelayMs = 0, TuneTerminalDelayMs = 0 };
    private readonly SerialTransport _transport;
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly ModemSurface _modem;

    /// <summary>The demo's canned DISABLED preset — absent from the bulk
    /// listing, served by the targeted read. That asymmetry is the only
    /// captured enabled/disabled signal there is.</summary>
    private const int DisabledPreset = 2;

    public ModemPresetsViewModelEnableFlowTests()
    {
        _transport = new SerialTransport(_demo) { OpenSettleMs = 0 };
        _radio = new Prc138Radio(_transport);
        _session = new RadioSession(_radio, _transport);
        _modem = new ModemSurface(_radio);
    }

    private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        Assert.True(condition(), "timed out waiting for: " + what);
    }

    /// <summary>Wait for the card's own read-back cell to settle on a value.
    /// The ASSERTION is on the OPERATOR-VISIBLE cell, not on the mirror behind
    /// it: "the write took" means the card says so.</summary>
    private static void WaitForPresence(ModemPresetsViewModel vm, string expected)
        => WaitUntil(() => vm.PickedRow.PresenceText == expected,
            $"the read-back's state cell to read \"{expected}\" (it reads \"{vm.PickedRow.PresenceText}\")");

    [Fact]
    public void ProgramThenDisableThenReEnable_IsVisibleOnTheCard_A4()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        WaitUntil(() => _session.Phase == SessionPhase.Ready, "session Ready over DEMO");

        var vm = new ModemPresetsViewModel(_modem, _session);
        while (vm.PickedPreset != DisabledPreset) vm.PresetUpCommand.Execute(null);

        // The landing (§9 A3) reads the preset's FIELDS targeted and runs the
        // bulk PRESENCE op behind it — which is what lets this cell say
        // anything at all. Round 11's read-back could only ever say "—".
        WaitForPresence(vm, "Disabled");

        // ---- PROGRAM + ENABLE, the way the operator does it ---------------
        // ROUND 13 B1: SelectedState now PREFILLS from presence — here it
        // prefills "DIS" (the cell above reads Disabled), so assigning "EN" is
        // a real change and marks the field dirty, which is what puts EN on
        // the Store line. Through round 12 the prefill was structurally null
        // and the tap was the only way the field was ever set at all.
        vm.NameInput = "DAT2";
        vm.SelectedType = "39TONE";
        vm.SelectedDataMode = "ASYNC REM";
        vm.SelectedBaud = "2400";
        vm.SelectedState = "EN";
        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError, vm.InputError);   // the ALE guard must NOT fire at SSB>
        WaitForPresence(vm, "Enabled");

        // ---- DISABLE ------------------------------------------------------
        vm.SelectedState = "DIS";
        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError, vm.InputError);
        WaitForPresence(vm, "Disabled");

        // ---- RE-ENABLE — the bench symptom, end to end --------------------
        vm.SelectedState = "EN";
        vm.StoreCommand.Execute(null);

        Assert.False(vm.HasInputError, vm.InputError);
        WaitForPresence(vm, "Enabled");
    }

    /// <summary>The anti-vacuity partner: the flow above must not pass because
    /// the cell reads "Enabled" for everything. In ONE session, WITHOUT any
    /// write, the read-back DISCRIMINATES — preset 0 (in the demo's bulk
    /// listing) reads Enabled, preset 2 (absent from it, but served by the
    /// targeted read) reads Disabled, and a preset the radio has not listed at
    /// all reads the honest third state.</summary>
    [Fact]
    public void TheReadBacksStateCell_DiscriminatesWithoutAnyWrite_A4()
    {
        _session.Connect(new PortSettings { PortName = DemoSerialPort.DemoPortName });
        WaitUntil(() => _session.Phase == SessionPhase.Ready, "session Ready over DEMO");

        var vm = new ModemPresetsViewModel(_modem, _session);
        vm.EnsureLoaded();                               // the view's Loaded — an editor landing
        WaitForPresence(vm, "Enabled");                  // preset 0, listed by the bulk

        while (vm.PickedPreset != DisabledPreset) vm.PresetUpCommand.Execute(null);
        WaitForPresence(vm, "Disabled");                 // preset 2, absent from the bulk

        // …and the third state is genuinely reachable, so "Disabled" is not
        // merely the fallback wearing a different name.
        Assert.Equal("—", ModemPresetRow.Unlisted(DisabledPreset).PresenceText);
    }

    public void Dispose()
    {
        _session.Dispose();
        _radio.Dispose();
        _transport.Dispose();
    }
}
