using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.Core.Radio;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;

namespace Falcon.App.Core.ViewModels;

/// <summary>One pickable ALE station (Messages target / LQA pickers): the
/// flat-list vocabulary (plan §3) — CAL/SE do not distinguish a net from an
/// individual, so targets are one list with the kind as a label. Selfs are
/// NEVER targets (they are this radio's own addresses).
///
/// <para>BROADCAST ROUND (plan-ale-broadcast-round.md §2): the two broadcast
/// literals ride this same type with the kind word <c>broadcast</c>. They are
/// APP FURNITURE, not book records — the radio never lists them — so they are
/// appended as a permanent TAIL rather than accumulated from the mirror
/// (invariant 2: the pinned entries never join the book's ordering).</para></summary>

public sealed class AleTargetChoice
{
    public string Address { get; }
    public string KindText { get; }
    public string Display { get; }

    internal AleTargetChoice(string address, string kindText)
    {
        Address = address;
        KindText = kindText;
        Display = $"{address}  ({kindText})";
    }
}

/// <summary>One Inbox row — a received AMD as the radio announced it
/// (Stage 9 closed 2026-08-24; the record's provenance is
/// <see cref="RxAmdMessage"/>). Delete sends <c>RXM DEL &lt;slot&gt;</c>
/// then re-lists (PROVISIONAL — the TXMSG DEL precedent).</summary>
public sealed class RxMessageRowViewModel
{
    public int Slot { get; }
    public string From { get; }
    public string Text { get; }
    /// <summary>"24-AUG-26 22:06" — the radio's own date/time words.</summary>
    public string WhenText { get; }
    public IRelayCommand DeleteCommand { get; }

    internal RxMessageRowViewModel(RxAmdMessage m, Action<RxMessageRowViewModel> delete)
    {
        Slot = m.Slot;
        From = m.From;
        Text = m.Text;
        WhenText = $"{m.Date} {m.Time}";
        DeleteCommand = new RelayCommand(() => delete(this));
    }
}

/// <summary>One sent-log row (app-side, session-scoped — the radio's stored
/// slots are ignored entirely per the owner's scratch-slot decision).</summary>
public sealed class SentMessageRowViewModel
{
    public string TimeText { get; }
    public string Target { get; }
    public string Text { get; }
    public string StatusText { get; }
    public bool IsFailed { get; }
    public bool IsPending { get; }

    internal SentMessageRowViewModel(string timeText, string target, string text,
        string statusText, bool isFailed, bool isPending)
    {
        TimeText = timeText;
        Target = target;
        Text = text;
        StatusText = statusText;
        IsFailed = isFailed;
        IsPending = isPending;
    }
}

/// <summary>
/// The Messages page (plan §4.5) — AMD compose and send via the app's
/// designated scratch slot: compose (≤90 chars, live counter, hard cap) →
/// pick a target from the flat station list (individuals + nets; selfs
/// excluded) → Send delegates to Core's verified flow (TXMSG 9 write →
/// read-back verify → SE 9 — NEVER sent unverified; Core enforces, this VM
/// surfaces the outcome). The sent log is app-side and session-scoped. The
/// radio's stored slots get no UI (owner decision 2026-08-02 — no slot
/// manager). The Inbox tab is a labeled placeholder GATED to Stage 9: the
/// RXMSG payload format is unverified until the two-station session.
///
/// <para>BROADCAST ROUND (plan-ale-broadcast-round.md §2, probes P20/P20b):
/// the target list carries a permanent TAIL — <c>ANY</c> and <c>ALL</c> — and
/// picking either reveals a CHANNEL picker beneath To. ANY REQUIRES a channel
/// (the radio answers a bare `SE 9 ANY` with ` NO CHANS IN GRP ` and transmits
/// nothing); ALL defaults to <c>Auto</c>, the bare form the radio answers by
/// choosing its own channel. The channel rides `SE`'s existing argument slot —
/// no new sender.</para>
/// </summary>
public partial class MessagesViewModel : ObservableObject
{
    public const int MaxLength = 90;

