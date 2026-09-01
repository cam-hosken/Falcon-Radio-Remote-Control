using System.Globalization;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.Surfaces;

/// <summary>ALE slice (Stage 6, plan §4.4/§4.5) — ONE surface for the
/// ALE-domain consumers (ALE pane, Messages page, LQA page, and the ALE
/// settings pane's programming cards): they share the station-list mirror and
/// the AleController intents, so one Q9 slice is the minimal seam. The app
/// never auto-sends SCA or ST on mode entry (recorded owner decision; the
/// radio manages its own scan).
///
/// <para><b>AMENDED — scope amendment X8</b> (plan-ale-programming.md §4.2/
/// §4.3, owner-confirmed): the Stage-6 "NO fill editing anywhere" rule is
/// REPLACED. Eight fill-editing builders left the GUI-out guard list and this
/// surface carries their wrappers, because the ALE settings pane now has the
/// two programming cards those builders exist for. Everything that made the
/// old rule safe still holds: the wrappers are the ONLY app-layer path to
/// those builders (Falcon.Core.Tests GuiOutScopeGuardTests scans for both the
/// builder and the wrapper names), every write is one visible command, no
/// write happens silently, and every write goes through
/// <see cref="Programming"/> — the one serialized gate — so the radio's own
/// refusal lines are what the operator sees. The reasons per name are
/// recorded in the guard test.</para></summary>
public sealed class AleSurface : RadioSurface
{
    public AleSurface(Prc138Radio radio)
        : base(radio,
            RadioProperty.AleLinkState, RadioProperty.AleLinkedStation,
            // Round 15 item I: a running LQA walks a channel per line without
            // moving the link state, so the banner follows THIS event.
            RadioProperty.AleLqaProgress,
            RadioProperty.AleFillState, RadioProperty.AleSelfAddresses,
            RadioProperty.AleIndividualAddresses, RadioProperty.AleNetAddresses,
            RadioProperty.AleLqaReport, RadioProperty.OperatingMode,
            // Phase R / Wave 2: the nine ALE settings the ALE SH block
            // reports, so the settings pane refreshes when any is (re)confirmed.
            RadioProperty.AleAllCall, RadioProperty.AleAnyCall,
            RadioProperty.AleAmdDisplay, RadioProperty.AleKeyToCall,
            RadioProperty.AleListenBeforeTx, RadioProperty.AleRadioSilence,
            RadioProperty.AleMaxScanChannels, RadioProperty.AleLinkTimeout,
            RadioProperty.AleTuneTime,
            // X8: the programming cards' mirror — the group table, the
            // radio's refusal lines, and the read completions. AleSync is
            // deliberately absent (see the LastSync projection): barriers
            // carry nothing a card displays, and every operation fires at
            // least two.
            RadioProperty.AleChannelGroups, RadioProperty.AleProgrammingRefusal,
            RadioProperty.AleBookRead, RadioProperty.AleGroupRead,
            // Round 11 §8: the membership and LQA-schedule mirrors, each with
            // its own sentinel-scoped completion (the same pairing the book
            // and group stores carry).
            RadioProperty.AleNetMembers, RadioProperty.AleMemberRead,
            RadioProperty.AleLqaSchedules, RadioProperty.AleScheduleRead,
            // Stage 9 closed 2026-08-24: the received-AMD mirror (async
            // arrivals + the provisional RXM listing) drives the Inbox.
            RadioProperty.AleRxMessages,
            // The heard-on-air event stream (LQA Heard-stations frame).
            RadioProperty.AleLastHeard)
        => Programming = new AleProgrammingGate(radio);

    /// <summary>The ONE programming-operation gate (X8 §4.3): both cards run
    /// their writes through it, so only one operation is ever in flight and
    /// every outcome is attributed to the write that drew it.</summary>
    public AleProgrammingGate Programming { get; }

    /// <summary>Link state from the radio's announced lines only. INVENTORY,
    /// corrected 2026-08-23 (round 15 item I): SCANNING / SCAN STOPPED /
    /// CALLING / SENDING / LINKED, plus the LQA lifecycle probe P14 captured —
    /// SOUNDING and EXCHANGE progress lines and the <c>SH</c> block's
    /// <c>LQA/SOUND</c> first line. Unconfirmed until the radio speaks — enum
    /// ordinal 0 is Scanning, the default-leak class.</summary>
    public Confirmed<AleLinkState> LinkState => Radio.State.Ale.LinkState;

