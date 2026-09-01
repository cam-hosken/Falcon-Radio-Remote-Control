using System.Globalization;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.Core.Modes;

/// <summary>
/// ALE operations: scan/stop, call/disconnect, AMD send via the designated
/// scratch slot (write + read-back verify + SE), RXMSG listing, LQA report
/// read (RAN) and scheduling (EXCH/SOU), station-list queries, the nine ALE
/// settings setters (Phase R), the scan channel-group reads with their
/// one-at-a-time read queue and the bare sentinel barrier
/// (plan-ale-programming.md §4.1), and fill/address editing builders.
///
/// <para>Fill editing was "backend in, GUI out" (plan-gui-rejigger.md round
/// 4, E2). Scope amendment X8 (plan-ale-programming.md §4.2) opened the eight
/// fill builders to the app layer — through the AleSurface wrappers and the
/// single programming gate, and nowhere else; the GuiOutScopeGuardTests
/// source scan still says exactly which names may appear in which files.</para>
/// </summary>
public sealed class AleController
{
    /// <summary>The app-designated TXMSG scratch slot (owner decision
    /// 2026-08-02): all AMD sends go through slot 9; the radio's other
    /// stored slots are ignored entirely.</summary>
    public const int ScratchSlot = 9;

    private readonly Prc138Radio _radio;
    internal AleController(Prc138Radio radio) => _radio = radio;

    private RadioState State => _radio.State;

    // ---- Scan / call ------------------------------------------------------

    public void StartScan() => _radio.Send("SCA");

    /// <summary>Stop scan; also terminates an in-progress call/link/send
    /// (bench: ST → KEY OFF + SCAN STOPPED).</summary>
    public void Stop() => _radio.Send("ST");

    public void Call(string address, string? channel = null)
    {
        ValidateAddress(address);
        _radio.Send("CAL", address.ToUpperInvariant(), ValidateChannel(channel));
    }

    // ---- AMD (scratch-slot send) --------------------------------------------