    /// <summary>Why the AMD send is withheld while the radio is on air.
    /// REWORDED 2026-08-23 (manager ruling, on the phase-5 on-air sweep): it
    /// read "A call/send is in progress — STOP (Operate → ALE) first.", which
    /// the on-air term made false on screen — a bare-STA LQA is neither a call
    /// nor a send, and it holds the wire for MINUTES (P14c). E-4 house style:
    /// the radio's situation in plain prose, naming what the operator must do,
    /// with no raw token in it.</summary>
    public const string OnAirDisabledReason =
        "The radio is on the air — stop the scan, call or LQA first.";

    // ---- The broadcast vocabulary (plan-ale-broadcast-round.md §2) ------------
    // Spelled ONCE, here, because BOTH consumers need the same words: this VM's
    // pinned compose targets and AleViewModel's pinned ANY/ALL rows (which
    // preselect through PreselectBroadcast). AleViewModel already depends on
    // this type, so the constants travel in the direction the wiring already
    // runs.

    /// <summary>The wire's broadcast-to-everyone address. `CAL ALL` / `SE 9 ALL`
    /// pick their own channel and AUTO-LINK (probe P20).</summary>
    public const string AllAddress = "ALL";

    /// <summary>The wire's broadcast-to-any-listener address. The radio REFUSES
    /// a channel-less ANY with ` NO CHANS IN GRP ` (probe P20), which is why
    /// every ANY path here requires an explicit channel.</summary>
    public const string AnyAddress = "ANY";

    /// <summary>The kind word the two pinned choices wear instead of IND/NET —
    /// they are neither, and the picker line must say so.</summary>
    public const string BroadcastKind = "broadcast";

    /// <summary>The ALL picker's first, DEFAULT entry: let the radio choose,
    /// which is the bare `SE 9 ALL` / `CAL ALL` form (P20). Not a channel — it
    /// is the absence of the channel argument, spelled for the operator.</summary>
    public const string AutoChannel = "Auto";

    /// <summary>Why Send is withheld on an ANY broadcast with no channel picked
    /// (plan §2). The radio's own refusal is named so the reason is checkable
    /// against what the operator would otherwise see on the console.</summary>
    public const string AnyNeedsChannelReason =
        "Pick a channel — an ANY broadcast needs one (the radio refuses NO CHANS IN GRP without it).";

    private readonly AleSurface _ale;
    private readonly RadioSession _session;
    private readonly TimeProvider _time;

    private sealed class LogEntry
    {
        public required string TimeText;
        public required string Target;
        public required string Text;
        public string Status = "Sending…";
        public bool Failed;
        public bool Pending = true;
    }

    private readonly List<LogEntry> _log = [];   // newest first
    private bool _sendInFlight;
    private bool _refreshing;

    /// <summary>The once-per-session Inbox landing read (the LQA-tab
    /// precedent): fired when ALE is ready with the Inbox open, reset when
    /// readiness drops so a reconnect re-reads.</summary>
    private bool _inboxReadFired;

    /// <summary>The two PINNED tail choices, built ONCE: the rebuild appends
    /// these same instances every time, so a target the operator picked
    /// survives every book refresh by identity rather than by re-matching.</summary>
    private readonly AleTargetChoice _anyTarget = new(AnyAddress, BroadcastKind);
    private readonly AleTargetChoice _allTarget = new(AllAddress, BroadcastKind);

    /// <summary>Which BROADCAST kind the current target is (<c>ANY</c>,
    /// <c>ALL</c>, or null for a book target). The KIND — not the choice — is
    /// what the channel reset watches (plan §2, critic F4): re-picking the same
    /// ANY must not throw away the channel the operator just chose.</summary>
    private string? _broadcastKind;

    [ObservableProperty] private string composeText = "";
    [ObservableProperty] private string counterText = $"0/{MaxLength}";
    [ObservableProperty] private IReadOnlyList<AleTargetChoice> targets = [];
    [ObservableProperty] private AleTargetChoice? selectedTarget;
    [ObservableProperty] private bool canSend;
    [ObservableProperty] private string sendDisabledReason = "";
    [ObservableProperty] private IReadOnlyList<SentMessageRowViewModel> sentRows = [];

    /// <summary>The channel row under To is shown for the two BROADCAST targets
    /// and hidden for every book one (plan §2): a book send takes no channel
    /// argument, so a control that did nothing would be a lie.</summary>
    [ObservableProperty] private bool isChannelPickerVisible;