    /// <summary>Station named by the last CALLING/SENDING/LINKED line.</summary>
    public string? LinkedStation => Radio.State.Ale.LinkedStation;

    /// <summary>Channel from the last CALLING/SENDING line ("CHANNEL: nn") —
    /// the only announcement of the call's channel change (Core trigger row
    /// (b) owns the SSB-side re-poll).</summary>
    public string? LinkedChannel => Radio.State.Ale.LinkedChannel;

    /// <summary>Station named by the last LQA progress line (for a sounding,
    /// the radio's own self) — its OWN slot, so a later bare <c>LINKED</c>
    /// cannot render it as the linked station. Cleared when the run ends.</summary>
    public string? LqaStation => Radio.State.Ale.LqaStation;

    /// <summary>Channel from the last LQA progress line — it walks the target's
    /// whole channel group while the run lasts.</summary>
    public string? LqaChannel => Radio.State.Ale.LqaChannel;

    /// <summary>THE on-air term (round 15 I-D2): the radio has ANNOUNCED that
    /// it is transmitting or holding a link — a call handshake or an LQA run.
    /// Every consumer that must not key the radio (Call/Scan, the AMD send,
    /// the clone campaigns, the two programming cards' writes, LQA
    /// Now/Schedule) reads this ONE property instead of its own enum list.
    /// False while UNCONFIRMED: nothing is refused on a state the radio has
    /// not reported.</summary>
    public bool IsOnAir => LinkState.IsConfirmed && LinkState.Value.IsOnAir();

    /// <summary>The established-link state alone — the AMD carve-out's term
    /// (manual §2.5.2.7(g): a transmit AMD "may be sent when the R/T is
    /// either linked or scanning"; owner ask 2026-08-24 after the first
    /// two-station contact, where the app forced an SCA before a reply
    /// could go out). Every OTHER on-air refusal keeps <see cref="IsOnAir"/>
    /// whole.</summary>
    public bool IsLinked => LinkState.IsConfirmed && LinkState.Value == AleLinkState.Linked;

    // ---- Received AMDs (Stage 9 closed 2026-08-24) --------------------------

    /// <summary>The received-AMD mirror, slot-ordered (newest first — the
    /// radio stores newest at 00).</summary>
    public IReadOnlyList<RxAmdMessage> RxMessages => Radio.State.Ale.RxMessages;

    /// <summary>Refresh the Inbox: clear the mirror, then the bare <c>RXM</c>
    /// listing (PROVISIONAL shape — HELP PROG; the async arrival form is the
    /// captured one and the parser claims only that).</summary>
    public void RefreshRxMessages()
    {
        Radio.Ale.ForgetReceivedMessages();
        Radio.Ale.QueryRxMessages();
    }

    /// <summary>Delete one received slot and re-list to verify (`RXM DEL` is
    /// assumed SILENT on success — the TXMSG DEL precedent).</summary>
    public void RemoveReceivedMessage(int slot)
    {
        Radio.Ale.DeleteRxMessage(slot);
        RefreshRxMessages();
    }

    /// <summary>The latest heard-on-air event (a NEW instance per wire line;
    /// consumers detect novelty by REFERENCE — see <see cref="AleHeard"/>).</summary>
    public AleHeard? LastHeard => Radio.State.Ale.LastHeard;

    /// <summary>Fill gate, READ-ONLY, driven only by the radio's own gate
    /// lines and the SCANNING⇒Complete inference (probe R7: IN_PROG is
    /// noise, not a fill flag).</summary>
    public Confirmed<AleFillState> FillState => Radio.State.Ale.FillState;

    // Station book (copy-on-write snapshots; a refresh commits atomically on
    // the closing sentinel — a swallowed listing keeps the last confirmed book).
    public IReadOnlyList<AleAddress> SelfAddresses => Radio.State.Ale.SelfAddresses;
    public IReadOnlyList<AleAddress> IndividualAddresses => Radio.State.Ale.IndividualAddresses;
    public IReadOnlyList<AleAddress> NetAddresses => Radio.State.Ale.NetAddresses;

    /// <summary>Stored LQA scores from the last RANK report (cleared by each
    /// new RANK header; rows accumulate from the CHAN: continuation lines).</summary>
    public IReadOnlyList<LqaScore> LqaReport => Radio.State.Ale.LqaReport;

