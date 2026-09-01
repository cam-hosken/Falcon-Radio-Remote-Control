using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Services;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The ALE settings pane's "Scan channel groups" card
/// (plan-ale-programming.md §4.4). The groups are the axis addresses point at
/// by NUMBER: <c>ADDC</c>/<c>DELC</c> edit one group's membership, and a
/// <c>CHG g</c> read is the verify.
///
/// <list type="bullet">
///   <item><b>Program</b> (LEFT, the DEFAULT): the vertical group spinner
///     (0-9, wrapping) beside the PICKED group's channel rows, each with a
///     Remove, and one add-channel row.</item>
///   <item><b>Groups</b>: ten read-only rows, the same three-state
///     rendering.</item>
/// </list>
///
/// <para><b>Three states, never conflated</b> (the
/// <see cref="AleChannelGroup"/> contract): "—" when the group has NOT been
/// queried this session, "No channels in this group" when the radio has
/// CONFIRMED it empty (an empty group answers NOTHING at all — the captured
/// silence is what a completed read turns into the empty state), and the rows
/// themselves in the radio's own order.</para>
///
/// <para><b>Read path — the round-9 two-tier doctrine (§6).</b> The SPINNER is
/// the editor's target identity, so a spin is an EDITOR LANDING: it sends
/// <c>CHG g</c> for the group it lands on. So do the initial sight (and the
/// reconnect after a drop, edge-detected) and every Program-tab landing. The
/// GROUPS tab is the lazy tier: its FIRST landing this session sends the
/// whole table (<c>CHG 0</c> … <c>CHG 9</c> + one sentinel) and nothing else
/// in the app ever does. Rapid spins are safe by Core's one-operation-per-store
/// queue: the active read commits its slot once and the spins coalesce into
/// ONE pending operation that commits its union once.</para>
///
/// <para><b>Writes share the ONE gate</b> with the address card — mutual
/// exclusion between the two cards is the point — and each closes with a
/// <c>CHG g</c> read. A DUPLICATE add is silently ignored by the radio, so the
/// closing read shows an unchanged list and the card invents no error.</para>
///
/// <para><b>Gating is TWO-LEVEL</b> exactly as on the address card (owner
/// ruling 5): the card stays live while the radio scans — landings and spins
/// still READ — while the write commands require not scanning and not
/// calling/linked/sending.</para>
/// </summary>
public partial class AleScanGroupsViewModel : ObservableObject
{
    /// <summary>Groups are 0-9 (AleController.ValidateChannelGroup).</summary>
    public const int GroupCount = 10;

    /// <summary>Channels are 0-99 (AleController.SendChannelEdit).</summary>
    public const int MaxChannel = 99;

    /// <summary>The queried-and-CONFIRMED-empty caption — the second of the
    /// three states, and never shown for a group nobody has asked about.</summary>
    public const string EmptyGroupCaption = "No channels in this group";

    /// <summary>The never-queried rendering — the radio has said nothing about
    /// this group this session.</summary>
    public const string UnqueriedText = "—";

    // ---- Multi-add (round 11 §5) -----------------------------------------
    // The add box takes MULTIPLE space-separated channels, like the HOP LIST
    // add box it is modelled on. The wire is unchanged — `ADDC g ch` is still
    // one channel per command — so N tokens are N SEQUENTIAL sends through the
    // one gate, each with its own outcome. Client validation is ALL-OR-NOTHING
    // and names the offender: half a batch on the wire because token four was
    // a typo is the failure mode this avoids.

    /// <summary>The offender-naming client refusal (R13: operator words, no
    /// radio token). <c>{0}</c> is the token as the operator typed it.</summary>
    public const string InvalidChannelFormat = "'{0}' — channels are 0-99.";

    /// <summary>Nothing typed at all.</summary>
    public const string NoChannelsError = "Type at least one channel to add, 0-99.";

    /// <summary>The add box's hint (§5's exact placeholder).</summary>
    public const string AddChannelsPlaceholder = "e.g. 5 12 47 (space-separated)";

    private readonly AleSurface _ale;
    private readonly RadioSession _session;

    private bool _sightReadThisSession;
    private bool _groupsTabLoadedThisSession;
    private bool _aleWasConfirmed;