    /// <summary>What the channel picker offers for the CURRENT target: the
    /// radio-reported channels for ANY, those same channels behind
    /// <see cref="AutoChannel"/> for ALL, nothing for a book target. Sourced
    /// from <see cref="AleSurface.BroadcastChannels"/> — the ONE union, so this
    /// picker and the pane's pinned rows cannot drift (plan §2).</summary>
    [ObservableProperty] private IReadOnlyList<string> composeChannelChoices = [];

    private string? _selectedComposeChannel;

    /// <summary>The picked channel. App-side INPUT state (the ComposeText
    /// precedent) — null means "none picked", which is the REQUIRED state for
    /// ANY and the reason Send is withheld there.
    ///
    /// <para>AUDIT ROUND 1, MAJOR 2 — why this is hand-written rather than an
    /// <c>[ObservableProperty]</c>: a real MAUI <c>Picker</c> CLEARS its
    /// <c>SelectedItem</c> when its <c>ItemsSource</c> is rebuilt blank or
    /// shorter, and the TwoWay binding writes that null straight in, walking
    /// past the selection-lifetime rule on exactly the reconnect this app does
    /// routinely. A person cannot UNSELECT from a Picker, so an incoming null
    /// is never an operator gesture. A write from a HIDDEN picker is refused
    /// for the same reason — the row is collapsed on a book target and has no
    /// business speaking. The app-side paths (the kind reset, the prune, the
    /// row-action prefill) go through <see cref="SetComposeChannel"/>, which
    /// this guard deliberately does not cover.</para></summary>
    public string? SelectedComposeChannel
    {
        get => _selectedComposeChannel;
        set
        {
            if (value is null || _broadcastKind is null) return;
            SetComposeChannel(value);
        }
    }