    /// <summary>How long to wait for the TXMSG read-back before giving up
    /// on an AMD send. Default scales like the init watchdog.</summary>
    public int AmdVerifyTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Send an AMD: write the text to scratch slot 9, read the slot back
    /// (TXMSG listing), verify the stored text matches, and only then
    /// <c>SE 9 &lt;addr&gt;</c>. The radio silently ignores some bad writes
    /// (documented behavior class), so the send NEVER fires unverified —
    /// on mismatch/timeout an error is raised instead.
    /// <paramref name="onOutcome"/> (optional, Stage 6 — the Messages page's
    /// sent-log needs the verified outcome) runs exactly once, marshalled
    /// like every Core callback: (true, null) after SE goes out, or
    /// (false, reason) on the read-back mismatch/timeout paths. The error
    /// event still fires on failure either way.
    /// </summary>
    public void SendAmd(string text, string address, string? channel = null,
        Action<bool, string?>? onOutcome = null)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 90)
            throw new ArgumentException("AMD message must be 1-90 characters.", nameof(text));
        // A CR/LF in the free message text would inject a second wire command
        // (the TXMSG store line is CR-terminated like every other) — reject
        // control chars before anything is sent, same guard as addresses.
        RejectControlChars(text, nameof(text), "AMD message");
        ValidateAddress(address);
        // Validate the channel UP FRONT: it is only used later in the Ping
        // callback (which runs on the parse thread, after TXMSG is already on
        // the wire) — validating it there would let a poisoned channel through
        // the two TXMSG sends before throwing. Fail before ANY send instead.
        channel = ValidateChannel(channel);

        var expected = text.Trim();
        _radio.Send("TXMSG", ScratchSlot.ToString(CultureInfo.InvariantCulture), text);
        _radio.Send("TXMSG");   // listing — the parser mirrors slot 9's stored text

        _radio.Ping(answered =>
        {
            if (!answered)
            {
                Fail("AMD not sent: the radio did not answer the message read-back.");
                return;
            }

            string? stored = null;
            foreach (var m in State.Ale.TxMessages)
                if (m.Slot == ScratchSlot) { stored = m.Text; break; }

            if (stored is null || !string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase))
            {
                Fail($"AMD not sent: slot {ScratchSlot} read back '{stored ?? "(nothing)"}' instead of the composed text.");
                return;
            }

            _radio.Send("SE", ScratchSlot.ToString(CultureInfo.InvariantCulture),
                address.ToUpperInvariant(), channel);   // validated up front
            onOutcome?.Invoke(true, null);
        }, AmdVerifyTimeoutMs);

        void Fail(string reason)
        {
            RaiseError(reason);
            onOutcome?.Invoke(false, reason);
        }
    }

    /// <summary>List the received-message store (bare <c>RXMSG</c>). Stage 9
    /// CLOSED 2026-08-24: the async ARRIVAL shape is captured and mirrored
    /// (field transcript 22:06:59); the LISTING's answer shape remains
    /// PROVISIONAL — rows matching the captured header mirror, anything else
    /// surfaces raw. Callers clear first (<see cref="ForgetReceivedMessages"/>)
    /// — the upsert-by-slot mirror cannot un-say an omitted slot.</summary>
    public void QueryRxMessages() => _radio.Send("RXMSG");

    // ---- Stored TX messages (round 11 §9A, scope amendment X10) ----------
    // THE ONLY NEW BUILDERS ROUND 11 ADDS ANYWHERE (plan §10 + invariant 1).
    // They are not a new command FAMILY: `TXMSG` is the same wire family
    // SendAmd already writes (scratch slot 9) and the parser already mirrors
    // (TXMSG header + text continuation). What is new is the WHOLE STORE —
    // slots 0-9 — which the radio-cloning campaigns must read, replay and
    // delete, because ERASE spares stored messages and they would otherwise
    // diverge silently between a source and a target radio.
    //
    // **THE WHOLE FAMILY IS `ALE>`-ONLY** (CAPTURED 2026-08-18, round-11 §14;
    // docs/protocol.md's mode table): at an `SSB>` or `HOP>` prompt every one
    // of these — the listing, the targeted read, the store and the delete —
    // answers `** ERROR **`. None of the builders below positions the prompt,
    // and none may: an orchestrator that wants the store gets to `ALE>` first.
    // Recorded here as the CONTRACT because the round-11 clone campaign issues
    // its message leg at `SSB>` and therefore reads and writes NOTHING; moving
    // that leg is round-12 P2's, in the same commit that flips the demo radio
    // to `ALE>`-only answering (plan-clone-round12 §3 leg 5, §6).
    //
    // The 2026-08-02 owner "scratch slot only" decision (and the W1 named
    // skip "TXMSG DEL / whole-store editing") is CONSCIOUSLY REVERSED for the
    // clone by owner ruling R8 (cloning scope = FULL RADIO). Nothing else
    // reaches these: the app layer sees them only through the three
    // AleSurface wrappers, and GuiOutScopeGuardTests' X10 block pins which
    // files may name either side.

    /// <summary>List the stored TX message store (bare <c>TXMSG</c> — the
    /// listing the parser mirrors as <c>TXMSG nn</c> header + the text on the
    /// NEXT line, protocol.md). Purely a read; an empty store simply lists
    /// nothing, so callers bracket it with a sentinel to tell empty from
    /// swallowed.</summary>
    public void QueryTxMessages() => _radio.Send("TXMSG");

    /// <summary>Forget every stored TX message the radio has reported, sending
    /// NOTHING — the explicit clear a re-listing needs, because the mirror is
    /// UPSERT-ONLY and a listing that omits a slot cannot un-say it. Exactly
    /// the <c>ForgetStoredChannels</c> gesture, for exactly the same reason;
    /// the clone's verify read would otherwise still show slots it had just
    /// deleted.</summary>
    public void ForgetStoredMessages() => State.Ale.ClearTxMessages();

    /// <summary>Store one slot (<c>TXMSG &lt;n&gt; &lt;text&gt;</c>, 0-9;
    /// 1-90 characters, the same bound <see cref="SendAmd"/> validates).
    /// Executes even under the fill-gate trailer (protocol.md) and is
    /// verified by re-listing, never by its echo.</summary>
    public void StoreTxMessage(int slot, string text)
    {
        ValidateMessageSlot(slot);
        if (string.IsNullOrEmpty(text) || text.Length > 90)
            throw new ArgumentException("Stored message must be 1-90 characters.", nameof(text));
        // Same injection defense the AMD path uses: the store line is
        // CR-terminated, so an embedded CR/LF would emit a second command.
        RejectControlChars(text, nameof(text), "Stored message");
        _radio.Send("TXMSG", slot.ToString(CultureInfo.InvariantCulture), text);
    }

    /// <summary>Delete one stored slot (<c>TXMSG DEL &lt;n&gt;</c>, 0-9).
    /// <b>SILENT on success</b> (Stage 6 gate, 2026-08-03) — verify by
    /// re-listing, which is exactly what the clone write campaign does.</summary>
    public void DeleteTxMessage(int slot)
    {
        ValidateMessageSlot(slot);
        _radio.Send("TXMSG", "DEL", slot.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidateMessageSlot(int slot)
    {
        if (slot is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(slot), "Stored message slot is 0-9.");
    }

    // ---- Received AMDs, write side (Stage 9 closed 2026-08-24) ---------------
    // HELP PROG's `RXMsg - (none/DELete) (Message Number <0-9>) (LASt)` is the
    // authority; the TXMSG family the behavioral precedent (ALE>-only; DEL
    // silent on success, verified by re-listing). The DEL answer is
    // PROVISIONAL until a live capture graduates it.

    /// <summary>Forget every received AMD the radio has reported, sending
    /// NOTHING — the <see cref="ForgetStoredMessages"/> gesture for the
    /// received store.</summary>
    public void ForgetReceivedMessages() => State.Ale.ClearRxMessages();

    /// <summary>Delete one received slot (<c>RXMSG DEL &lt;n&gt;</c>, 0-9).
    /// PROVISIONAL: assumed SILENT on success like <c>TXMSG DEL</c> — verify
    /// by re-listing.</summary>
    public void DeleteRxMessage(int slot)
    {
        ValidateMessageSlot(slot);
        _radio.Send("RXMSG", "DEL", slot.ToString(CultureInfo.InvariantCulture));
    }

    // ---- LQA -----------------------------------------------------------------

    /// <summary>Read the stored LQA scores for an individual (RAN — passive,
    /// does NOT transmit; SOUnd/EXCHange are the gatherers).</summary>
    public void Rank(string individualAddress)
    {
        ValidateAddress(individualAddress);
        _radio.Send("RAN", individualAddress.ToUpperInvariant());
    }

    /// <summary>Schedule LQA exchanges with a station. RETRACTED 2026-08-17:
    /// schedules ARE readable — bare <c>EXCH</c> lists them
    /// (<see cref="ReadLqaSchedules"/>).</summary>
    public void StartExchange(string address, string? intervalHhMm = null, string? startHhMm = null)
    {
        ValidateAddress(address);
        _radio.Send("EXCH", "STA", address.ToUpperInvariant(),
            ValidateHhMm(intervalHhMm), ValidateHhMm(startHhMm));
    }

    public void StopExchange(string address)
    {
        ValidateAddress(address);
        _radio.Send("EXCH", "STO", address.ToUpperInvariant());
    }

    /// <summary>Schedule soundings from a SELF address (SOU takes a self —
    /// the one other address-type-restricted operational command).</summary>
    public void StartSounding(string selfAddress, string? intervalHhMm = null, string? startHhMm = null)
    {
        ValidateSelf(selfAddress);
        _radio.Send("SOU", "STA", selfAddress.ToUpperInvariant(),
            ValidateHhMm(intervalHhMm), ValidateHhMm(startHhMm));
    }

    public void StopSounding(string selfAddress)
    {
        ValidateSelf(selfAddress);
        _radio.Send("SOU", "STO", selfAddress.ToUpperInvariant());
    }

    // ---- Station list (read-only fill queries) ---------------------------------

    public void QuerySelfAddresses() => _radio.Send("SLFAD");
    public void QueryIndividualAddresses() => _radio.Send("INDAD");

    /// <summary>
    /// The net listing. BARE (<paramref name="netName"/> null) lists every net
    /// WITHOUT members — the bulk-listing trap. Naming ONE net is the
    /// TARGETED read, and the only way to read membership at all: the record
    /// line is followed by indented <c>MEMBER nn  &lt;addr&gt;</c> lines, or by
    /// the <c>NO MEMBERS PRGMD</c> marker (protocol.md; captured 2026-08-17).
    /// <para>Callers wanting the MIRROR to move want
    /// <see cref="ReadNetMembers"/>, which brackets this with the sentinel that
    /// makes the commit atomic; this raw form exists because the bare listing
    /// is one leg of the book refresh.</para>
    /// </summary>
    public void QueryNetAddresses(string? netName = null)
    {
        if (netName is null) { _radio.Send("NETAD"); return; }
        ValidateAddress(netName);
        _radio.Send("NETAD", netName.ToUpperInvariant());
    }

    // ---- Membership + LQA schedules (round 11 §8 read stores) -------------
    // Both follow the station-book pattern exactly: payload command + ONE
    // closing sentinel, an accumulator that commits ATOMICALLY when the
    // sentinel proves the radio finished, and prior state kept when it does
    // not. Neither is the AleProgrammingGate's write bracket — that is a
    // writes-only construct and is untouched here.

    /// <summary>
    /// Read ONE net's membership (<c>NETAD &lt;name&gt;</c> + sentinel). The
    /// commit REPLACES that net's rows whole, or publishes read-empty when the
    /// radio listed none. Returns the operation's read id — a request arriving
    /// while a member read is on the wire sends NOTHING and returns the pending
    /// operation's id, its name UNIONED into that operation's name set (the
    /// coalescing precedent, with names for slots).
    /// </summary>
    public long ReadNetMembers(string netName)
    {
        ValidateAddress(netName);
        long readId = State.Ale.RequestNetMembersRead(netName.ToUpperInvariant(), out var dispatchName);
        if (dispatchName is not null) DispatchMemberRead(readId, dispatchName);
        return readId;
    }

    private void DispatchMemberRead(long readId, string netName)
    {
        QueryNetAddresses(netName);
        _radio.Ping(answered => CompleteMemberRead(readId, answered), RefreshTimeoutMs);
    }

    private void CompleteMemberRead(long readId, bool answered)
    {
        // The book refresh and the targeted membership read share ONE queue
        // (both emit NETAD records, and a record says nothing about which
        // asked for it), so either completion may promote the OTHER kind.
        State.Ale.CompleteNetMembersRead(readId, answered,
            out long nextReadId, out bool dispatchBook, out var nextName);
        DispatchPromotedNetadRead(nextReadId, dispatchBook, nextName);
    }

    private void DispatchPromotedNetadRead(long nextReadId, bool dispatchBook, string? nextName)
    {
        if (dispatchBook) DispatchBookRead(nextReadId);
        else if (nextName is not null) DispatchMemberRead(nextReadId, nextName);
    }

    /// <summary>
    /// Read the queued LQA schedules (bare <c>EXCH</c> + sentinel — bare
    /// <c>SOU</c> answers the identical list, so only one builder exists).
    /// Rows arrive in the radio's own chronological order; an empty queue
    /// answers <c>NO LQA SCHEDULED</c>, which commits as read-empty.
    /// </summary>
    public long ReadLqaSchedules()
    {
        long readId = State.Ale.RequestScheduleRead(out bool dispatch);
        if (dispatch) DispatchScheduleRead(readId);
        return readId;
    }

    private void DispatchScheduleRead(long readId)
    {
        _radio.Send("EXCH");
        _radio.Ping(answered => CompleteScheduleRead(readId, answered), RefreshTimeoutMs);
    }

    private void CompleteScheduleRead(long readId, bool answered)
    {
        State.Ale.CompleteScheduleRead(readId, answered, out long nextReadId, out bool dispatchNext);
        if (dispatchNext) DispatchScheduleRead(nextReadId);
    }

    /// <summary>How long to wait for a station-list refresh to settle.</summary>
    public int RefreshTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Re-read the whole station list. Answers ACCUMULATE and are swapped in
    /// atomically when the closing sentinel proves the radio finished — if a
    /// listing query is swallowed (documented quirk) the last radio-confirmed
    /// list stays on display instead of a silently wrong empty one.
    /// <para>Returns the operation's READ ID — the same id its completion
    /// record (<see cref="AleState.LastBookRead"/>,
    /// <see cref="RadioProperty.AleBookRead"/>) carries. A request arriving
    /// while a book read is already on the wire sends NOTHING and returns the
    /// pending operation's id instead (plan-ale-programming.md §4.1: one read
    /// per store on the wire, coalescing requesters share its commit). The
    /// void→long signature change is the deliberate, whitelisted one.</para>
    /// </summary>
    public long RefreshStationList()
    {
        long readId = State.Ale.RequestBookRead(out bool dispatch);
        if (dispatch) DispatchBookRead(readId);
        return readId;
    }

    private void DispatchBookRead(long readId)
    {
        QuerySelfAddresses();
        QueryIndividualAddresses();
        QueryNetAddresses();
        _radio.Ping(answered => CompleteBookRead(readId, answered), RefreshTimeoutMs);
    }

    private void CompleteBookRead(long readId, bool answered)
    {
        State.Ale.CompleteBookRead(readId, answered,
            out long nextReadId, out bool dispatchNext, out var nextMemberName);
        DispatchPromotedNetadRead(nextReadId, dispatchNext, nextMemberName);
    }

    // ---- Scan channel groups (read side) ---------------------------------
    // There is deliberately NO public bare-CHG sender: every group read goes
    // through one of the two below, so every one carries a commit barrier.

    /// <summary>Read ONE scan channel group (<c>CHG &lt;g&gt;</c> →
    /// "CHGROUP 01 CHANS 00 01 "; an EMPTY group answers nothing at all,
    /// which the commit turns into a confirmed-empty slot). Returns the
    /// operation's read id — see <see cref="RefreshStationList"/> for the
    /// queue contract.</summary>
    public long RequestChannelGroup(int channelGroup)
    {
        ValidateChannelGroup(channelGroup);
        return RequestGroupRead([channelGroup]);
    }

    /// <summary>Read ALL ten scan channel groups (<c>CHG 0</c> … <c>CHG 9</c>
    /// + one closing sentinel — one operation over the slot set {0..9}, the
    /// same machinery a single-group read uses).</summary>
    public long RefreshChannelGroups() => RequestGroupRead([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);

    private long RequestGroupRead(int[] slots)
    {
        long readId = State.Ale.RequestGroupRead(slots, out var dispatchSlots);
        if (dispatchSlots is not null) DispatchGroupRead(readId, dispatchSlots);
        return readId;
    }

    private void DispatchGroupRead(long readId, int[] slots)
    {
        foreach (var group in slots)
            _radio.Send("CHG", group.ToString(CultureInfo.InvariantCulture));
        _radio.Ping(answered => CompleteGroupRead(readId, answered), RefreshTimeoutMs);
    }

    private void CompleteGroupRead(long readId, bool answered)
    {
        State.Ale.CompleteGroupRead(readId, answered, out long nextReadId, out var nextSlots);
        if (nextSlots is not null) DispatchGroupRead(nextReadId, nextSlots);
    }

    /// <summary>
    /// The bare sentinel barrier every refresh already uses, exposed as a
    /// standalone intent: emits ONLY the sentinel command and completes via
    /// <see cref="AleState.LastSync"/> / <see cref="RadioProperty.AleSync"/>
    /// with the returned id.
    /// <para>Sync barriers are INDEPENDENT of the two read queues — no
    /// accumulator, no coalescing, no deferral; each call enqueues its
    /// sentinel immediately, in call order. That independence is what lets
    /// the app layer bracket a programming write between two barriers
    /// atomically (plan-ale-programming.md §4.3).</para>
    /// </summary>
    public long Synchronize()
    {
        long readId = State.Ale.BeginSync();
        _radio.Ping(answered => State.Ale.CompleteSync(readId, answered), RefreshTimeoutMs);
        return readId;
    }

    // ---- ALE settings (Phase R — all bench-confirmed query+set) -------------

    /// <summary>Accept all-calls (ALL_C ON|OFF).</summary>
    public void SetAllCall(OnOff state) => _radio.Send("ALL_C", state.ToWire());

    /// <summary>Accept any-calls (ANY_C ON|OFF).</summary>
    public void SetAnyCall(OnOff state) => _radio.Send("ANY_C", state.ToWire());

    /// <summary>AMD display (AMD_D ON|OFF).</summary>
    public void SetAmdDisplay(OnOff state) => _radio.Send("AMD_D", state.ToWire());

    /// <summary>Key-to-call (KEY_T ON|OFF).</summary>
    public void SetKeyToCall(OnOff state) => _radio.Send("KEY_T", state.ToWire());

    /// <summary>Listen before transmit (LSTN ON|OFF).</summary>
    public void SetListenBeforeTx(OnOff state) => _radio.Send("LSTN", state.ToWire());

    /// <summary>Radio silence (RAD_S ON|OFF).</summary>
    public void SetRadioSilence(OnOff state) => _radio.Send("RAD_S", state.ToWire());

    /// <summary>Max channels to scan (MAXCH 0-100).</summary>
    public void SetMaxScanChannels(int channels)
    {
        if (channels < Wire.MaxScanChannelsMin || channels > Wire.MaxScanChannelsMax)
            throw new ArgumentOutOfRangeException(nameof(channels), "MAXCH is 0-100.");
        _radio.Send("MAXCH", channels.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Link timeout minutes (TIME_OU). 0 is VALID despite HELP's
    /// "1-60" — measured (session-18: "TIME_OU 0" echoes "TIME_OUT 000");
    /// the radio wins.</summary>
    public void SetLinkTimeout(int minutes)
    {
        if (minutes < Wire.LinkTimeoutMinMinutes || minutes > Wire.LinkTimeoutMaxMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutes), "TIME_OU is 0-60 minutes (0 measured valid).");
        _radio.Send("TIME_OU", minutes.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Tune time seconds (TUNE 1-60).</summary>
    public void SetTuneTime(int seconds)
    {
        if (seconds < Wire.TuneTimeMinSeconds || seconds > Wire.TuneTimeMaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "TUNE time is 1-60 seconds.");
        _radio.Send("TUNE", seconds.ToString(CultureInfo.InvariantCulture));
    }

    // ====================================================================
    // Fill / address editing — backend in, GUI OUT (plan round 4, E2).
    // This consciously amends the Stage 1 "no builders at all" pin: the
    // guard flipped from "builders must not exist" to "builders exist,
    // whitelisted; NO app-layer file references them" (RawCommand-guard
    // pattern — GuiOutScopeGuardTests). Wire syntax per protocol.md's
    // confirmed programming table.
    // ====================================================================

    /// <summary>Store a self address (SLFAD &lt;addr&gt; &lt;chgroup&gt;;
    /// 1-15 chars, PROVISIONAL — see <see cref="ValidateSelf"/>).
    /// Names are GLOBAL across kinds — a duplicate answers
    /// " ADDRESS EXISTS ". A group-0 self is the front-panel bootstrap
    /// convention and does NOT satisfy the fill gate.</summary>
    public void SetSelfAddress(string address, int channelGroup)
    {
        ValidateSelf(address);
        ValidateChannelGroup(channelGroup);
        _radio.Send("SLFAD", address.ToUpperInvariant(), channelGroup.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Store an individual (INDAD &lt;addr&gt; &lt;chgroup&gt;
    /// &lt;assoc-self&gt;). The associated self must exist — the radio
    /// answers " INV ASSOC SELF " otherwise.</summary>
    public void SetIndividualAddress(string address, int channelGroup, string associatedSelf)
        => SendAddressWrite("INDAD", address, channelGroup, associatedSelf);

    /// <summary>Store a net (NETAD &lt;addr&gt; &lt;chgroup&gt;
    /// &lt;assoc-self&gt; — same shape as INDAD; nets need no individuals).
    /// <para>Round 11 §8: the net's MEMBERSHIP mirror goes UNREAD — a net
    /// write can move what the targeted read would say (a re-created net has
    /// no members), and the display must show the third state rather than
    /// yesterday's rows.</para></summary>
    public void SetNetAddress(string address, int channelGroup, string associatedSelf)
    {
        SendAddressWrite("NETAD", address, channelGroup, associatedSelf);
        State.Ale.InvalidateNetMembers(address.ToUpperInvariant());
    }

    private void SendAddressWrite(string command, string address, int channelGroup, string associatedSelf)
    {
        ValidateAddress(address);
        ValidateChannelGroup(channelGroup);
        ValidateSelf(associatedSelf);
        _radio.Send(command, address.ToUpperInvariant(),
            channelGroup.ToString(CultureInfo.InvariantCulture), associatedSelf.ToUpperInvariant());
    }

    /// <summary>Add a member to a net (ADDM &lt;net&gt; &lt;member&gt;).
    /// SILENT on success; " INV MEMBER ADDR " if the member does not exist,
    /// " DUPLICATE MEMBER " for a repeat, " INV SELF MEMBER " for any self but
    /// the net's own. Add-only — there is no remove-member verb — but READABLE
    /// since 2026-08-17 (<see cref="ReadNetMembers"/>), which is why the write
    /// invalidates that net's membership mirror.</summary>
    public void AddNetMember(string netName, string memberAddress)
    {
        ValidateAddress(netName);
        ValidateAddress(memberAddress);
        _radio.Send("ADDM", netName.ToUpperInvariant(), memberAddress.ToUpperInvariant());
        State.Ale.InvalidateNetMembers(netName.ToUpperInvariant());
    }

    /// <summary>Delete an address (DELAD &lt;addr&gt;). Deleting a SELF is
    /// TWO-CASE (characterization campaign 2026-08-17; this REPLACES the older
    /// "a self delete always cascades" note, which the campaign DISPROVED —
    /// plan/characterization-handoff.md PRIMARY-SELF model):
    /// <list type="bullet">
    /// <item>a SECONDARY self — its individuals and nets survive and RE-POINT
    /// at the primary self; nothing is destroyed;</item>
    /// <item>the PRIMARY self (the first <c>SLFAD</c> listing row) — its nets
    /// keep their entry with a BLANK associated self and the scan gate
    /// regresses, and its individuals are ORPHANED and go INVISIBLE: they are
    /// NOT deleted (CORRECTED 2026-08-18 — the 2026-08-17 reading was wrong;
    /// docs/protocol.md PRIMARY-SELF MODEL). While the book has no primary,
    /// bulk and targeted <c>INDAD</c> both answer trailer-only while the nets'
    /// own <c>MEMBER nn</c> lines still name them; programming any new 1–3
    /// character self brings every orphan back, re-pointed at it.</item>
    /// </list>
    /// <para>Round 11 §8: deletion is GLOBAL — the address leaves EVERY net's
    /// member list and its queued LQA schedule goes with it (2026-08-17) — so
    /// ALL membership mirrors and the schedule mirror go unread.</para></summary>
    public void DeleteAddress(string address)
    {
        ValidateAddress(address);
        _radio.Send("DELAD", address.ToUpperInvariant());
        State.Ale.InvalidateAllNetMembers();
        State.Ale.InvalidateLqaSchedules();
    }

    /// <summary>Add a channel to a scan group (ADDC &lt;group&gt;
    /// &lt;chan&gt; — confirmed "ADDC 1 00"; duplicates silently ignored).</summary>
    public void AddScanChannel(int channelGroup, int channel) => SendChannelEdit("ADDC", channelGroup, channel);

    /// <summary>Remove a channel from a scan group (DELC).</summary>
    public void DeleteScanChannel(int channelGroup, int channel) => SendChannelEdit("DELC", channelGroup, channel);

    private void SendChannelEdit(string command, int channelGroup, int channel)
    {
        ValidateChannelGroup(channelGroup);
        if (channel is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(channel), "Channel is 0-99.");
        _radio.Send(command, channelGroup.ToString(CultureInfo.InvariantCulture),
            channel.ToString("D2", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Clear EVERY ALE address (ERASE — protocol.md hazard table; channel
    /// groups, stored messages, and settings survive). Silent on success —
    /// the fill gate regressing to "PRG 1-3 CHAR SLF" is the only sign.
    ///
    /// <para><b>UI tweaks round 10 §5 (owner ruling 9): the typed-token
    /// parameter is GONE.</b> This is a destructive-DATA sender, and for those
    /// "the back end does what the GUI tells it" — Core executes, and ASKING
    /// the operator is a GUI concern (the app's <c>IConfirmationPrompt</c>
    /// popup, §5's prompt table). The signature itself is reflection-pinned in
    /// CommandSurfaceTests so the token cannot creep back. The three
    /// TRANSMIT-hazard token gates (<c>SetKeyline</c> TRANSMIT /
    /// <c>SelfTest</c> / <c>VswrTest</c>) are OUT OF SCOPE and unchanged, and
    /// the wire sweep / X8 / forbidden-prefix guards are untouched: they, not
    /// a token, are the accidental-sender defence.</para>
    /// </summary>
    /// <remarks>Round 11 §8: ERASE clears addresses, membership AND the LQA
    /// schedule queue (channel groups and stored messages survive), so both
    /// round-11 mirrors go unread with it.</remarks>
    public void EraseAllAddresses()
    {
        _radio.Send("ERASE");
        State.Ale.InvalidateAllNetMembers();
        State.Ale.InvalidateLqaSchedules();
    }

    private static void ValidateChannelGroup(int channelGroup)
    {
        if (channelGroup is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(channelGroup), "Channel group is 0-9.");
    }

    // ---- Helpers ------------------------------------------------------------

    private void RaiseError(string message) => _radio.RaiseControllerError(message);

    private static void ValidateAddress(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > 15)
            throw new ArgumentException("ALE address must be 1-15 characters.", nameof(address));
        RejectControlChars(address, nameof(address), "ALE address");
    }

    /// <summary>
    /// UI tweaks round 10 (§7, owner ruling 3): the self-address bound is
    /// 1-15, the same as every other ALE address — NOT 1-3.
    ///
    /// <para><b>PROVISIONAL — the true maximum is UNKNOWN and the radio
    /// decides.</b> The 1-3 bound came from the radio's own gate line
    /// (<c>PRG 1-3 CHAR SLF</c>, docs/protocol.md), which is a FILL-GATE
    /// prompt, not a stored-length limit; no probe has ever attempted a
    /// longer self. Relaxing the client bound lets the radio answer for
    /// itself — a refusal comes back as a normal parser-routed refusal line.
    /// Bench probe PENDING (docs/bench-checklist.md §12 probe track: 4/8/15
    /// character SLFAD attempts, and whether a &gt;3-char self satisfies the
    /// fill gate). If the radio caps lower, this bound tightens to the
    /// MEASURED value; it carries no claim about gate semantics
    /// (invariant 7).</para>
    /// </summary>
    private static void ValidateSelf(string selfAddress)
    {
        if (string.IsNullOrEmpty(selfAddress) || selfAddress.Length > 15)
            throw new ArgumentException("ALE self address must be 1-15 characters.", nameof(selfAddress));
        RejectControlChars(selfAddress, nameof(selfAddress), "Self address");
    }

    /// <summary>
    /// Reject ANY control character in a free-string argument that reaches
    /// the wire. The transport is CR-terminated and the send path does NO
    /// escaping (CommandFactory joins with spaces; SerialTransport appends
    /// "\r"), so an embedded CR/LF would emit a SECOND arbitrary command —
    /// e.g. DeleteAddress("AAA\rZERO") would run DELAD AAA then ZERO. The
    /// static forbidden-prefix sweep cannot catch a runtime argument, so this
    /// is the only defense. Throws <see cref="ArgumentException"/> (the same
    /// type the length checks throw) BEFORE anything is sent.
    /// </summary>
    private static void RejectControlChars(string value, string paramName, string what)
    {
        foreach (var ch in value)
            if (char.IsControl(ch))
                throw new ArgumentException(
                    $"{what} must not contain control characters (a CR/LF would inject a second wire command).",
                    paramName);
    }

    /// <summary>An optional CAL/SE channel argument reaches the wire as a
    /// free string (bench form "01"/"05"). Must be null or 1-2 ASCII digits
    /// (00-99) — which also structurally forbids a control-char injection
    /// like "01\rZERO".</summary>
    private static string? ValidateChannel(string? channel)
    {
        if (channel is null) return null;
        if (channel.Length is < 1 or > 2 || !channel.All(char.IsAsciiDigit))
            throw new ArgumentException("Channel must be 1-2 digits (00-99).", nameof(channel));
        return channel;
    }

    private static string? ValidateHhMm(string? value)
    {
        // Shape AND range (audit round 1, F3): EXCH/SOU answer NOTHING on
        // the wire (Stage 6 gate), so client-side validation is the only
        // defense against storing a nonsense schedule.
        if (value is null) return null;
        // \A and \z (NOT ^ and $): .NET's $ matches BEFORE a trailing \n, and
        // int.Parse tolerates trailing whitespace, so "12:34\n" would pass ^$
        // and be sent (EXCH/SOU lines are CR-terminated) — breaching the
        // no-control-char invariant (\n is a documented carrier, protocol.md
        // bare-LF quirk). \z anchors the ABSOLUTE end, allowing no trailing
        // newline.
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A\d{2}:\d{2}\z"))
            throw new ArgumentException("Interval/start must be hh:mm.", nameof(value));
        int hours = int.Parse(value[..2], CultureInfo.InvariantCulture);
        int minutes = int.Parse(value[3..], CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59)
            throw new ArgumentException("Interval/start must be within 00:00-23:59.", nameof(value));
        return value;
    }
}