    /// <summary>True only when the radio has CONFIRMED it is in ALE this
    /// session — the gate for ALE-domain controls.</summary>
    public bool IsAleConfirmed =>
        Radio.State.OperatingMode.IsConfirmed
        && Radio.State.OperatingMode.Value == OperatingMode.Ale;

    // ---- Intents ----------------------------------------------------------

    /// <summary>SCA. Callers gate on a CONFIRMED Complete fill: SCA is
    /// blocked by the radio until self + individual + channels exist.</summary>
    public void StartScan() => Radio.Ale.StartScan();

    /// <summary>ST — stops scan AND terminates an in-progress call/link/send
    /// (bench: KEY OFF + SCAN STOPPED), which is why the STOP button doubles
    /// as Disconnect; the banner makes the effect visible.</summary>
    public void Stop() => Radio.Ale.Stop();

    /// <summary>CAL — initiates a call (TRANSMITS the handshake). The radio
    /// announces the channel change via the CALLING line.
    /// <para>The optional channel rides the wire form's existing slot
    /// (<c>CAL ANY 12</c>): no new sender, and the broadcast literals
    /// <c>ANY</c>/<c>ALL</c> are ordinary addresses to Core. The radio REFUSES
    /// a bare `CAL ANY` with ` NO CHANS IN GRP ` (probe P20), so an ANY caller
    /// must supply one; `CAL ALL` picks its own channel (P20).</para></summary>
    public void Call(string address, string? channel = null) => Radio.Ale.Call(address, channel);

    /// <summary>AMD send via Core's verified scratch-slot flow (TXMSG 9
    /// write → read-back verify → SE 9; never sends unverified). The outcome
    /// callback runs exactly once, marshalled. The channel rides `SE`'s
    /// existing slot (`SE 9 ANY 12`, probe P20b) under the same ANY/ALL rule
    /// as <see cref="Call"/>.</summary>
    public void SendAmd(string text, string address, string? channel, Action<bool, string?>? onOutcome)
        => Radio.Ale.SendAmd(text, address, channel, onOutcome);

    /// <summary>The channel-less form every pre-broadcast caller uses —
    /// kept so the Messages page's book sends and their pins are unchanged
    /// by the widening (plan §2).</summary>
    public void SendAmd(string text, string address, Action<bool, string?> onOutcome)
        => SendAmd(text, address, null, onOutcome);

    /// <summary>RAN — passive read of STORED scores for an individual; does
    /// NOT transmit (bench session-08).</summary>
    public void RequestRank(string individualAddress) => Radio.Ale.Rank(individualAddress);

    // LQA scheduling. RETRACTED 2026-08-17: the queue IS readable (bare EXCH
    // — see RequestLqaSchedules), so the schedule display is a radio mirror,
    // not an app-side card.
    public void StartExchange(string address, string? intervalHhMm, string? startHhMm)
        => Radio.Ale.StartExchange(address, intervalHhMm, startHhMm);
    public void StopExchange(string address) => Radio.Ale.StopExchange(address);
    public void StartSounding(string selfAddress, string? intervalHhMm, string? startHhMm)
        => Radio.Ale.StartSounding(selfAddress, intervalHhMm, startHhMm);
    public void StopSounding(string selfAddress) => Radio.Ale.StopSounding(selfAddress);

    /// <summary>SLFAD + INDAD + NETAD, accumulated and committed atomically
    /// on the closing sentinel (Core RefreshStationList).</summary>
    public void RefreshStationList() => Radio.Ale.RefreshStationList();

    // ---- X8: the programming cards' mirror + intents ----------------------

    /// <summary>The ten scan channel groups; three-state per
    /// <see cref="AleChannelGroup"/> (never queried / confirmed empty /
    /// confirmed membership in the radio's order).</summary>
    public IReadOnlyList<AleChannelGroup> ChannelGroups => Radio.State.Ale.ChannelGroups;

    /// <summary>The channels a broadcast may be sent on: the DISTINCT channel
    /// numbers of every REPORTED group (a null <c>Channels</c> is "never read",
    /// not "empty"), sorted numerically and formatted <c>"00"</c> — the wire's
    /// own two-digit spelling.
    /// <para>Derived, never persisted, and the ONE source for both broadcast
    /// pickers (plan-ale-broadcast-round.md §2: the ALE pane's pinned ANY/ALL
    /// rows and the compose channel picker read THIS, so the two view-models
    /// cannot drift). Mirror-honest by construction: an unread `CHG` table
    /// yields an EMPTY list, which is what disables an ANY call — the radio
    /// refuses a channel-less ANY with ` NO CHANS IN GRP ` (probe P20), and
    /// offering the raw 0-99 range would be inventing a fact the radio never
    /// reported (owner ruling 4).</para></summary>
    public IReadOnlyList<string> BroadcastChannels =>
        ChannelGroups
            .Where(g => g.Channels is not null)
            .SelectMany(g => g.Channels!)
            .Distinct()
            .Order()
            .Select(ch => ch.ToString("00", CultureInfo.InvariantCulture))
            .ToList();