    /// <summary>THE CAMPAIGN SIGNAL (plan-clone-write-structural.md D1, §4 row
    /// 9). Null where there is no campaign to wait for.</summary>
    private readonly ICampaignSignal? _campaign;

    /// <summary>Deferred reads owed to the campaign's end (§4 SUPPRESSION
    /// SCOPE). One flag per WIRE ACT, not per caller: however many sight edges,
    /// tab opens and spins were deferred, exactly one read of each kind goes
    /// out when the campaign lets go.</summary>
    private bool _pickedGroupReadOwed;

    private bool _allGroupsReadOwed;

    public AleScanGroupsViewModel(
        AleSurface ale, RadioSession session, ICampaignSignal? campaign = null)
    {
        _ale = ale;
        _session = session;
        _campaign = campaign;
        GroupRows = [.. Enumerable.Range(0, GroupCount).Select(g => new AleGroupListRow(g))];

        // The campaign's END edge runs the recompute; Refresh settles what is
        // owed if this pane can read now, and leaves it owed if it cannot.
        if (campaign is not null)
            campaign.Changed += (_, _) => { if (!campaign.CampaignActive) Refresh(); };
        ale.Changed += (_, _) => Refresh();
        session.PhaseChanged += (_, _) =>
        {
            if (_session.Phase != SessionPhase.Ready)
            {
                _sightReadThisSession = false;
                _groupsTabLoadedThisSession = false;
                _aleWasConfirmed = false;
                // Session-scoped: reads deferred for a radio that has gone are
                // not owed to the next one.
                _pickedGroupReadOwed = false;
                _allGroupsReadOwed = false;
                InputError = "";
                OperationStatus = "";
                // A multi-add batch that lost its session sends no remainder:
                // the gate abandons the operation in flight, and the channels
                // behind it were never on the wire. The RUNNING latch goes with
                // them, or the Add button would stay dead after a reconnect.
                _pendingAdds.Clear();
                _addOutcomes.Clear();
                _addBatchRunning = false;
                _ale.Programming.AbandonForSessionDrop();
            }
            Refresh();
        };

        Refresh();
    }

    private bool Ready => _session.Phase == SessionPhase.Ready;

    private bool AleReady => Ready && _ale.IsAleConfirmed;

    // ---- The read path (§6) ------------------------------------------------

    /// <summary>One <c>CHG g</c> + sentinel for the PICKED group — an editor
    /// landing, fresh every time.</summary>
    private void ReadPickedGroup()
    {
        // D1 QUIESCE (audit round 1): `!AleReady` leaves the debt OWED. A
        // campaign that ended outside ALE must not consume a read this pane
        // cannot perform; the next ALE confirmation pays it.
        if (!AleReady) return;
        // Every caller of this funnel — the sight edge, the Program-tab open, a
        // spin — defers here while a campaign owns the wire.
        if (_campaign?.CampaignActive == true) { _pickedGroupReadOwed = true; return; }
        _pickedGroupReadOwed = false;
        _ale.RequestChannelGroup(PickedGroup);
    }

    /// <summary>Settle the deferred reads, once each, and ONLY while this pane
    /// can read. Called from <see cref="Refresh"/>, the pane's every-event
    /// recompute.</summary>
    private void PayWhatIsOwed()
    {
        if (_campaign?.CampaignActive == true || !AleReady) return;

        if (_allGroupsReadOwed)
        {
            _allGroupsReadOwed = false;
            _ale.RequestAllChannelGroups();
        }
        if (_pickedGroupReadOwed) ReadPickedGroup();      // clears the latch itself
    }

    /// <summary>The view's <c>Loaded</c>; the initial-sight read is
    /// edge-detected in <see cref="Refresh"/>.</summary>
    public void EnsureLoaded() => Refresh();

    // ---- Sub-tab view state ------------------------------------------------

    [ObservableProperty] private bool isGroupsTabOpen;

    [RelayCommand]
    private void OpenProgramTab()
    {
        IsGroupsTabOpen = false;
        InputError = "";
        ReadPickedGroup();
        Refresh();
    }

