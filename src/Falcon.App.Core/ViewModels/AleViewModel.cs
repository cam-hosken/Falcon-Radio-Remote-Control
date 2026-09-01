using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>One SELF row of the ALE Operate pane's selfs table (UI tweaks
/// round 10 §4). Replaces the round-6 one-line <c>SelfSummaryText</c>: the
/// selfs are a TABLE now (Self | Chan grp), so the projection is per-row and
/// the view formats nothing. <see cref="GroupText"/> is the CHAN GRP
/// vocabulary (owner ruling 1) — a bare two-digit group, no "grp" word and no
/// parentheses, in the same <c>"00"</c> shape the programming card's book rows
/// already use. Read-only display: the row carries no commands, because the
/// Operate pane has no fill editing (that lives on the ALE settings pane).</summary>
public sealed class AleSelfRowViewModel
{
    public string Address { get; }
    public string GroupText { get; }

    internal AleSelfRowViewModel(string address, int channelGroup)
    {
        Address = address;
        GroupText = channelGroup.ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>One station row (plan §4.4). ROUND 15 §17: the ONE flat list is
/// rendered as TWO cards — <see cref="AleViewModel.NetRows"/> above
/// <see cref="AleViewModel.StationRows"/> — but it is still ONE row type over
/// ONE mirror, and <see cref="IsNet"/> is still what gates the actions (CAL/SE
/// do not distinguish the kinds; the old two-widget split made nets
/// uncallable). LQA (RAN) is offered only on individuals — the radio's own
/// address-type restriction. Rows are rebuilt on every refresh (ChoiceItem
/// pattern; commands route to the parent's guarded flows so XAML DataTemplates
/// bind without ancestor references).
///
/// <para><see cref="KindText"/> and <see cref="GroupText"/> survive the pane's
/// column change: the CARD now says which kind a row is and the Chan-grp column
/// is gone from this pane, but both properties are read elsewhere (the bench
/// gate counts kinds; the address book owns the group vocabulary), and
/// invariant 4 keeps a display change off the model.</para></summary>
public sealed class AleStationRowViewModel
{
    public string Address { get; }
    public string KindText { get; }          // "IND" / "NET"
    public bool IsNet { get; }
    public string GroupText { get; }
    /// <summary>The row's associated self, or "—" when the radio reports none
    /// (§17: a net whose PRIMARY self was deleted comes back with a BLANK
    /// assoc — the primary-deletion artifact, docs/protocol.md — and the third
    /// state is displayed, never defaulted away).</summary>
    public string AssociatedSelfText { get; }
    public bool CanCall { get; }
    public bool CanAmd { get; }
    public bool CanLqa { get; }
    public IRelayCommand CallCommand { get; }
    public IRelayCommand AmdCommand { get; }
    public IRelayCommand LqaCommand { get; }

    internal AleStationRowViewModel(string address, bool isNet, int channelGroup,
        string? associatedSelf,
        bool canCall, bool canAmd, bool canLqa,
        Action<AleStationRowViewModel> call,
        Action<AleStationRowViewModel> amd,
        Action<AleStationRowViewModel> lqa)
    {
        Address = address;
        IsNet = isNet;
        KindText = isNet ? "NET" : "IND";
        AssociatedSelfText = string.IsNullOrWhiteSpace(associatedSelf) ? "—" : associatedSelf;
        // UI tweaks round 10 (§4, owner ruling 1): the CHAN GRP vocabulary —
        // a bare two-digit group under a "Chan grp" column heading. The old
        // "grp n" cell repeated its own header and did not pad.
        GroupText = channelGroup.ToString("00", CultureInfo.InvariantCulture);
        CanCall = canCall;
        CanAmd = canAmd;
        CanLqa = canLqa;
        CallCommand = new RelayCommand(() => call(this));
        AmdCommand = new RelayCommand(() => amd(this));
        LqaCommand = new RelayCommand(() => lqa(this));
    }
}

/// <summary>
/// The ALE pane (plan §4.4). Link BANNER — the operator's primary
/// situational fact — rendered ONLY from the radio's announced lines.
/// INVENTORY, corrected 2026-08-23 (round 15 item I, critic F75): SCANNING /
/// SCAN STOPPED / CALLING addr CHANNEL: nn / SENDING / LINKED, plus the LQA
/// lifecycle probe P14 captured — SOUNDING self CHANNEL: nn / EXCHANGE ind
/// CHANNEL: nn, and the SH block's kind-unknown LQA/SOUND, which renders the
/// prose "LQA IN PROGRESS". Unreported renders "—", never a default (enum
/// ordinal 0 is Scanning — the leak class). STOP doubles as Disconnect during
/// Calling/Linked/Sending (ST also terminates calls; there is NO call-failure
/// line within 25 s — B10 open — so Disconnect must be offered during
/// Calling); it stays STOP during an LQA, which ST also aborts. SCAN is
/// disabled-with-reason until the radio reports a COMPLETE fill (gate lines;
/// Complete inferred from SCANNING — probe R7), and while the radio is on air.
/// The app NEVER auto-sends SCA or ST on mode entry (owner decision; the radio
/// manages its own scan).
///
/// Station lists: read-only display + action rows — NO fill editing on THIS
/// pane (the selfs table beside them is read-only too; editing lives on the
/// ALE settings pane's programming cards). Round 10 §4 replaced the one-line
/// self summary with <see cref="SelfRows"/> — a real Self | Chan grp table
/// above the stations. ROUND 15 §17 splits the one flat list into
/// <see cref="NetRows"/> and <see cref="StationRows"/>, rendered as two cards
/// (Selfs · Nets · Stations) over the SAME mirror. Loaded lazily once per
/// session (SLFAD + INDAD + NETAD via the accumulate-and-commit refresh); the
/// manual Refresh is DELETED (§17 G-D1, owner) — every app-side write closes
/// with the bulk book re-read into this mirror, so the lists follow by
/// construction.
///
/// GUI-rejigger N1 (Wave 1, W4): Messages and LQA are FOLDED into the ALE
/// pane — Messages content lives on the MAIN tab (below the fill strip),
/// LQA is a SUB-TAB (IsLqaTabOpen — pure view state, sends nothing). The
/// pane binds the folded content through <see cref="Messages"/> and
/// <see cref="Lqa"/> (the same singleton VMs the standalone pages used).
/// Row actions: CALL here; AMD ▸ and LQA ▸ preselect the station on the
/// Messages/LQA ViewModels and switch the in-pane view — no navigation.
///
/// BROADCAST ROUND (plan-ale-broadcast-round.md, probes P20/P20b): the Nets
/// card gains two PINNED rows — ANY and ALL — that are app furniture rather
/// than book records (fixed position, "—" for the associated self, a caption
/// naming what they do). ANY needs an explicit channel; ALL defaults to Auto.
/// STOP gains its ONE branch: an established link takes SCA (the captured
/// terminator — ST does not end a link), everything else keeps ST.
/// </summary>
public partial class AleViewModel : ObservableObject
{
    private readonly AleSurface _ale;
    private readonly RadioSession _session;
    private readonly MessagesViewModel _messages;
    private readonly LqaViewModel _lqa;

    private bool _loadedThisSession;

    /// <summary>Whether ALE was confirmed at the previous refresh — the edge
    /// the LQA recovery landing rides (audit round 1, MAJOR-3).</summary>
    private bool _wasAleReady;

    [ObservableProperty] private string bannerText = "—";
    [ObservableProperty] private bool isBannerConfirmed;
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private bool isCalling;
    [ObservableProperty] private bool isSending;
    [ObservableProperty] private bool isLinked;

    /// <summary>An LQA is in progress (round 15 item I) — the chip's third
    /// colour state, and the flag the Scan reason names.</summary>
    [ObservableProperty] private bool isLqa;

    /// <summary>The inbound handshake (SIGNAL RECEIVED / RECEIVING CALL —
    /// field capture 2026-08-24): one flag for the chip's ok-green fill
    /// (activity is good news — owner ruling); the banner text tells the
    /// two states apart.</summary>
    [ObservableProperty] private bool isIncomingCall;

    /// <summary>Why Scan is withheld during an LQA (round 15 item I). An LQA is
    /// a minutes-long transmission the operator did not necessarily start (a
    /// queued schedule fires on its own), so the reason names the thing that
    /// ends it: STOP sends <c>ST</c>, which aborts it (P14b).</summary>
    public const string LqaInProgressReason = "LQA in progress — Stop aborts it.";

    /// <summary>Why Scan is withheld for the OTHER on-air states — a call, a
    /// send, or a held link. REWORDED 2026-08-23 (manager ruling): it read
    /// "A call/send is in progress", which the widened term made false for a
    /// held LINK, and the sentence now names the situation rather than one of
    /// its cases. (An LQA takes <see cref="LqaInProgressReason"/> instead — it
    /// is tested first, because it names a different way out.)</summary>
    public const string OnAirDisabledReason =
        "The radio is on the air — STOP terminates the call or link.";

    [ObservableProperty] private string stopButtonText = "STOP";
    [ObservableProperty] private bool canStop;
    [ObservableProperty] private bool canScan;
    [ObservableProperty] private string scanDisabledReason = "";

    /// <summary>The NETS card's rows, in the radio's listing order (§17). Split
    /// out of the one flat list so the two cards can head themselves; the LIST
    /// is one mirror still.</summary>
    [ObservableProperty] private IReadOnlyList<AleStationRowViewModel> netRows = [];

    /// <summary>The STATIONS card's rows: the INDIVIDUALS, in listing order
    /// (§17 — the nets moved to <see cref="NetRows"/> above them).</summary>
    [ObservableProperty] private IReadOnlyList<AleStationRowViewModel> stationRows = [];

    // ---- The PINNED broadcast rows (plan-ale-broadcast-round.md §2/§3) -------
    // App furniture, not book records: they render whether or not the radio has
    // reported a single address, they carry "—" for the associated self (the
    // wire takes no self argument — owner ruling 2), and they never appear in
    // Stations. Their channel lists are DERIVED from the ONE union Core
    // exposes, so they and the compose picker cannot drift.

    /// <summary>The ANY row's picker: the radio-reported channels, nothing
    /// else. Empty until a `CHG` read has landed, which is exactly what leaves
    /// CALL withheld — an ANY with no channel is refused on the wire (P20).</summary>
    [ObservableProperty] private IReadOnlyList<string> anyChannelChoices = [];

    /// <summary>The ALL row's picker: <see cref="MessagesViewModel.AutoChannel"/>
    /// first (the bare `CAL ALL` the radio answers by choosing its own channel
    /// — P20), then the same reported channels. Seeded with Auto alone: it is a
    /// pure function of <see cref="AnyChannelChoices"/>, which is what the
    /// rebuild's change check reads.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> allChannelChoices = [MessagesViewModel.AutoChannel];

    private string? _selectedAnyChannel;
    private string _selectedAllChannel = MessagesViewModel.AutoChannel;

    /// <summary>The ANY row's pick. App-side INPUT state; starts null and
    /// STAYS null until the operator chooses, because there is no honest
    /// default — the radio reports no preference and inventing one would send a
    /// call on a channel nobody asked for.
    ///
    /// <para>AUDIT ROUND 1, MAJOR 2 — why this is hand-written rather than an
    /// <c>[ObservableProperty]</c>: a real MAUI <c>Picker</c> CLEARS its
    /// <c>SelectedItem</c> when its <c>ItemsSource</c> is rebuilt blank or
    /// shorter, and the TwoWay binding then writes that null straight into the
    /// view-model — walking past the selection-lifetime rule entirely, on
    /// exactly the reconnect this app does routinely. A person cannot
    /// legitimately UNSELECT from a Picker, so an incoming null is never an
    /// operator gesture and is REFUSED. The only paths to null are this
    /// class's own prune (<see cref="RefreshBroadcastChoices"/>), which uses
    /// the private setter deliberately.</para></summary>
    public string? SelectedAnyChannel
    {
        get => _selectedAnyChannel;
        set
        {
            if (value is null) return;                 // binding-originated unselection
            SetSelectedAnyChannel(value);
        }
    }

    /// <summary>The ALL row's pick, defaulting to
    /// <see cref="MessagesViewModel.AutoChannel"/> — the captured bare form.
    /// Same null refusal as <see cref="SelectedAnyChannel"/> (audit round 1,
    /// MAJOR 2): the wire's bare form is spelled "Auto", never an absent
    /// selection, so a null arriving here is a rebuilt Picker and nothing
    /// else.</summary>
    public string SelectedAllChannel
    {
        get => _selectedAllChannel;
        set
        {
            if (value is null) return;                 // binding-originated unselection
            SetSelectedAllChannel(value);
        }
    }

    /// <summary>The APP-SIDE write path, which the null refusal above does not
    /// guard — the prune is the one caller allowed to clear the pick.</summary>
    private void SetSelectedAnyChannel(string? value)
    {
        if (SetProperty(ref _selectedAnyChannel, value, nameof(SelectedAnyChannel)))
            CallAnyCommand.NotifyCanExecuteChanged();
    }

    private void SetSelectedAllChannel(string value)
        => SetProperty(ref _selectedAllChannel, value, nameof(SelectedAllChannel));

    /// <summary>The selfs TABLE (round 10 §4) — one row per reported self,
    /// empty until the radio has answered SLFAD this session (the view renders
    /// its own "No self addresses reported yet." empty view).</summary>
    [ObservableProperty] private IReadOnlyList<AleSelfRowViewModel> selfRows = [];

    [ObservableProperty] private string fillStateText = "—";
    [ObservableProperty] private bool isFillComplete;

    [ObservableProperty] private bool areControlsEnabled;
    [ObservableProperty] private string disabledReason = "";

    /// <summary>LQA sub-tab open (N1: Messages on the main tab, LQA a
    /// sub-tab). Pure app-side view state — switching sends nothing.</summary>
    [ObservableProperty] private bool isLqaTabOpen;

    /// <summary>The folded Messages content (main tab) — the same singleton
    /// VM the standalone Messages page bound; the pane binds through here.</summary>
    public MessagesViewModel Messages => _messages;

    /// <summary>The folded LQA content (sub-tab) — same singleton VM.</summary>
    public LqaViewModel Lqa => _lqa;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 7). This pane is the field failure's worst producer: its first-ALE burst
    /// is `SLFAD`+`INDAD`+`NETAD`+sentinel then `CHG 0..9`+sentinel, and it
    /// fires on exactly the event a campaign's book leg generates. Null where
    /// there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    public AleViewModel(AleSurface ale, RadioSession session,
        MessagesViewModel messages, LqaViewModel lqa, Action<string>? navigate = null,
        ICampaignSignal? campaign = null)
    {
        // navigate is VESTIGIAL (GUI-rejigger W4 coordination ruling): row
        // actions preselect in-pane now — nothing invokes it. The parameter
        // stays only so W5's DI wiring keeps compiling; removed in Wave 2.
        _ = navigate;
        _ale = ale;
        _session = session;
        _messages = messages;
        _lqa = lqa;
        _campaign = campaign;
        ale.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready) _loadedThisSession = false;
            Refresh();
        };
        // THE ONE OWED READ (D1): both of this pane's deferral points are inside
        // Refresh, and both are left OWED by the deferral — the first-ALE latch
        // unset, the LQA-tab edge unabsorbed — so one Refresh pays them.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;
    private bool AleReady => Ready && _ale.IsAleConfirmed;

    /// <summary>The radio is ON AIR by its own announced line — a call/send
    /// handshake, a held link, or an LQA run (round 15 I-D2: the ONE predicate,
    /// <see cref="AleSurface.IsOnAir"/>). CAL/SE during any of them is unprobed
    /// wire behaviour, so the UI does not offer it. WIDENED by item I from the
    /// old private <c>Calling|Sending</c> list: a held LINK and the three LQA
    /// states are on air too, and the five consumers that each kept their own
    /// list now read the same term.</summary>
    private bool InCall => _ale.IsOnAir;

    /// <summary>An LQA is running by the radio's own line: the two progress
    /// kinds, or the kind-unknown <c>LQA/SOUND</c> the <c>SH</c> block reports
    /// when no progress line has landed yet.</summary>
    private bool IsLqaRunning
    {
        get
        {
            var s = _ale.LinkState;
            return s.IsConfirmed
                && s.Value is AleLinkState.Sounding or AleLinkState.Exchanging or AleLinkState.Lqa;
        }
    }

    private void Refresh()
    {
        // Lazy first load (once per session): the station book — three
        // queries committed atomically on the closing sentinel. Queries
        // only; the app never auto-sends SCA/ST on mode entry.
        //
        // D1 QUIESCE: a clone campaign owns the wire. The latch is left UNSET,
        // so this burst stays owed and the campaign-end handler runs it once.
        bool campaignActive = _campaign?.CampaignActive == true;
        if (AleReady && !_loadedThisSession && !campaignActive)
        {
            _loadedThisSession = true;
            _ale.RefreshStationList();
            // BROADCAST ROUND (plan §2, critic F3): the CHG mirror is populated
            // NOWHERE else on the operate path, and the pinned rows' channel
            // pickers read it. Same tab, same lazy tier, one grouped read — and
            // without it the pickers stay empty for the whole session.
            _ale.RequestAllChannelGroups();
        }

        // Round 11 §4, audit round 1 (MAJOR-3): ALE CONFIRMING while the
        // operator is ALREADY standing on the LQA sub-tab is a landing too.
        // Without it the tab they are looking at could never read — the landing
        // that put them there was refused for want of a confirmed ALE, nothing
        // retries while they stay, and §4's placeholder would present an UNREAD
        // radio queue as read-empty for the rest of the session. Fired on the
        // TRANSITION, so it costs one read and not one per refresh, and only
        // when this is the current tab — a read never fires from a tab the
        // operator is not on (round-9 doctrine).
        //
        // D1 QUIESCE, second deferral point (§4 per-producer correction): this
        // one defers by NOT ABSORBING. `_wasAleReady` is left behind while a
        // campaign owns the wire, so the campaign-end Refresh meets the SAME
        // unconsumed transition and pays the read once.
        bool aleReady = AleReady;
        if (aleReady && !_wasAleReady && IsLqaTabOpen && !campaignActive) _lqa.OnLqaTabOpened();
        if (!campaignActive) _wasAleReady = aleReady;

        var link = _ale.LinkState;
        IsBannerConfirmed = link.IsConfirmed;
        IsScanning = link.IsConfirmed && link.Value == AleLinkState.Scanning;
        IsCalling = link.IsConfirmed && link.Value == AleLinkState.Calling;
        IsSending = link.IsConfirmed && link.Value == AleLinkState.Sending;
        IsLinked = link.IsConfirmed && link.Value == AleLinkState.Linked;
        IsLqa = IsLqaRunning;
        IsIncomingCall = link.IsConfirmed && link.Value
            is AleLinkState.SignalReceived or AleLinkState.ReceivingCall;
        // COMPOSED PROSE, never the wire token verbatim (owner ruling
        // 2026-08-24, superseding the I-5 radio's-own-vocabulary precedent):
        // the banner reads like the app's other status texts. Where the
        // natural phrasing coincides with the radio's word ("Scanning"),
        // that is coincidence, not quotation — the STATE is still the only
        // thing mirrored, and the station/channel still come from the
        // radio's own line.
        BannerText = !link.IsConfirmed ? "—" : link.Value switch
        {
            AleLinkState.Scanning => "Scanning",
            AleLinkState.Stopped => "Scan stopped",
            AleLinkState.Calling => WithStation("Calling", _ale.LinkedStation, _ale.LinkedChannel),
            AleLinkState.Sending => WithStation("Sending to", _ale.LinkedStation, _ale.LinkedChannel),
            // The link's channel shows too (owner 2026-08-24): the LINKED
            // line can carry it (Phase A), and without one the slot still
            // holds the call's own channel — the link's, either way.
            AleLinkState.Linked => _ale.LinkedStation is null ? "Linked"
                : WithStation("Linked to", _ale.LinkedStation, _ale.LinkedChannel),
            // The two LQA kinds the radio names, read from the LQA's own
            // slot; a sounding transmits THIS station's self.
            AleLinkState.Sounding => WithStation("Sounding as", _ale.LqaStation, _ale.LqaChannel),
            AleLinkState.Exchanging => WithStation("LQA exchange with", _ale.LqaStation, _ale.LqaChannel),
            AleLinkState.Lqa => "LQA in progress",
            // The inbound handshake (field capture 2026-08-24): energy
            // detected, then the call decoding. Neither line carries a
            // station — the name arrives with LINKED.
            AleLinkState.SignalReceived => "Signal received",
            AleLinkState.ReceivingCall => "Receiving a call",
            _ => "—",
        };

        // ST also terminates calls/sends — the button says what it will do.
        StopButtonText = IsCalling || IsSending || IsLinked ? "DISCONNECT" : "STOP";
        CanStop = AleReady;

        var fill = _ale.FillState;
        IsFillComplete = fill.IsConfirmed && fill.Value == AleFillState.Complete;
        FillStateText = !fill.IsConfirmed ? "—" : fill.Value switch
        {
            AleFillState.NeedSelfAddress => "incomplete — needs a self address",
            AleFillState.NeedIndividual => "incomplete — needs an individual",
            AleFillState.NeedChannels => "incomplete — needs scan channels",
            AleFillState.Complete => "✓ Complete",
            _ => "—",
        };

        CanScan = AleReady && IsFillComplete && !IsScanning && !InCall;
        ScanDisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_ale.IsAleConfirmed ? "ALE controls wait for the radio to confirm ALE mode."
            : !fill.IsConfirmed ? "Scan waits for the radio to report its fill state (fill-gate lines, or SCANNING when complete)."
            : !IsFillComplete ? $"Scan is blocked by the radio until the fill is complete — radio reports: {FillStateText}."
            : IsLqa ? LqaInProgressReason
            : InCall ? OnAirDisabledReason
            : IsScanning ? "Already scanning."
            : "";

        var selfs = _ale.SelfAddresses;
        var selfRows = new List<AleSelfRowViewModel>(selfs.Count);
        foreach (var s in selfs) selfRows.Add(new AleSelfRowViewModel(s.Address, s.ChannelGroup));
        SelfRows = selfRows;

        NetRows = BuildRows(nets: true);
        StationRows = BuildRows(nets: false);
        RefreshBroadcastChoices();

        AreControlsEnabled = AleReady;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_ale.IsAleConfirmed ? "ALE controls wait for the radio to confirm ALE mode."
            : "";

        ScanCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CallAnyCommand.NotifyCanExecuteChanged();
        CallAllCommand.NotifyCanExecuteChanged();
        AmdAnyCommand.NotifyCanExecuteChanged();
        AmdAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The two pinned pickers' contents, rebuilt in the same refresh
    /// that builds <see cref="NetRows"/> and sourced from the ONE union
    /// (<see cref="AleSurface.BroadcastChannels"/>).
    ///
    /// <para>SELECTION LIFETIME (plan §3, verbatim): the picks are app-side
    /// INPUT state, INDEPENDENT of the ItemsSource. They are pruned ONLY when
    /// the group mirror is CONFIRMED-read and NON-BLANK yet lacks the picked
    /// channel; a blank/unreported rebuild — a fresh session, or the reconnect
    /// that blanks the mirror — never prunes, because "not reported yet" is not
    /// "gone". The commands' own guards gate the wire in the meantime.</para>
    ///
    /// <para>AUDIT ROUND 1, MAJOR 1: "confirmed-read" means the WHOLE TEN-SLOT
    /// TABLE (<see cref="AleSurface.GroupTableFullyRead"/>), not merely a
    /// non-empty union. A partial table — one group answered, nine still
    /// unread — carries a union that legitimately lacks a channel a later
    /// group will report, and pruning on it erased the operator's pick after a
    /// reconnect.</para>
    ///
    /// <para>AUDIT ROUND 1, MAJOR 2: when the lists actually change, the
    /// SELECTION is re-announced. A live Picker whose ItemsSource went blank
    /// has already dropped its own SelectedItem; the property write is refused
    /// (see the setters), so this notification is what makes it re-adopt the
    /// kept value once its items come back.</para></summary>
    private void RefreshBroadcastChoices()
    {
        var channels = _ale.BroadcastChannels;
        if (!AnyChannelChoices.SequenceEqual(channels))
        {
            AnyChannelChoices = channels;
            AllChannelChoices = [MessagesViewModel.AutoChannel, .. channels];
            OnPropertyChanged(nameof(SelectedAnyChannel));
            OnPropertyChanged(nameof(SelectedAllChannel));
        }

        if (!_ale.GroupTableFullyRead) return;                 // partial read: never prunes
        if (channels.Count == 0) return;                       // blank mirror: never prunes
        if (_selectedAnyChannel is { } any && !channels.Contains(any))
            SetSelectedAnyChannel(null);
        if (_selectedAllChannel != MessagesViewModel.AutoChannel && !channels.Contains(_selectedAllChannel))
            SetSelectedAllChannel(MessagesViewModel.AutoChannel);
    }

    private static string WithStation(string verb, string? station, string? channel)
    {
        if (station is null) return verb;
        return channel is null ? $"{verb} {station}" : $"{verb} {station} — CH {channel}";
    }

    /// <summary>ONE projection, run twice (§17): the NETS or the INDIVIDUALS,
    /// each in the radio's own listing order. Selfs are NOT call targets — they
    /// live in the read-only selfs card. LQA (RAN) is offered on individuals
    /// only, so a net row reports <c>CanLqa</c> false and the pane leaves its
    /// column empty.</summary>
    private IReadOnlyList<AleStationRowViewModel> BuildRows(bool nets)
    {
        bool canAct = AleReady;
        // Round 15 item I (F69): AMD ▸ carries the on-air term too. It only
        // preselects and switches the view, but it is the door to a SEND, and
        // the send itself is refused on air (MessagesViewModel) — a row that
        // stays live while the radio transmits offers a dead end.
        // LINKED is the carve-out (owner ask 2026-08-24; manual §2.5.2.7(g)):
        // the send is legal in a held link, so AMD ▸ opens there too. CALL
        // keeps the full on-air gate — a CAL from inside a link is uncaptured,
        // and the link's own terminator is the Disconnect button (SCA).
        bool canCall = canAct && !InCall;
        bool canAmd = canAct && (!InCall || IsLinked);
        var rows = new List<AleStationRowViewModel>();
        foreach (var a in nets ? _ale.NetAddresses : _ale.IndividualAddresses)
            rows.Add(new AleStationRowViewModel(a.Address, isNet: nets, a.ChannelGroup,
                a.AssociatedSelf,
                canCall, canAmd, canLqa: canAct && !nets, CallRow, AmdRow, LqaRow));
        return rows;
    }

    // ---- Row actions (bodies repeat the CanExecute guards) --------------------

    private void CallRow(AleStationRowViewModel row)
    {
        if (!AleReady || InCall || !row.CanCall) return;
        _ale.Call(row.Address);
    }

    private void AmdRow(AleStationRowViewModel row)
    {
        if (!AleReady || !row.CanAmd) return;
        _messages.PreselectTarget(row.Address);      // view state only — sends nothing
        _messages.OpenComposeCommand.Execute(null);  // Compose visible if Inbox was open
        IsLqaTabOpen = false;                        // Messages lives on the main tab
    }

    private void LqaRow(AleStationRowViewModel row)
    {
        if (!AleReady || !row.CanLqa || row.IsNet) return;   // RAN: individuals only
        _lqa.PreselectRankStation(row.Address);      // view state only — sends nothing
        OpenLqaTab();                                // switch to the LQA sub-tab (and land)
    }

    // ---- The pinned broadcast rows' actions (plan §2) ---------------------------
    // Bodies repeat the CanExecute guards, house style. Each is ONE visible
    // command on the existing CAL/SE senders — the literals and the channel ride
    // argument slots that already exist (invariant 4: no new spellings).

    private bool CanExecuteCallAny() => AleReady && !InCall && SelectedAnyChannel is not null;

    /// <summary>`CAL ANY nn`. The channel is REQUIRED: the radio answers a bare
    /// `CAL ANY` with ` NO CHANS IN GRP ` and transmits nothing (probe P20),
    /// so the button stays withheld until one is picked — the caption under the
    /// rows is the reason.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteCallAny))]
    private void CallAny()
    {
        if (!AleReady || InCall || SelectedAnyChannel is null) return;
        _ale.Call(MessagesViewModel.AnyAddress, SelectedAnyChannel);
    }

    private bool CanExecuteCallAll() => AleReady && !InCall;

    /// <summary>`CAL ALL` (Auto — the radio picks its own channel and the call
    /// AUTO-LINKS, P20) or `CAL ALL nn` when the operator picked one.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteCallAll))]
    private void CallAll()
    {
        if (!AleReady || InCall) return;
        if (SelectedAllChannel == MessagesViewModel.AutoChannel) _ale.Call(MessagesViewModel.AllAddress);
        else _ale.Call(MessagesViewModel.AllAddress, SelectedAllChannel);
    }

    // LINKED allows AMD (the row-gating carve-out, owner ask 2026-08-24).
    private bool CanExecuteAmdBroadcast() => AleReady && (!InCall || IsLinked);

    /// <summary>AMD ▸ for ANY — mirrors <see cref="AmdRow"/>: preselect on the
    /// Messages VM, open Compose, come back to the main tab. Sends NOTHING.
    /// An unpicked channel still opens compose; the compose picker is what
    /// gates the send.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteAmdBroadcast))]
    private void AmdAny()
    {
        if (!AleReady || (InCall && !IsLinked)) return;
        _messages.PreselectBroadcast(MessagesViewModel.AnyAddress, SelectedAnyChannel);
        _messages.OpenComposeCommand.Execute(null);
        IsLqaTabOpen = false;
    }

    /// <summary>AMD ▸ for ALL. "Auto" carries as NO channel — the compose
    /// picker lands on its own Auto default, which is the bare `SE 9 ALL`.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteAmdBroadcast))]
    private void AmdAll()
    {
        if (!AleReady || (InCall && !IsLinked)) return;
        _messages.PreselectBroadcast(MessagesViewModel.AllAddress,
            SelectedAllChannel == MessagesViewModel.AutoChannel ? null : SelectedAllChannel);
        _messages.OpenComposeCommand.Execute(null);
        IsLqaTabOpen = false;
    }

    // ---- Sub-tab switch --------------------------------------------------------
    // The SWITCH itself is view state. Round 11 §4 gives the LQA landing ONE
    // read — the bare EXCH that refreshes the schedule mirror (the
    // editors-read-fresh tier: EVERY landing, one cheap command). It fires HERE
    // because round-9 doctrine forbids a read from a tab the operator is not on,
    // and this is the only place the operator arrives.

    [RelayCommand]
    private void OpenLqaTab()
    {
        IsLqaTabOpen = true;
        _lqa.OnLqaTabOpened();
    }

    [RelayCommand] private void OpenMainTab() => IsLqaTabOpen = false;

    // ---- Scan / Stop -----------------------------------------------------------

    private bool CanExecuteScan() => CanScan;

    [RelayCommand(CanExecute = nameof(CanExecuteScan))]
    private void Scan()
    {
        if (!CanScan) return;
        _ale.StartScan();
    }

    private bool CanExecuteStop() => CanStop;

    [RelayCommand(CanExecute = nameof(CanExecuteStop))]
    private void Stop()
    {
        if (!CanStop) return;
        // F5 (plan §2, probes P20/P20b): ST does NOT end an established link —
        // the ALL link survived two of them AND a serial session close/reopen.
        // `SCA` is the captured terminator (`ALE> TERMINATING LINK` →
        // `SCANNING`). Calling / Sending / an LQA keep ST, which aborts them
        // (captured). This is the app's ONE behavioural branch, and it branches
        // on the RADIO-REPORTED link state the banner reads — never on
        // app-side memory of what was pressed.
        if (IsLinked) _ale.StartScan();
        else _ale.Stop();
    }

    // ---- No manual refresh (§17 G-D1, owner 2026-08-22) -------------------------
    // The Stations card's Refresh button and its command are DELETED. Every
    // app-side write already closes with the bulk book re-read through the
    // programming gate, into the ONE mirror both cards render from, so the
    // lists follow Program / Delete / Add member / Erase BY CONSTRUCTION;
    // off-app edits (front panel, fill gun) are the reconnect's business. The
    // lazy first load above is untouched — that is what fills the cards.
}