    /// <summary>The APP-SIDE write path — the one allowed to clear the pick or
    /// to set it while the row is hidden.</summary>
    private void SetComposeChannel(string? value)
    {
        if (SetProperty(ref _selectedComposeChannel, value, nameof(SelectedComposeChannel))
            && !_refreshing) Refresh();
    }
    /// <summary>UI tweaks round 3 (U1): Inbox is the DEFAULT view — it sits
    /// on the LEFT of the Messages card's Inbox|Compose strip and opens
    /// first. Its content is still the Stage-9-gated placeholder; the
    /// "AMD ▸" row action switches to Compose explicitly.</summary>
    [ObservableProperty] private bool isInboxOpen = true;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 11). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>An explicit inbox Refresh PRESS accepted while a campaign owned
    /// the wire (§4 SUPPRESSION SCOPE): the press stands and the read runs once
    /// at campaign end.</summary>
    private bool _refreshPressOwed;

    public MessagesViewModel(
        AleSurface ale, RadioSession session, TimeProvider time,
        ICampaignSignal? campaign = null)
    {
        _ale = ale;
        _session = session;
        _time = time;
        _campaign = campaign;
        // The campaign's END edge runs the recompute; Refresh settles whatever
        // is owed IF this pane can read now, and leaves it owed if it cannot.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        ale.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _log.Clear();          // sent log is session-scoped
                _sendInFlight = false;
            }
            Refresh();
        };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool AleReady => Ready && _ale.IsAleConfirmed;

    /// <summary>Hard 90-char cap: input beyond the AMD limit is clamped at
    /// the VM (the view's MaxLength is a convenience, not the guard).</summary>
    partial void OnComposeTextChanged(string value)
    {
        if (value.Length > MaxLength)
        {
            ComposeText = value[..MaxLength];
            return;                    // the re-entrant change runs Refresh
        }
        Refresh();
    }

    partial void OnSelectedTargetChanged(AleTargetChoice? value)
    {
        ApplyTargetKind(value);
        if (!_refreshing) Refresh();
    }

    /// <summary>The TARGET-CHANGE rule (plan §2, critic F4): a change of KIND
    /// resets the channel to that kind's own default — null for ANY (a channel
    /// is required, and carrying ALL's "Auto" across would send a bare `SE 9
    /// ANY`, which the radio refuses), <see cref="AutoChannel"/> for ALL, and
    /// nothing for a book target. No carry-over in either direction.</summary>
    private void ApplyTargetKind(AleTargetChoice? target)
    {
        string? kind = IsBroadcast(target) ? target!.Address : null;
        if (kind == _broadcastKind) return;
        _broadcastKind = kind;
        // The app-side path deliberately (audit round 1, MAJOR 2): the reset to
        // null is exactly the write the public setter refuses.
        SetComposeChannel(kind == AllAddress ? AutoChannel : null);
    }

    private static bool IsBroadcast(AleTargetChoice? target)
        => target is not null && target.KindText == BroadcastKind;

    /// <summary>Row-action entry point (ALE pane "AMD ▸"): preselect the
    /// station if the current book still lists it. View state only.</summary>
    public void PreselectTarget(string address)
    {
        foreach (var t in Targets)
            if (string.Equals(t.Address, address, StringComparison.OrdinalIgnoreCase))
            {
                SelectedTarget = t;
                return;
            }
    }

    /// <summary>Row-action entry point for the pane's PINNED rows (plan §2):
    /// select the matching broadcast tail choice and carry the channel the row
    /// was showing. A null channel leaves the kind's own default standing — ANY
    /// unpicked (Send stays withheld with its reason), ALL on
    /// <see cref="AutoChannel"/>. View state only; sends nothing.</summary>
    public void PreselectBroadcast(string address, string? channel)
    {
        foreach (var t in Targets)
            if (IsBroadcast(t) && string.Equals(t.Address, address, StringComparison.OrdinalIgnoreCase))
            {
                SelectedTarget = t;                                  // resets the channel by kind
                if (channel is not null) SetComposeChannel(channel); // app-side path
                return;
            }
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            // Inbox: the received-AMD mirror, one row per slot (already
            // newest-first). Rebuilt only when the content moved, so a
            // SCANNING refresh does not churn row identities.
            var rx = _ale.RxMessages;
            bool inboxMoved = rx.Count != InboxRows.Count;
            if (!inboxMoved)
                for (int i = 0; i < rx.Count; i++)
                    if (rx[i].Slot != InboxRows[i].Slot || rx[i].Text != InboxRows[i].Text
                        || rx[i].From != InboxRows[i].From
                        // Audit MINOR (2026-08-24): a same-slot/same-text
                        // re-send at a LATER time is still news — the
                        // timestamp is part of the record.
                        || $"{rx[i].Date} {rx[i].Time}" != InboxRows[i].WhenText) { inboxMoved = true; break; }
            if (inboxMoved)
                InboxRows = [.. rx.Select(m => new RxMessageRowViewModel(m, DeleteInboxRow))];
            // ONCE PER SESSION, like Operate's station-list latch: a mode
            // lap (SSB and back) does NOT re-read; only a session drop does.
            if (!Ready) { _inboxReadFired = false; _refreshPressOwed = false; }
            // …and the deferred PRESS is settled HERE too, on whatever event
            // next finds this pane readable (audit round 1). The campaign's end
            // edge is not the payment point: a campaign can end in SSB with the
            // press still owed, and this pane may only read at `ALE>`.
            PayDeferredRefreshPress(FireInboxLandingRead());

            // Flat target list: individuals + nets, selfs excluded.
            var targets = new List<AleTargetChoice>();
            foreach (var a in _ale.IndividualAddresses)
                targets.Add(new AleTargetChoice(a.Address, "IND"));
            foreach (var a in _ale.NetAddresses)
                targets.Add(new AleTargetChoice(a.Address, "NET"));
            // …then the PERMANENT tail (plan §2): the two broadcast literals,
            // pinned AFTER the book exactly as the pane pins their rows under
            // the Nets card. They are always present — an empty book still
            // offers them — so the comparison loop below sees them as ordinary
            // entries and a book change can never drop them.
            targets.Add(_anyTarget);
            targets.Add(_allTarget);

            bool changed = targets.Count != Targets.Count;
            if (!changed)
                for (int i = 0; i < targets.Count; i++)
                    if (targets[i].Address != Targets[i].Address
                        || targets[i].KindText != Targets[i].KindText)
                    { changed = true; break; }
            if (changed)
            {
                var keep = SelectedTarget;
                Targets = targets;
                SelectedTarget = keep is null ? null
                    : targets.Find(t => t.Address == keep.Address && t.KindText == keep.KindText);
            }

            CounterText = $"{ComposeText.Length}/{MaxLength}";

            RefreshChannelChoices();

            // ROUND 15 item I (F69): THE on-air term, Core's own predicate.
            // It was this file's private Calling|Sending list; a held LINK and
            // the three LQA states are on air too, and an SE queued behind a
            // minutes-long bare-STA transmission (P14c) is the case the private
            // list could not see.
            //
            // THE ONE CARVE-OUT (owner ask 2026-08-24, after the first
            // two-station contact forced an SCA before a reply could go out):
            // an established LINK accepts an AMD — manual §2.5.2.7(g), "may be
            // sent when the R/T is either linked or scanning". The send itself
            // while LINKED is UNCAPTURED (the field transcript's SE went out
            // scanning); a refusal would surface as the radio's own line.
            // Every actively-transmitting state still refuses.
            bool inCall = _ale.IsOnAir && !_ale.IsLinked;

            // An ANY broadcast with no channel picked is REFUSED on the wire
            // (` NO CHANS IN GRP `, probe P20), so the app does not offer it.
            bool anyNeedsChannel = _broadcastKind == AnyAddress && SelectedComposeChannel is null;

            CanSend = AleReady && !_sendInFlight && !inCall
                && SelectedTarget is not null
                && !anyNeedsChannel
                && ComposeText.Length is > 0 and <= MaxLength;
            SendDisabledReason =
                !Ready ? "Not connected — open Settings → Connection to connect."
                : !_ale.IsAleConfirmed ? "AMD send is ALE-domain — waiting for the radio to confirm ALE."
                : _sendInFlight ? "A send is already in progress (write → read-back verify → SE)."
                : inCall ? OnAirDisabledReason
                : SelectedTarget is null ? "Pick a target station (individuals and nets; selfs are not targets)."
                : anyNeedsChannel ? AnyNeedsChannelReason
                : ComposeText.Length == 0 ? "Compose a message (1–90 characters)."
                : "";

            SentRows = _log.ConvertAll(e => new SentMessageRowViewModel(
                e.TimeText, e.Target, e.Text, e.Status, e.Failed, e.Pending));

            SendCommand.NotifyCanExecuteChanged();
        }
        finally { _refreshing = false; }
    }

    /// <summary>The channel picker's contents and its VISIBILITY, rebuilt on
    /// the same refresh everything else is (plan §2). ANY lists the reported
    /// channels; ALL puts <see cref="AutoChannel"/> in front of them.
    ///
    /// <para>SELECTION LIFETIME (plan §3, verbatim): the pick is app-side INPUT
    /// state, INDEPENDENT of this ItemsSource. It is pruned ONLY when the group
    /// mirror is CONFIRMED-read and NON-BLANK yet lacks the picked channel; a
    /// blank rebuild — a fresh session, or the reconnect that blanks the mirror
    /// — never prunes, because "the radio has not told us yet" is not "the
    /// channel is gone". The Send guard does the gating in the meantime.</para>
    ///
    /// <para>AUDIT ROUND 1, MAJOR 1: "confirmed-read" means the WHOLE ten-slot
    /// table (<see cref="AleSurface.GroupTableFullyRead"/>), not merely a
    /// non-empty union — a partial read's union legitimately lacks channels a
    /// group nobody has read yet still carries.</para>
    ///
    /// <para>AUDIT ROUND 1, MAJOR 2: when the list actually changes the
    /// SELECTION is re-announced, so a live Picker that dropped its own
    /// SelectedItem on a blank ItemsSource re-adopts the kept value once its
    /// items return (the write back in is refused — see the setter).</para></summary>
    private void RefreshChannelChoices()
    {
        var channels = _ale.BroadcastChannels;

        IsChannelPickerVisible = _broadcastKind is not null;
        IReadOnlyList<string> choices = _broadcastKind switch
        {
            AnyAddress => channels,
            AllAddress => [AutoChannel, .. channels],
            _ => [],
        };
        if (!ComposeChannelChoices.SequenceEqual(choices))
        {
            ComposeChannelChoices = choices;
            OnPropertyChanged(nameof(SelectedComposeChannel));
        }

        if (!_ale.GroupTableFullyRead) return;                 // partial read: never prunes
        if (channels.Count == 0) return;                       // blank mirror: never prunes
        if (_selectedComposeChannel is not { } picked || picked == AutoChannel) return;
        if (!channels.Contains(picked))
            SetComposeChannel(_broadcastKind == AllAddress ? AutoChannel : null);
    }

    private bool CanExecuteSend() => CanSend;

    /// <summary>One gesture → Core's one visible short sequence (TXMSG 9 +
    /// TXMSG + sentinel, then SE 9 only after the verified read-back). The
    /// log row goes Pending now and resolves on the marshalled outcome.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteSend))]
    private void Send()
    {
        if (!CanSend || SelectedTarget is null) return;   // body repeats the guard

        // The channel rides `SE`'s existing argument slot (`SE 9 ANY 12`, probe
        // P20b). ALL on "Auto" sends the bare form and lets the radio pick
        // (P20); a book target has never taken one.
        string? channel = _broadcastKind switch
        {
            AnyAddress => SelectedComposeChannel,
            AllAddress => SelectedComposeChannel == AutoChannel ? null : SelectedComposeChannel,
            _ => null,
        };

        var entry = new LogEntry
        {
            TimeText = _time.GetLocalNow().ToString("HH:mm:ss"),
            // The log row names the channel the send actually carried — an
            // "Auto" ALL and a CH 12 ALL are different transmissions.
            Target = channel is null ? SelectedTarget.Display : $"{SelectedTarget.Display} — CH {channel}",
            Text = ComposeText,
        };
        _log.Insert(0, entry);
        _sendInFlight = true;

        _ale.SendAmd(entry.Text, SelectedTarget.Address, channel, (ok, reason) =>
        {
            entry.Pending = false;
            entry.Failed = !ok;
            entry.Status = ok
                ? "sent — read-back verified, SE 9 dispatched"
                : "FAILED — " + (reason ?? "unknown");
            _sendInFlight = false;
            if (ok) ComposeText = "";
            Refresh();
        });
        Refresh();
    }

    // ---- Inbox tab (view state; content is a labeled Stage 9 gate) ----------

    [RelayCommand]
    private void OpenInbox()
    {
        IsInboxOpen = true;
        FireInboxLandingRead();
    }

    [RelayCommand] private void OpenCompose() => IsInboxOpen = false;

    // ---- Inbox (Stage 9 closed 2026-08-24 — the received-AMD mirror) --------

    /// <summary>Received messages, newest first (slot order — the radio
    /// stores newest at 00).</summary>
    [ObservableProperty] private IReadOnlyList<RxMessageRowViewModel> inboxRows = [];

    /// <summary>Re-read the radio's received store: clear the mirror, bare
    /// <c>RXM</c> (PROVISIONAL listing shape — only the async arrival form
    /// is captured; an unrecognized listing falls to the console).</summary>
    [RelayCommand]
    private void RefreshInbox()
    {
        if (!AleReady) return;
        // D1 QUIESCE (§4 SUPPRESSION SCOPE): the press is ACCEPTED and the read
        // waits for the campaign to let go of the wire.
        if (_campaign?.CampaignActive == true) { _refreshPressOwed = true; return; }
        _ale.RefreshRxMessages();
    }

    private void DeleteInboxRow(RxMessageRowViewModel row)
    {
        if (!AleReady) return;
        _ale.RemoveReceivedMessage(row.Slot);   // RXMSG DEL n, then clear + re-list
    }

    /// <summary>Returns TRUE when this call actually put an <c>RXM</c> on the
    /// wire — the deferred press reads that answer rather than re-deriving it,
    /// so the two owed reads can never double up nor cancel each other.</summary>
    private bool FireInboxLandingRead()
    {
        if (_inboxReadFired || !AleReady || !IsInboxOpen) return false;
        // D1 QUIESCE: a clone campaign owns the wire. The latch is left UNSET,
        // so the landing stays owed until this pane is readable again.
        if (_campaign?.CampaignActive == true) return false;
        _inboxReadFired = true;
        _ale.RefreshRxMessages();
        return true;
    }

    /// <summary>Settle a deferred Refresh press, ONCE, and only while this pane
    /// can read. Left owed otherwise — the pane's own next readable moment pays
    /// it, and a session drop discards it.
    /// <para>A still-owed inbox LANDING is the same <c>RXM</c>, so the landing
    /// is given first refusal and this sends nothing behind it.</para></summary>
    private void PayDeferredRefreshPress(bool landingAlreadyRead)
    {
        if (!_refreshPressOwed || !AleReady) return;
        if (_campaign?.CampaignActive == true) return;
        _refreshPressOwed = false;
        if (!landingAlreadyRead) _ale.RefreshRxMessages();
    }
}