    /// <summary>Whether the WHOLE <c>CHG</c> table has been read this session:
    /// every one of the ten groups reports a non-null <c>Channels</c>. The
    /// mirror is THREE-state per slot — null is "never read", an empty list is
    /// "confirmed empty", a list is "reported" — so a table with any null in it
    /// is a PARTIAL read, not a small one.
    /// <para>AUDIT ROUND 1, MAJOR 1: this is what plan §3's selection-lifetime
    /// rule means by a CONFIRMED-read mirror, and it is the half that
    /// <see cref="BroadcastChannels"/> cannot express — a non-empty union
    /// proves only that SOME group answered. A reconnect followed by a
    /// single-group read would otherwise erase a selection that a group nobody
    /// has read yet still carries. Exposed HERE, beside the union, so both
    /// view-models share one spelling of the predicate as well as one source of
    /// the channels.</para></summary>
    public bool GroupTableFullyRead => ChannelGroups.All(g => g.Channels is not null);

    /// <summary>The last refusal line the radio sent, with its monotone
    /// session sequence. Consumers must NOT display this directly: only an
    /// <see cref="AleProgrammingGate"/> outcome attributes a refusal to a
    /// write (a bad Operate CAL lands here too).</summary>
    public AleProgrammingRefusal ProgrammingRefusal => Radio.State.Ale.ProgrammingRefusal;

    /// <summary>Completion record of the last committed station-book read —
    /// the id equals the one <see cref="RequestStationBook"/> returned.</summary>
    public AleReadCompletion LastBookRead => Radio.State.Ale.LastBookRead;

    /// <summary>Completion record of the last committed channel-group read.</summary>
    public AleReadCompletion LastGroupRead => Radio.State.Ale.LastGroupRead;

    /// <summary>Completion record of the last bare sentinel barrier
    /// (<c>AleController.Synchronize</c>) — readable here for completeness of
    /// the §4.3 projections and for diagnostics.
    /// <para><b>Deliberately NOT in the Changed-raising watched set</b>
    /// (owner ruling, audit round 1): every programming operation fires at
    /// least two barriers, and a card that re-renders on each of them would
    /// be storming on an event carrying nothing it displays. The one consumer
    /// that needs barrier COMPLETIONS is <see cref="AleProgrammingGate"/>,
    /// which subscribes to the radio's own StateChanged directly. Both facts
    /// — this projection exists, that omission is deliberate — are
    /// pinned.</para></summary>
    public AleReadCompletion LastSync => Radio.State.Ale.LastSync;

    // Read wrappers. Reads are NOT guard-scoped (X8 §4.2.4), so these may
    // share their Core builders' names; each returns the operation's read id.

    /// <summary>SLFAD + INDAD + NETAD + sentinel — the same intent
    /// <see cref="RefreshStationList"/> uses, returning the read id the
    /// programming cards need to match a completion.</summary>
    public long RequestStationBook() => Radio.Ale.RefreshStationList();

    /// <summary>CHG g + sentinel — ONE group.</summary>
    public long RequestChannelGroup(int group) => Radio.Ale.RequestChannelGroup(group);

    /// <summary>CHG 0 … CHG 9 + one sentinel — the whole table in one
    /// operation (the groups LIST tab's lazy-once load).</summary>
    public long RequestAllChannelGroups() => Radio.Ale.RefreshChannelGroups();

    // ---- Round 11 §8: the two NEW read stores -----------------------------

    /// <summary>
    /// Per-net membership, three-state per net (key absent = never read or
    /// invalidated; <c>[]</c> = read and confirmed empty, i.e. the radio's
    /// <c>NO MEMBERS PRGMD</c>; rows otherwise, in INSERTION order). Keyed
    /// case-insensitively, like the radio's own lookup.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AleNetMember>> NetMembers
        => Radio.State.Ale.NetMembers;