    /// <summary>The LAZY tier: the whole ten-group table, once per session, on
    /// the FIRST landing on this tab — the ONE place in the app that reads a
    /// group the picker is not on.</summary>
    [RelayCommand]
    private void OpenGroupsTab()
    {
        IsGroupsTabOpen = true;
        InputError = "";
        if (AleReady && !_groupsTabLoadedThisSession)
        {
            _groupsTabLoadedThisSession = true;
            // D1 QUIESCE (§4 SUPPRESSION SCOPE — tab opens defer too): the tab
            // opens, the table renders from whatever the mirror holds, and the
            // ten-group read is owed to the campaign's end.
            if (_campaign?.CampaignActive == true) _allGroupsReadOwed = true;
            else _ale.RequestAllChannelGroups();
        }
        Refresh();
    }

    // ---- The group spinner (TARGET IDENTITY — a spin reads) ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickedGroupText))]
    private int pickedGroup;

    public string PickedGroupText => PickedGroup.ToString(CultureInfo.InvariantCulture);

    [RelayCommand] private void GroupUp() => SpinGroup(+1);

    [RelayCommand] private void GroupDown() => SpinGroup(-1);

    private void SpinGroup(int delta)
    {
        PickedGroup = (PickedGroup + delta + GroupCount) % GroupCount;
        InputError = "";
        ReadPickedGroup();          // a landing reads its target
        Refresh();
    }

    // ---- Gate, notes and the operation status ------------------------------