    /// <summary>The queued LQA schedules in the RADIO's order (chronological
    /// by next start). <c>null</c> = never read/invalidated; <c>[]</c> = read
    /// and confirmed empty (<c>NO LQA SCHEDULED</c>); rows otherwise.</summary>
    public IReadOnlyList<LqaSchedule>? LqaSchedules => Radio.State.Ale.LqaSchedules;

    /// <summary>Completion record of the last committed membership read.</summary>
    public AleReadCompletion LastMemberRead => Radio.State.Ale.LastMemberRead;

    /// <summary>Completion record of the last committed schedule read.</summary>
    public AleReadCompletion LastScheduleRead => Radio.State.Ale.LastScheduleRead;

    /// <summary>NETAD &lt;name&gt; + sentinel — ONE net's membership, committed
    /// atomically. Returns the operation's read id.</summary>
    public long RequestNetMembers(string netName) => Radio.Ale.ReadNetMembers(netName);

    /// <summary>Bare EXCH + sentinel — the whole LQA schedule queue, committed
    /// atomically. Returns the operation's read id.</summary>
    public long RequestLqaSchedules() => Radio.Ale.ReadLqaSchedules();

    // ---- Round 11 §9A (X10): the stored TX message store -------------------
    // Three more Core names became app-reachable, and no others: the whole
    // TXMSG store — slots 0-9 — because the radio CLONE must carry it (ERASE
    // spares stored messages, so a source and a target diverge silently
    // otherwise; owner ruling R8, full-radio scope). The ONLY app-layer caller
    // is the clone service; GuiOutScopeGuardTests' X10 block pins that.

    /// <summary>The stored TX messages the last <c>TXMSG</c> listing reported
    /// (slot + text). Upsert-keyed by slot in Core: a slot the listing did not
    /// mention is simply absent, which is how an EMPTY slot reads.</summary>
    public IReadOnlyList<AmdMessage> StoredMessages => Radio.State.Ale.TxMessages;

    /// <summary>Bare TXMSG — list the whole stored-message store. Purely a
    /// read; the caller brackets it with a sentinel, because an empty store
    /// answers nothing at all.</summary>
    public void RequestStoredMessages() => Radio.Ale.QueryTxMessages();

    /// <summary>Forget the reported stored messages — sends nothing. The
    /// mirror is upsert-only, so a re-listing after a slot DELETE would
    /// otherwise still show the deleted row (the ForgetReportedChannels
    /// gesture, same reason).</summary>
    public void ForgetReportedMessages() => Radio.Ale.ForgetStoredMessages();

    /// <summary>TXMSG &lt;n&gt; &lt;text&gt; — store one slot (0-9).</summary>
    public void ProgramStoredMessage(int slot, string text)
        => Radio.Ale.StoreTxMessage(slot, text);

    /// <summary>TXMSG DEL &lt;n&gt; — delete one slot (0-9). SILENT on
    /// success; the caller re-lists to verify.</summary>
    public void RemoveStoredMessage(int slot) => Radio.Ale.DeleteTxMessage(slot);

    // Write wrappers (X8 §4.3 table). Each is textually distinct from — and
    // not a substring of — every Core builder name, because the guard scan
    // follows names. NONE of these may be called outside a
    // <see cref="Programming"/> operation: the gate is what makes a refusal
    // attributable and what keeps two writes off the wire at once.

    /// <summary>SLFAD &lt;addr&gt; &lt;group&gt; — the fill ROOT.</summary>
    public void ProgramSelf(string address, int channelGroup)
        => Radio.Ale.SetSelfAddress(address, channelGroup);

    /// <summary>INDAD &lt;addr&gt; &lt;group&gt; &lt;assoc-self&gt; — the
    /// associated self must exist (" INV ASSOC SELF " otherwise).</summary>
    public void ProgramIndividual(string address, int channelGroup, string associatedSelf)
        => Radio.Ale.SetIndividualAddress(address, channelGroup, associatedSelf);

    /// <summary>NETAD &lt;addr&gt; &lt;group&gt; &lt;assoc-self&gt;.</summary>
    public void ProgramNet(string address, int channelGroup, string associatedSelf)
        => Radio.Ale.SetNetAddress(address, channelGroup, associatedSelf);

    /// <summary>ADDM &lt;net&gt; &lt;member&gt; — write-only and add-only:
    /// membership can never be read back (no query, no DELM).</summary>
    public void ProgramNetMember(string netName, string memberAddress)
        => Radio.Ale.AddNetMember(netName, memberAddress);

    /// <summary>DELAD &lt;addr&gt; — deleting a SELF is TWO-CASE
    /// (characterization campaign 2026-08-17, replacing the disproved
    /// universal-cascade note): a SECONDARY self's individuals and nets
    /// SURVIVE and re-point at the primary; deleting the PRIMARY self blanks
    /// its nets' associated self and un-satisfies the scan gate, and its
    /// individuals are ORPHANED and go INVISIBLE — NOT deleted (corrected
    /// 2026-08-18, <c>docs/protocol.md</c> PRIMARY-SELF MODEL; the 2026-08-17
    /// reading was wrong). Programming any new 1–3 character self brings every
    /// orphan back, re-pointed. The §5 delete prompts say exactly this, per
    /// row kind.</summary>
    public void RemoveAddress(string address) => Radio.Ale.DeleteAddress(address);

    /// <summary>ADDC &lt;group&gt; &lt;chan&gt; — a duplicate is silently
    /// ignored by the radio, so the closing read shows an unchanged list and
    /// the app invents no error.</summary>
    public void ProgramScanChannel(int channelGroup, int channel)
        => Radio.Ale.AddScanChannel(channelGroup, channel);

    /// <summary>DELC &lt;group&gt; &lt;chan&gt;.</summary>
    public void RemoveScanChannel(int channelGroup, int channel)
        => Radio.Ale.DeleteScanChannel(channelGroup, channel);

    /// <summary>ERASE — clears every ALE address (groups, messages and
    /// settings survive). Round 10 §5: PARAMETERLESS. Confirmation is the
    /// GUI's (the card asks through <c>IConfirmationPrompt</c> before this is
    /// ever called); Core executes what it is told.</summary>
    public void EraseAddressBook() => Radio.Ale.EraseAllAddresses();

    // ---- ALE settings (Phase R / Wave 2) --------------------------------
    // All nine are reported in the ALE SH block and are confirmed query+set
    // on the bench (protocol.md "ALE SH block"); the settings pane renders
    // from these confirmed reads and mutates through the intents below,
    // which route to the W1 AleController builders. Unconfirmed until the
    // radio reports them this session (enum/int defaults never leak → "—").

    public Confirmed<OnOff> AllCall => Radio.State.Ale.AllCall;
    public Confirmed<OnOff> AnyCall => Radio.State.Ale.AnyCall;
    public Confirmed<OnOff> AmdDisplay => Radio.State.Ale.AmdDisplay;
    public Confirmed<OnOff> KeyToCall => Radio.State.Ale.KeyToCall;
    public Confirmed<OnOff> ListenBeforeTx => Radio.State.Ale.ListenBeforeTx;
    public Confirmed<OnOff> RadioSilence => Radio.State.Ale.RadioSilence;

    /// <summary>Channels to scan (MAXCH 0-100).</summary>
    public Confirmed<int> MaxScanChannels => Radio.State.Ale.MaxScanChannels;
    /// <summary>Link timeout minutes (TIME_OUT; 0-60 — 0 measured valid,
    /// session-18).</summary>
    public Confirmed<int> LinkTimeoutMinutes => Radio.State.Ale.LinkTimeoutMinutes;
    /// <summary>Tune time seconds (TUNETIME 1-60).</summary>
    public Confirmed<int> TuneTimeSeconds => Radio.State.Ale.TuneTimeSeconds;

    public void SetAllCall(OnOff state) => Radio.Ale.SetAllCall(state);
    public void SetAnyCall(OnOff state) => Radio.Ale.SetAnyCall(state);
    public void SetAmdDisplay(OnOff state) => Radio.Ale.SetAmdDisplay(state);
    public void SetKeyToCall(OnOff state) => Radio.Ale.SetKeyToCall(state);
    public void SetListenBeforeTx(OnOff state) => Radio.Ale.SetListenBeforeTx(state);
    public void SetRadioSilence(OnOff state) => Radio.Ale.SetRadioSilence(state);
    public void SetMaxScanChannels(int channels) => Radio.Ale.SetMaxScanChannels(channels);
    public void SetLinkTimeout(int minutes) => Radio.Ale.SetLinkTimeout(minutes);
    public void SetTuneTime(int seconds) => Radio.Ale.SetTuneTime(seconds);

    /// <summary>SH — the ALE SH block carries all nine settings (the pane's
    /// lazy first load and manual Refresh; a query, visible in the Console).</summary>
    public void RequestSettings() => Radio.Show();
}