    [ObservableProperty] private bool areControlsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisabledReason))]
    private string disabledReason = "";

    public bool HasDisabledReason => !string.IsNullOrEmpty(DisabledReason);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWriteDisabledReason))]
    private string writeDisabledReason = "";

    public bool HasWriteDisabledReason => !string.IsNullOrEmpty(WriteDisabledReason);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputError))]
    private string inputError = "";

    public bool HasInputError => !string.IsNullOrEmpty(InputError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationStatus))]
    private string operationStatus = "";

    public bool HasOperationStatus => !string.IsNullOrEmpty(OperationStatus);

    private bool IsScanning
    {
        get
        {
            var link = _ale.LinkState;
            return link.IsConfirmed && link.Value == AleLinkState.Scanning;
        }
    }

    /// <summary>ROUND 15 item I (F69): THE on-air term, the same one every
    /// other write surface reads (it was this file's own copy of the list).</summary>
    private bool InCallOrSending => _ale.IsOnAir;

    private bool CanWrite() => AleReady && !IsScanning && !InCallOrSending;

    // ---- The picked group's rows -------------------------------------------

    /// <summary>The picked group's channels, 2-digit, in the RADIO's order and
    /// un-deduplicated (store what was sent).</summary>
    public ObservableCollection<AleChannelRow> ChannelRows { get; } = [];

    /// <summary>State 1: this group has never been queried this session.</summary>
    [ObservableProperty] private bool isPickedGroupUnqueried = true;

    /// <summary>State 2: queried and CONFIRMED empty.</summary>
    [ObservableProperty] private bool isPickedGroupEmpty;

    // ---- The Groups tab's ten rows -----------------------------------------

    public IReadOnlyList<AleGroupListRow> GroupRows { get; }

    // ---- Refresh from the mirror -------------------------------------------

    private void Refresh()
    {
        if (AleReady && !_sightReadThisSession)
        {
            _sightReadThisSession = true;
            ReadPickedGroup();
        }

        // …and whatever a campaign deferred, settled on the same recompute.
        PayWhatIsOwed();

        if (_aleWasConfirmed && !AleReady) OperationStatus = "";
        _aleWasConfirmed = AleReady;

        AreControlsEnabled = AleReady;
        DisabledReason =
            !Ready ? "Not connected — open Settings → Connection to connect."
            : !_ale.IsAleConfirmed ? "Scan channel groups are ALE-scoped — waiting for the radio to confirm ALE."
            : "";

        WriteDisabledReason =
            !AleReady ? ""
            : IsScanning ? AleProgrammingViewModel.ScanningDisabledReason
            : InCallOrSending ? AleProgrammingViewModel.InCallDisabledReason
            : "";

        var groups = _ale.ChannelGroups;
        foreach (var row in GroupRows) row.Apply(ChannelsTextOf(groups, row.Number));
        UpdateChannelRows(groups);

        AddChannelCommand.NotifyCanExecuteChanged();
        RemoveChannelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The three-state text one group renders as, shared by the list
    /// tab's rows so two views of one group cannot disagree.</summary>
    private static string ChannelsTextOf(IReadOnlyList<AleChannelGroup> groups, int number)
    {
        var channels = number >= 0 && number < groups.Count ? groups[number].Channels : null;
        if (channels is null) return UnqueriedText;
        if (channels.Count == 0) return EmptyGroupCaption;
        return string.Join(' ', channels.Select(TwoDigit));
    }

    private void UpdateChannelRows(IReadOnlyList<AleChannelGroup> groups)
    {
        var channels = PickedGroup < groups.Count ? groups[PickedGroup].Channels : null;

        IsPickedGroupUnqueried = channels is null;
        IsPickedGroupEmpty = channels is { Count: 0 };

        var wanted = channels ?? [];

        // Rebuild only on real change — the rows carry a command each.
        if (ChannelRows.Count == wanted.Count)
        {
            bool same = true;
            for (int i = 0; i < wanted.Count; i++)
                if (ChannelRows[i].Channel != wanted[i]) { same = false; break; }
            if (same) return;
        }

        ChannelRows.Clear();
        foreach (var channel in wanted) ChannelRows.Add(new AleChannelRow(channel, RemoveChannelCommand));
    }

    internal static string TwoDigit(int channel)
        => channel.ToString("00", CultureInfo.InvariantCulture);

    // ---- Add / remove a channel --------------------------------------------

    [ObservableProperty] private string addChannelInput = "";

    /// <summary>The channels of THIS batch still to send, in typed order.</summary>
    private readonly Queue<int> _pendingAdds = new();

    /// <summary>This batch's per-channel outcomes, oldest first. Accepted
    /// channels contribute NOTHING — the re-read rows are their proof, the
    /// card's standing rule — so what accumulates here is the ones the
    /// operator must not miss.</summary>
    private readonly List<string> _addOutcomes = [];

    /// <summary>How many channels this batch asked for. A ONE-channel add
    /// reports exactly as it always did (the outcome, bare); only a MULTI add
    /// prefixes each outcome with its channel, because only then is "which
    /// one?" a question the operator can have.</summary>
    private int _addBatchSize;

    /// <summary>A batch is RUNNING — from the first write until the last
    /// outcome (or the abandonment that ends it early).
    /// <para>AUDIT ROUND 1, MAJOR-1: the batch state is SHARED, so a second
    /// press while one is in flight used to clear the running batch's own
    /// remainder — the first batch then sent one channel and dropped the rest
    /// SILENTLY. The press is refused instead, by CanExecute and again in the
    /// body (Execute ignores CanExecute), and the running batch is never
    /// touched by anything but its own callback.</para></summary>
    private bool _addBatchRunning;

    /// <summary>Why a press during a running batch sends nothing (R13:
    /// operator words, no radio token).</summary>
    public const string BatchRunningError =
        "Still adding the last channels — wait for them to finish.";

    /// <summary>Level THREE, and this command's alone: a batch already on the
    /// wire owns the queue until it is done.</summary>
    private bool CanAddChannel() => CanWrite() && !_addBatchRunning;

    /// <summary>
    /// ADDC for EVERY typed channel, one command each (round 11 §5). The
    /// parse is all-or-nothing and names the first offender; nothing is sent
    /// when it fails. DUPLICATES ARE NOT PRE-FILTERED: the radio ignores a
    /// repeat silently, so a duplicate is wire semantics, not a client error,
    /// and pre-filtering would make the app disagree with the radio about what
    /// was asked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddChannel))]
    private void AddChannel()
    {
        if (!AleReady) return;
        if (!CanWrite()) { InputError = WriteDisabledReason; return; }
        // BEFORE anything touches the shared batch state (MAJOR-1).
        if (_addBatchRunning) { InputError = BatchRunningError; return; }

        // SPACE-separated, exactly as §5 spells it (audit round 1, MAJOR-2):
        // a comma or a semicolon is part of the token, so "5,12" is ONE token
        // and one offender. Widening the delimiter set would silently accept
        // a grammar the plan does not define.
        var tokens = (AddChannelInput ?? "").Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            InputError = NoChannelsError;
            return;
        }

        var channels = new int[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out channels[i])
                || channels[i] < 0 || channels[i] > MaxChannel)
            {
                // CLIENT-SIDE, before anything reaches the wire, naming the
                // token that failed — a batch is refused whole.
                InputError = string.Format(CultureInfo.InvariantCulture, InvalidChannelFormat, tokens[i]);
                return;
            }
        }

        _pendingAdds.Clear();
        _addOutcomes.Clear();
        foreach (var channel in channels) _pendingAdds.Enqueue(channel);
        _addBatchSize = channels.Length;
        AddChannelInput = "";               // the box is spent

        _addBatchRunning = true;
        AddChannelCommand.NotifyCanExecuteChanged();
        SendNextAdd();
    }

    /// <summary>One ADDC, then the next on its outcome — SEQUENTIAL, because
    /// the gate runs one operation at a time and each write's refusal must
    /// stay attributable to the channel that drew it.</summary>
    private void SendNextAdd()
    {
        if (_pendingAdds.Count == 0) { EndBatch(); return; }

        int group = PickedGroup;
        int channel = _pendingAdds.Dequeue();

        bool started = RunWrite(group, () => _ale.ProgramScanChannel(group, channel), outcome =>
        {
            string text = DescribeOutcome(outcome);
            if (text.Length > 0)
                _addOutcomes.Add(_addBatchSize > 1 ? $"Channel {TwoDigit(channel)}: {text}" : text);
            SendNextAdd();
        });

        // A gate that refused to open sent nothing and will never call back:
        // the rest of the batch is abandoned, with the reason on screen and
        // the outcomes collected so far still rendered.
        if (!started)
        {
            _pendingAdds.Clear();
            EndBatch();
        }
    }

    /// <summary>The batch is over — render whatever the operator must not miss
    /// and hand the Add button back.</summary>
    private void EndBatch()
    {
        _addBatchRunning = false;
        if (_addOutcomes.Count > 0) OperationStatus = string.Join(" ", _addOutcomes);
        AddChannelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void RemoveChannel(string? channelText)
    {
        if (!AleReady) return;
        if (!CanWrite()) { InputError = WriteDisabledReason; return; }
        if (!int.TryParse(channelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
            return;

        int group = PickedGroup;
        RunWrite(group, () => _ale.RemoveScanChannel(group, channel),
            outcome => OperationStatus = DescribeOutcome(outcome));
    }

    /// <summary>Every write on this card: through the ONE gate, closing with a
    /// <c>CHG g</c> read of the group it edited. False = the gate was busy,
    /// nothing was sent, and the reason is the InputError.</summary>
    private bool RunWrite(int group, Action write, Action<AleProgrammingOutcome> onOutcome)
    {
        InputError = "";
        OperationStatus = "";

        if (_ale.Programming.TryRun(
                write, () => _ale.RequestChannelGroup(group), onOutcome, out string busyReason))
            return true;

        InputError = busyReason;
        return false;
    }

    private static string DescribeOutcome(AleProgrammingOutcome outcome) => outcome.Result switch
    {
        // Accepted says nothing: the re-read list is the proof.
        AleProgrammingResult.Accepted => "",
        AleProgrammingResult.Refused => AleRefusalVocabulary.Describe(outcome.Detail),
        AleProgrammingResult.Unverified =>
            "Unverified — " + (outcome.Detail ?? "the radio did not answer") + ".",
        _ => "Failed — " + (outcome.Detail ?? "nothing reached the wire") + ".",
    };
}

/// <summary>One channel of the picked group: the radio's own 2-digit value,
/// and the Remove that sends it back. Immutable — a changed group rebuilds
/// the rows.</summary>
public sealed class AleChannelRow
{
    public AleChannelRow(int channel, ICommand remove)
    {
        Channel = channel;
        ChannelText = AleScanGroupsViewModel.TwoDigit(channel);
        Remove = remove;
    }

    internal int Channel { get; }

    /// <summary>The 2-digit form the wire uses and the operator reads. It is
    /// also the Remove command's parameter, so a removal cannot be lost in a
    /// round trip through the display.</summary>
    public string ChannelText { get; }

    public ICommand Remove { get; }
}

/// <summary>One READ-ONLY row of the Groups tab: a group number and the same
/// three-state rendering the editor uses. No commands, no buffers.</summary>
public partial class AleGroupListRow : ObservableObject
{
    public AleGroupListRow(int number)
    {
        Number = number;
        NumberText = number.ToString(CultureInfo.InvariantCulture);
    }

    public int Number { get; }
    public string NumberText { get; }

    [ObservableProperty] private string channelsText = AleScanGroupsViewModel.UnqueriedText;

    internal void Apply(string text) => ChannelsText = text;
}
