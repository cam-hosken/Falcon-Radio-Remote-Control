using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Falcon.App.Core.Cloning;
using Falcon.App.Core.Session;
using Falcon.App.Core.Surfaces;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Core.ViewModels;

/// <summary>
/// The Radio-settings CLONING card (plan round 11 §9A, reorganized by
/// plan-clone-pane-cleanup) — the round-4 AL3 stub, wired, behind TWO TABS.
/// Two campaigns ("Read radio settings" / "Write file to radio") and the
/// identity TABLE: one <see cref="SelfRowViewModel"/> per self in the loaded
/// file (owner ruling R-A, plan-clone-field-round2 §3.3).
///
/// <para><b>The surface is per-operation.</b> Each tab owns its status line
/// (<see cref="ReadStatusText"/> / <see cref="WriteStatusText"/>), its
/// clearable report (<see cref="ReadReportLines"/> /
/// <see cref="WriteReportLines"/>) and its gate reason
/// (<see cref="ReadGateReason"/> / <see cref="WriteGateReason"/>), so a write
/// can never overwrite the read's account of itself and the read is never
/// greyed by a file it does not need. A service event is ROUTED to the tab it
/// belongs to; a file arriving from a load or an external <c>Adopt</c> belongs
/// to the Write tab, which is where <see cref="FileLine"/> names it.</para>
///
/// <para><b>Why a table replaced the single picker.</b> Round 11's control
/// chose ONE identity, which could only ever move the file's FIRST self and
/// demoted it silently. The 2026-08-21 live clone lost <c>HOS</c> that way. Now
/// every self is on screen with its own disposition, and
/// <see cref="CloneSwap.Refusal"/> answers LIVE — the operator sees a bad table
/// while editing it, not as a refusal mid-book after the erase.</para>
///
/// <para><b>Gating is the standing two-level policy.</b> Read needs
/// connected + Ready; Write needs connected + Ready + a file whose every
/// manifest domain was READ. BOTH additionally grey while the radio is ON THE
/// AIR — a whole-radio campaign during a link would fight the radio for the
/// wire. SCANNING no longer greys them (D11, plan-clone-write-structural): the
/// campaign stops the scan itself at every ALE occupancy and restarts it at the
/// end (D8), so gating the press on scanning only made the operator do by hand
/// what the campaign does automatically. The reason is always visible, never a
/// silent grey.</para>
///
/// <para>File I/O is the VIEW's (the Stage-8 export seam): this VM hands out
/// JSON and takes JSON back, and never touches a path.</para>
/// </summary>
public partial class CloneViewModel : ObservableObject
{
    /// <summary>Why BOTH campaigns are grey at the second gating level.
    /// REWORDED 2026-08-23 (manager ruling): "scanning or in a call" was false
    /// during an LQA, which the on-air term now also withholds the campaigns
    /// for — minutes of transmission a campaign would try to lap modes
    /// through. REWORDED AGAIN 2026-08-29 (D11,
    /// plan-clone-write-structural §2): the sentence named SCANNING, and
    /// scanning is no longer a gate term — see <see cref="OnAir"/>.</summary>
    public const string OnAirGateReason =
        "The radio is on the air — stop it first.";

    private readonly CloneService _clone;
    private readonly AleSurface _ale;
    private readonly RadioSession _session;

    public CloneViewModel(CloneService clone, AleSurface ale, RadioSession session)
    {
        _clone = clone;
        _ale = ale;
        _session = session;

        _clone.Changed += (_, _) => OnCloneChanged();
        // A session phase or ALE link change moves the GATES, never a report:
        // it is not an operation, so it may not repopulate a list the operator
        // has just cleared (plan-clone-pane-cleanup §6, the refresh split).
        _ale.Changed += (_, _) => RefreshGates();
        _session.PhaseChanged += (_, _) => RefreshGates();
        _observedFile = _clone.File;
        RefreshGates();
    }

    // ---- The two tabs (D1/D10) ---------------------------------------------

    /// <summary>Which of the card's two tabs is on screen — false is READ, the
    /// construction default (D10). The VM is a DI singleton and the page is
    /// transient, so this PERSISTS across page visits: an operator returning to
    /// Radio settings finds the tab they left.</summary>
    [ObservableProperty]
    private bool _isWriteTabOpen;

    [RelayCommand]
    private void OpenReadTab() => IsWriteTabOpen = false;

    [RelayCommand]
    private void OpenWriteTab() => IsWriteTabOpen = true;

    // ---- Which operation the card is talking about --------------------------

    /// <summary>The two operations the card reports on, plus "neither yet".</summary>
    private enum CloneOp { None, Read, Write }

    /// <summary>The operation whose command body is currently executing, or
    /// <see cref="CloneOp.None"/>.</summary>
    private CloneOp _running = CloneOp.None;

    /// <summary>The operation that ran LAST — where a <c>Changed</c> event
    /// outside a live run is routed. Set in each command's <c>finally</c>, so
    /// an exception can neither misroute a status line nor wedge a Clear gate.</summary>
    private CloneOp _lastRan = CloneOp.None;

    /// <summary>The <see cref="CloneService.File"/> REFERENCE last seen by the
    /// <c>Changed</c> handler. A new reference arriving while no command is
    /// running is a file that came from <c>LoadJson</c> or an external
    /// <c>Adopt</c> — both of which belong to the Write tab (D11).</summary>
    private CloneFile? _observedFile;

    /// <summary>
    /// The service raises <c>Changed</c> SYNCHRONOUSLY from inside
    /// <c>LoadJson</c>/<c>Adopt</c>, so routing state is always set BEFORE the
    /// call that can fire it (see <see cref="LoadJson"/>). This handler covers
    /// the one case no VM call site can: a file installed by something else.
    /// </summary>
    private void OnCloneChanged()
    {
        var installed = !ReferenceEquals(_observedFile, _clone.File);
        if (_running == CloneOp.None && installed)
            _lastRan = CloneOp.Write;
        // D12: the stored name belongs to the file that WAS stored. A DIFFERENT
        // file in hand has never been stored under it, and re-using the name
        // would overwrite the earlier read's file with these contents on the
        // first Store press. `File` is always assigned BEFORE the `Set`/`Status`
        // that raises this event (CloneService: the read's install, `LoadJson`,
        // `Adopt`), so this is the one place that sees every arrival.
        if (installed) LastStoredFileName = null;
        _observedFile = _clone.File;

        var target = _running != CloneOp.None ? _running : _lastRan;
        // File-visible data rides this SAME event, so the gates are refreshed
        // on every one of them — routing alone would leave HasFile, FileLine
        // and the write gating stale.
        RefreshGates();
        if (target != CloneOp.None) RefreshOperation(target);
    }

    // ---- The identity table (R-A) ------------------------------------------

    /// <summary>One row per self in the loaded file, in the file's own order —
    /// plus the synthetic "no self" row when the file has none (A-6). Empty
    /// until a file is in hand, and empty while the address book was not READ
    /// (C-Q5 — <see cref="BookNotReadCaption"/> says why).</summary>
    public ObservableCollection<SelfRowViewModel> SelfRows { get; } = [];

    /// <summary>C-Q5: an unread (or faulted) address book offers NO identity
    /// rows — there is nothing trustworthy to dispose of — and the write is
    /// already blocked by the manifest. Quoted verbatim by `docs/ui.md`.</summary>
    public const string BookNotReadCaption =
        "Address book not read — re-read the source radio before writing.";

    /// <summary>True while the card shows <see cref="BookNotReadCaption"/>
    /// instead of rows.</summary>
    public bool ShowsBookNotRead => _clone.File is { } file && file.BookState != CloneDomainState.Read;

    /// <summary>C-3: the radio refuses the whole book fill unless a 1-3
    /// character self is programmed FIRST, and the write leads with
    /// `target.Selfs` in file order — so a file whose FIRST self is longer
    /// cannot be written at all. The strict "first" rule is C-D1's (P11 may
    /// later relax it to "present").</summary>
    public static string FillGateCaption(string firstSelf) =>
        $"The radio needs a 1-3 character self first — the first self in this file is {firstSelf}.";

    /// <summary>The fill-gate refusal for the loaded file, or "" when the gate
    /// does not bite.</summary>
    public string FillGateReason
    {
        get
        {
            if (_clone.File is not { } file || file.BookState != CloneDomainState.Read) return "";
            if (file.Selfs.Count == 0) return "";               // the synthetic row IS the repair
            var first = file.Selfs[0].Name;
            return CloneSwap.IsScanGateSelf(first) ? "" : FillGateCaption(first);
        }
    }

    public bool HasFillGateReason => FillGateReason.Length > 0;

    /// <summary>The table as the transform reads it. Rows the operator left
    /// alone are Keep, which the transform treats exactly as an omission.</summary>
    public IReadOnlyList<SelfDisposition> Dispositions => [.. SelfRows.Select(r => r.ToDisposition())];

    /// <summary>The transform's own refusal, LIVE — one sentence or empty. The
    /// card asks the same question the write preflight will, so the operator
    /// never learns about a bad table from a half-written radio.</summary>
    public string IdentityError =>
        _clone.File is { } file ? CloneSwap.Refusal(file, Dispositions) ?? "" : "";

    public bool HasIdentityError => IdentityError.Length > 0;

    // ---- Campaign state ----------------------------------------------------

    public bool IsBusy => _clone.IsRunning;

    /// <summary>Whether a file is in hand at all (read or loaded).</summary>
    public bool HasFile => _clone.File is not null;

    /// <summary>
    /// Whether the Read tab's export presses — <b>Store file…</b> and, since
    /// D13, <b>Share…</b> — are live. A file in hand and no operation running,
    /// nothing else: both presses move a file the app already has, and neither
    /// touches the wire, so neither the session phase nor the on-air term has
    /// anything to say about them.
    ///
    /// <para><b>Why the presses exist</b> (D12, then D13 2026-08-30). A read
    /// used to persist itself into app-private storage the operator cannot
    /// browse and pop the share sheet ONCE — dismiss it and the read was gone.
    /// D13 ended the automatic export entirely: a read now saves nothing, and
    /// these two presses are the only ways the file leaves the app.</para>
    ///
    /// <para><b>ONE GATE FOR BOTH</b> (D13). The two presses ask exactly the
    /// same question, so they share the answer rather than carrying a
    /// <c>CanShare</c> alias that could drift away from it.</para>
    ///
    /// <para>AUDIT ROUND 1: …and not while an export is already in flight. The
    /// presses are <c>async void</c>, so a double-tap would otherwise open a
    /// second picker — or a picker over a share sheet — on top of the
    /// first.</para>
    /// </summary>
    public bool CanStore => HasFile && !IsBusy && !IsExporting;

    /// <summary>Whether the view's export seam is running right now (audit
    /// round 1). The export is the VIEW's — paths and the share sheet are its
    /// half of the Stage-8 seam — but WHETHER THE BUTTON IS PRESSABLE is a
    /// decision, and decisions are this VM's, so the seam reports its
    /// in-flight state here and the gate is computed in one place.</summary>
    public bool IsExporting { get; private set; }

    /// <summary>The view reports the export seam's in-flight state here, in a
    /// <c>try/finally</c> around it — so a throw can neither wedge the button
    /// grey nor leave a second export thinking it is the first.</summary>
    public void SetExporting(bool exporting)
    {
        if (IsExporting == exporting) return;
        IsExporting = exporting;
        OnPropertyChanged(nameof(IsExporting));
        OnPropertyChanged(nameof(CanStore));
    }

    // ---- Per-tab status and report (D6) ------------------------------------

    /// <summary>Per-leg progress while the READ runs, and its outcome sentence
    /// after. The Read tab's own line — a write can never overwrite it.</summary>
    [ObservableProperty]
    private string _readStatusText = "";

    /// <summary>The same for the WRITE — and for a file LOAD, which lands on
    /// the Write tab (D11).</summary>
    [ObservableProperty]
    private string _writeStatusText = "";

    /// <summary>The READ's outcome accounting, in report order. Emptied when a
    /// new read starts (D6) and by <see cref="ClearReadReportCommand"/>.</summary>
    public ObservableCollection<string> ReadReportLines { get; } = [];

    /// <summary>The WRITE's outcome accounting — and the load notices the
    /// round-17 downgrade check produces (D11), which would otherwise have no
    /// operator-visible home.</summary>
    public ObservableCollection<string> WriteReportLines { get; } = [];

    public bool HasReadReport => ReadReportLines.Count > 0;

    public bool HasWriteReport => WriteReportLines.Count > 0;

    private bool CanClearReadReport => HasReadReport && !IsBusy;

    private bool CanClearWriteReport => HasWriteReport && !IsBusy;

    /// <summary>Empty the READ tab's report. VM-side only: the service's own
    /// summary is untouched, and the notice slots are replaced by the next
    /// action of their kind rather than by a Clear.</summary>
    [RelayCommand(CanExecute = nameof(CanClearReadReport))]
    private void ClearReadReport()
    {
        if (ReadReportLines.Count == 0) return;
        ReadReportLines.Clear();
        OnPropertyChanged(nameof(HasReadReport));
        ClearReadReportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearWriteReport))]
    private void ClearWriteReport()
    {
        if (WriteReportLines.Count == 0) return;
        WriteReportLines.Clear();
        OnPropertyChanged(nameof(HasWriteReport));
        ClearWriteReportCommand.NotifyCanExecuteChanged();
    }

    // ---- The one FILE LINE, and where the file came from ---------------------

    /// <summary>Where the file in hand came from — which is what lets the Write
    /// tab NAME it.</summary>
    private enum FileOrigin { None, ReadThisSession, Opened }

    private (string? Name, FileOrigin Origin) _identity = (null, FileOrigin.None);

    private void SetIdentity(string? name, FileOrigin origin)
    {
        _identity = (name, origin);
        OnPropertyChanged(nameof(FileLine));
    }

    /// <summary>The counts composition the card has always shown, reused
    /// verbatim by <see cref="FileLine"/> — plus D5b's trailing clause.</summary>
    private static string CountsOf(CloneFile file) =>
        $"{file.Channels.Count} channel(s), {file.Selfs.Count} self(s), {file.Individuals.Count} individual(s), "
            + $"{file.Nets.Count} net(s), {file.Messages.Count} message(s)"
            + OtherDomainsClause(file);

    /// <summary>
    /// THE OTHER DOMAINS (plan-clone-write-structural.md D5b) —
    /// <c>" + HOP nets, presets, settings, lockouts"</c> (D15-inventory order,
    /// unified across surfaces — owner 2026-08-30), listing exactly the
    /// subset whose read-state marker is <see cref="CloneDomainState.Read"/>,
    /// and OMITTED ENTIRELY when that subset is empty.
    ///
    /// <para><b>Why it exists.</b> The counts are the five domains that have a
    /// number worth showing, and a clone of an EMPTY-FILL radio has zero of
    /// four of them — so the line read "0 self(s), 0 individual(s), 0 net(s),
    /// 2 channel(s), 0 message(s)" and the operator reasonably concluded the
    /// clone had captured only channels. It had captured the settings, the
    /// presets, the HOP nets and the lockouts too; nothing said so. This says
    /// so, in the same words the rest of the card uses (no counts, because
    /// these domains' counts are fixed inventories and a number would invite
    /// arithmetic nobody wants).</para>
    ///
    /// <para>This AMENDS plan-clone-pane-cleanup §7's string manifest, which
    /// forbids unlisted operator strings; the clause is pinned byte-exact in
    /// CloneViewModelTests.</para>
    /// </summary>
    private static string OtherDomainsClause(CloneFile file)
    {
        var read = new List<string>();
        if (file.HopNetState == CloneDomainState.Read) read.Add("HOP nets");
        if (file.ModemState == CloneDomainState.Read) read.Add("presets");
        if (file.SettingState == CloneDomainState.Read) read.Add("settings");
        if (file.Lockouts?.State == CloneDomainState.Read) read.Add("lockouts");
        return read.Count == 0 ? "" : " + " + string.Join(", ", read);
    }

    /// <summary>The ONE line the Write tab shows about the file in hand: its
    /// name, where it came from and what is in it. A read that has not been
    /// saved yet has no name to give, and says so rather than borrowing the
    /// previously opened file's.</summary>
    public string FileLine
    {
        get
        {
            if (_clone.File is not { } file) return "No file loaded.";
            var counts = CountsOf(file);
            if (_identity.Name is not { } name) return $"Read from this radio (not saved) — {counts}";
            var origin = _identity.Origin == FileOrigin.ReadThisSession
                ? "read from this radio"
                : "loaded from file";
            return $"{name} — {origin} — {counts}";
        }
    }

    // ---- Gating ------------------------------------------------------------

    private bool Ready => _session.Phase == SessionPhase.Ready;

    /// <summary>On air — the second gating level the whole card shares with
    /// every other write surface. ROUND 15 item I (F69): the on-air term is
    /// Core's ONE predicate now (it was this file's private list, so an LQA
    /// left the campaigns live while the radio transmitted for minutes); that
    /// half is UNCHANGED and Core's <c>IsOnAir()</c> is not this round's to
    /// touch.
    ///
    /// <para>D11 (2026-08-29, plan-clone-write-structural §2) DROPPED the
    /// round-15 companion term. That round kept <c>Scanning</c> as this card's
    /// OWN extra gate, on the reasoning that a campaign laps modes and a
    /// scanning radio is not something to lap. D8 made that obsolete: the
    /// campaign now issues an unconditional <c>ST</c> at every ALE occupancy
    /// and makes ONE restart attempt at its true end, so it stops and restores
    /// the scan itself. Gating the button press on scanning only forced the
    /// operator to do by hand what the campaign already does — which is exactly
    /// what the 2026-08-29 phone install hit. A CONFIRMED on-air state still
    /// greys both campaigns; a scanning radio does not.</para></summary>
    private bool OnAir
    {
        get
        {
            var link = _ale.LinkState;
            return link.IsConfirmed && link.Value.IsOnAir();
        }
    }

    public bool CanRead => !IsBusy && Ready && !OnAir;

    public bool CanWrite => !IsBusy && Ready && !OnAir && _clone.WriteBlockedReason is null
        && !HasIdentityError && !HasFillGateReason;

    /// <summary>D5: the word "campaign" is not the operator's. Which operation
    /// is in progress is <see cref="_running"/>'s to say.</summary>
    private string InProgressReason => _running == CloneOp.Write
        ? "A write is in progress."
        : "A read is in progress.";

    /// <summary>Why READ is grey, in the operator's words — the first gating
    /// level plus the shared on-air term, and nothing about a file the read
    /// does not need.</summary>
    public string ReadGateReason
    {
        get
        {
            if (IsBusy) return InProgressReason;
            if (!Ready) return "Not connected.";
            if (OnAir) return OnAirGateReason;
            return "";
        }
    }

    public bool HasReadGateReason => ReadGateReason.Length > 0;

    /// <summary>Why WRITE is grey: the same three terms, then the file-side
    /// ones in the order they have always been asked.</summary>
    public string WriteGateReason
    {
        get
        {
            if (IsBusy) return InProgressReason;
            if (!Ready) return "Not connected.";
            if (OnAir) return OnAirGateReason;
            if (HasIdentityError) return IdentityError;
            if (HasFillGateReason) return FillGateReason;
            return _clone.WriteBlockedReason ?? "";
        }
    }

    public bool HasWriteGateReason => WriteGateReason.Length > 0;

    // ---- Commands ----------------------------------------------------------

    /// <summary>
    /// Whether the LAST run of <see cref="ReadCommand"/> actually installed a
    /// new <see cref="CloneFile"/> — the same reference compare the identity
    /// reset turns on, published because the VIEW needs the same answer.
    ///
    /// <para><b>What it is for.</b> It drives the IDENTITY RESET below: a read
    /// that installed a DIFFERENT file has new radio contents in hand, and no
    /// previously opened file's name may stay attached to them.</para>
    ///
    /// <para><b>D13 retired its other job.</b> Audit round 1 published it
    /// because the VIEW saved whatever <see cref="BuildJson"/> handed it and
    /// called the result "read from this radio" — so a read that installed
    /// nothing would have written the PREVIOUSLY LOADED file out under a fresh
    /// name and left the operator one press from programming stale settings.
    /// D13 removed the automatic export, so the view no longer asks: a read
    /// saves nothing at all, and the defect is gone by construction rather than
    /// by this guard. The flag stays because the identity reset needs it.</para>
    /// </summary>
    public bool LastReadInstalledNewFile { get; private set; }

    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync()
    {
        // FIRST, ahead of the preflight (audit round 2): the flag describes the
        // last ATTEMPT, not the last completed run. A blocked attempt that left
        // the PREVIOUS read's `true` standing would send the view off to save
        // that read's file a second time, under a fresh name — the round-1
        // defect through a different door.
        LastReadInstalledNewFile = false;
        // Execute never consults CanExecute — re-check in the body.
        if (!CanRead) return;
        _running = CloneOp.Read;
        StartReport(ReadReportLines, nameof(HasReadReport), ClearReadReportCommand);
        // D6's replace-on-start, extended to the NOTICE (audit round 2,
        // manager ruling): starting a read IS the next action of this slot's
        // kind, so the previous file's "stored: …" may not stay on screen
        // beside a file it is not about. Since D13 a read writes NO line of its
        // own at all, which makes this the only thing that empties the slot
        // between one file's export and the next.
        ClearReadFileNotice();
        RefreshGates();
        // The service can come back WITHOUT installing a file (its own gate
        // closing is one way), and a same-reference return must keep the
        // loaded file's identity. A DIFFERENT instance is new radio contents,
        // which no previously opened file's name may be attached to.
        var before = _clone.File;
        try
        {
            await _clone.ReadAsync().ConfigureAwait(true);
        }
        finally
        {
            _lastRan = CloneOp.Read;
            _running = CloneOp.None;
            LastReadInstalledNewFile = !ReferenceEquals(before, _clone.File);
            if (LastReadInstalledNewFile) SetIdentity(null, FileOrigin.ReadThisSession);
            RefreshGates();
            RefreshOperation(CloneOp.Read);
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (!CanWrite) return;
        _running = CloneOp.Write;
        StartReport(WriteReportLines, nameof(HasWriteReport), ClearWriteReportCommand);
        RefreshGates();
        try
        {
            await _clone.WriteAsync(Dispositions).ConfigureAwait(true);
        }
        finally
        {
            _lastRan = CloneOp.Write;
            _running = CloneOp.None;
            RefreshGates();
            RefreshOperation(CloneOp.Write);
        }
    }

    // ---- File I/O seam (the VIEW owns the path) ----------------------------

    // ---- The two per-tab outcome slots (the view still owns the PATH) -------

    /// <summary>What the last READ's save/share step did, or why it did not.
    /// Rendered by TWO labels — a Caption and an ErrorCaption on complementary
    /// visibilities — so a success never appears in the error style.</summary>
    [ObservableProperty]
    private string _readFileNotice = "";

    private bool _readFileIsError;

    public bool ShowsReadFileNotice => ReadFileNotice.Length > 0 && !_readFileIsError;

    public bool ShowsReadFileError => ReadFileNotice.Length > 0 && _readFileIsError;

    /// <summary>The same slot for the OPEN step, on the Write tab.</summary>
    [ObservableProperty]
    private string _openFileNotice = "";

    private bool _openFileIsError;

    public bool ShowsOpenFileNotice => OpenFileNotice.Length > 0 && !_openFileIsError;

    public bool ShowsOpenFileError => OpenFileNotice.Length > 0 && _openFileIsError;

    /// <summary>
    /// The name the file in hand is ALREADY stored under this session, or null
    /// when it has never been stored (a fresh read, or a loaded file). Both
    /// export presses SEED with this name when there is one, so storing or
    /// sharing the same read twice offers the same name rather than a new
    /// timestamp per press.
    ///
    /// <para>Set by <see cref="NoteReadFileOutcome"/> — the one report that
    /// means "this file really is stored under this name" — and cleared the
    /// moment a DIFFERENT file is installed (see <see cref="OnCloneChanged"/>).
    /// D13 moved the only thing that SETS it to the save-location picker's
    /// success: nothing else in the app writes a durable file any more, so
    /// nothing else can honestly claim a name. It is a seam for the view's own
    /// decision, so it is bound by no markup and raises nothing.</para>
    /// </summary>
    public string? LastStoredFileName { get; private set; }

    /// <summary>The view reports the READ tab's file step here — since D13 that
    /// is the Store and Share presses only, because a read itself no longer has
    /// a file step to report. A non-null <paramref name="storedName"/> means the
    /// file really is stored under that name, which is what promotes it onto the
    /// Write tab's file line (D3); only the save-location picker's success
    /// passes one (D13).</summary>
    public void NoteReadFileOutcome(string message, string? storedName, bool isError)
    {
        _readFileIsError = isError;
        ReadFileNotice = message ?? "";
        OnPropertyChanged(nameof(ShowsReadFileNotice));
        OnPropertyChanged(nameof(ShowsReadFileError));
        if (storedName is not null)
        {
            LastStoredFileName = storedName;
            SetIdentity(storedName, FileOrigin.ReadThisSession);
        }
    }

    /// <summary>Empty the READ slot, both labels with it. Silent when there was
    /// nothing on screen, so a read never announces a notice it did not
    /// change.</summary>
    private void ClearReadFileNotice()
    {
        if (ReadFileNotice.Length == 0 && !_readFileIsError) return;
        _readFileIsError = false;
        ReadFileNotice = "";
        OnPropertyChanged(nameof(ShowsReadFileNotice));
        OnPropertyChanged(nameof(ShowsReadFileError));
    }

    /// <summary>The view reports the OPEN step here. A REJECTED file leaves the
    /// identity untouched, matching <see cref="LoadJson"/>'s keep-the-previous
    /// contract — so <paramref name="loadedName"/> is null on a rejection.</summary>
    public void NoteOpenFileOutcome(string message, string? loadedName, bool isError)
    {
        _openFileIsError = isError;
        OpenFileNotice = message ?? "";
        OnPropertyChanged(nameof(ShowsOpenFileNotice));
        OnPropertyChanged(nameof(ShowsOpenFileError));
        if (loadedName is not null) SetIdentity(loadedName, FileOrigin.Opened);
    }

    /// <summary>The file's JSON for the view to save/share. Null when there is
    /// nothing to save.</summary>
    public string? BuildJson() => _clone.File is null ? null : _clone.SaveJson();

    /// <summary>Take a picked file's text. A rejected file leaves the previous
    /// one in place and returns the reason, naming the offender.</summary>
    public string? LoadJson(string json)
    {
        // THE ORDERING RULE (§6): `CloneService.LoadJson` raises `Changed`
        // SYNCHRONOUSLY, before it returns — so the route for the event this
        // call is about to fire has to be chosen HERE, not afterwards. A load
        // belongs to the Write tab (D11): that is where the file it produced
        // is shown, and where its downgrade notices must land.
        _lastRan = CloneOp.Write;
        try
        {
            _clone.LoadJson(json);
        }
        catch (CloneFileFormatException ex)
        {
            RefreshGates();
            return ex.Message;
        }
        RefreshGates();
        RefreshOperation(CloneOp.Write);
        return null;
    }

    // ---- Rendering ---------------------------------------------------------

    /// <summary>
    /// The GATES half of the old monolithic refresh: file-derived state, the
    /// identity table, the gating reasons, busy state and every command's
    /// CanExecute. It never touches a report list — which is what lets a
    /// cleared report STAY cleared through a session-phase or ALE event, while
    /// <see cref="HasFile"/>, <see cref="FileLine"/> and the write gating stay
    /// live.
    /// </summary>
    private void RefreshGates()
    {
        RebuildSelfRows();
        OnPropertyChanged(nameof(Dispositions));
        OnPropertyChanged(nameof(IdentityError));
        OnPropertyChanged(nameof(HasIdentityError));
        OnPropertyChanged(nameof(ShowsBookNotRead));
        OnPropertyChanged(nameof(FillGateReason));
        OnPropertyChanged(nameof(HasFillGateReason));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(CanStore));
        OnPropertyChanged(nameof(FileLine));
        OnPropertyChanged(nameof(CanRead));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(ReadGateReason));
        OnPropertyChanged(nameof(HasReadGateReason));
        OnPropertyChanged(nameof(WriteGateReason));
        OnPropertyChanged(nameof(HasWriteGateReason));
        ReadCommand.NotifyCanExecuteChanged();
        WriteCommand.NotifyCanExecuteChanged();
        ClearReadReportCommand.NotifyCanExecuteChanged();
        ClearWriteReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The OPERATION half: one tab's status line and one tab's report,
    /// copied from the service. Nothing else in the card moves.</summary>
    private void RefreshOperation(CloneOp target)
    {
        switch (target)
        {
            case CloneOp.Read:
                ReadStatusText = _clone.StatusText;
                RebuildReport(ReadReportLines, nameof(HasReadReport), ClearReadReportCommand);
                break;
            case CloneOp.Write:
                WriteStatusText = _clone.StatusText;
                RebuildReport(WriteReportLines, nameof(HasWriteReport), ClearWriteReportCommand);
                break;
            default:
                break;
        }
    }

    /// <summary>D6: a new run REPLACES its own tab's report, so the list is
    /// emptied before the first line of the new one can arrive.</summary>
    private void StartReport(ObservableCollection<string> lines, string hasName, IRelayCommand clear)
    {
        if (lines.Count == 0) return;
        lines.Clear();
        OnPropertyChanged(hasName);
        clear.NotifyCanExecuteChanged();
    }

    private void RebuildReport(ObservableCollection<string> lines, string hasName, IRelayCommand clear)
    {
        var wanted = _clone.Summary;
        if (lines.SequenceEqual(wanted, StringComparer.Ordinal)) return;
        lines.Clear();
        foreach (var line in wanted) lines.Add(line);
        OnPropertyChanged(hasName);
        clear.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The table follows the FILE. It is rebuilt only when the file's shape
    /// really changed — the selfs it offers, the nets that title them and the
    /// individuals a swap can pick — because <see cref="RefreshGates"/> runs on
    /// every campaign tick, and a rebuild there would throw away what the
    /// operator was typing.
    ///
    /// <para>ROUND 15 C-1: the rows are per NET. One row per self still, in
    /// the file's own order (the primary first), but a self that ≥1 net names
    /// is TITLED by those nets and its picker is SCOPED to that net's own
    /// individuals — cloning a net promotes one of THAT net's stations. A
    /// self no net names keeps its own name as the title and offers the
    /// individuals associated with it (none → no picker at all).</para>
    /// </summary>
    private void RebuildSelfRows()
    {
        var file = _clone.File;
        // C-Q5: an unread address book offers NO rows — a caption says why,
        // and the write is blocked by the manifest anyway.
        bool bookRead = file is not null && file.BookState == CloneDomainState.Read;
        List<string> selfs = bookRead ? [.. file!.Selfs.Select(s => s.Name)] : [];
        // The no-self file gets ONE synthetic row: choosing a name is what
        // repairs it (A-6), so the row that asks for one has to exist.
        List<string> wanted = !bookRead ? [] : selfs.Count > 0 ? selfs : [""];

        var nets = wanted.Select(name => NetsOf(file, name)).ToList();
        var candidates = wanted
            .Select(name => (IReadOnlyList<string>)
                [.. CloneSwap.SwapCandidates(file ?? new CloneFile(), name).Select(c => c.Name)])
            .ToList();

        if (SelfRows.Count == wanted.Count
            && SelfRows.Select(r => r.SelfName).SequenceEqual(wanted, StringComparer.Ordinal)
            && SelfRows.Select((r, i) =>
                string.Equals(r.Title, SelfRowViewModel.TitleFor(wanted[i], nets[i]), StringComparison.Ordinal)
                && r.Nets.SequenceEqual(nets[i].Select(n => n.Name), StringComparer.Ordinal)
                && r.SwapChoices.Skip(1).SequenceEqual(candidates[i], StringComparer.Ordinal)).All(same => same))
            return;

        foreach (var row in SelfRows) row.PropertyChanged -= OnSelfRowChanged;
        SelfRows.Clear();
        for (int i = 0; i < wanted.Count; i++)
        {
            var row = new SelfRowViewModel(wanted[i], nets[i], candidates[i]);
            row.PropertyChanged += OnSelfRowChanged;
            SelfRows.Add(row);
        }
    }

    /// <summary>The nets this self is associated to, in the file's own net
    /// order — what titles the row (C-1). A net whose association is blank
    /// names no self, so it titles nothing (the blank-assoc net stays
    /// unwritable, as it was).</summary>
    private static IReadOnlyList<(string Name, int Group)> NetsOf(CloneFile? file, string selfName)
    {
        if (file is null || selfName.Length == 0) return [];
        string self = CloneFile.Normalize(selfName);
        return
        [
            .. file.Nets
                .Where(n => !string.IsNullOrWhiteSpace(n.AssociatedSelf)
                    && string.Equals(CloneFile.Normalize(n.AssociatedSelf!), self, StringComparison.Ordinal))
                .Select(n => (n.Name, n.Group)),
        ];
    }

    private void OnSelfRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshGates();
    }
}

/// <summary>
/// ONE row of the Cloning card's identity table (R-A, §3.3): a self in the
/// loaded file and what the operator decided to do with it.
///
/// <para><b>Row exclusivity (A-1).</b> Swap and Replace are alternatives, not a
/// pair to combine: typing a replacement clears the swap pick, picking a swap
/// clears the typed name, and leaving both alone is Keep. One explainable state
/// per row — which is exactly what round 11's "the Entry wins over the picker"
/// rule was working around.</para>
/// </summary>
public sealed partial class SelfRowViewModel : ObservableObject
{
    /// <summary>The swap picker's first position: "do not swap this self".
    /// A Picker with no selection and a Picker holding this mean the same
    /// thing, so <see cref="ToDisposition"/> reads them the same way.</summary>
    public const string KeepChoice = "(keep)";

    /// <summary>The caption the scan-gate row carries, quoted verbatim by
    /// `docs/ui.md` (the docs gate reads it from HERE, so the doc cannot drift
    /// into describing a rule the card does not enforce).</summary>
    public const string ScanGateCaption =
        "Scan-gate self: replace with a 1-3 character name; swapping is not offered.";

    /// <summary>The longest replacement the scan-gate self accepts (D2).</summary>
    public const int ScanGateNameLength = 3;

    /// <summary>The longest name the radio stores at all.</summary>
    public const int MaxNameLength = 15;

    public SelfRowViewModel(string selfName, IReadOnlyList<(string Name, int Group)> nets, IReadOnlyList<string> individuals)
    {
        ArgumentNullException.ThrowIfNull(selfName);
        ArgumentNullException.ThrowIfNull(nets);
        ArgumentNullException.ThrowIfNull(individuals);

        SelfName = selfName;
        IsSyntheticRow = selfName.Length == 0;
        Nets = [.. nets.Select(n => n.Name)];
        Title = TitleFor(selfName, nets);
        // The synthetic row names no self at all, so it is not the scan-gate
        // one however short it looks: a post-ERASE file takes any valid name.
        IsScanGateSelf = !IsSyntheticRow && CloneSwap.IsScanGateSelf(selfName);
        // C-1: a row with no candidate of its own shows no picker at all —
        // an empty picker is a control that cannot do anything.
        OffersSwap = !IsSyntheticRow && !IsScanGateSelf && individuals.Count > 0;
        SwapChoices = [KeepChoice, .. individuals];
        NameLength = IsScanGateSelf ? ScanGateNameLength : MaxNameLength;
    }

    /// <summary>C-1: the row is titled by the NETS this self is associated to,
    /// because that is what the operator is cloning — "Net HFL · group 2 ·
    /// self W6HOS". A self several nets name is ONE row (one slot, one
    /// disposition), titled with all of them; a self no net names keeps its
    /// own name. Public because the card's rebuild guard compares the title it
    /// WOULD build against the one on screen — a net whose group changed
    /// renames no row, and a stale title is exactly the kind of drift that
    /// guard exists to catch.</summary>
    public static string TitleFor(string selfName, IReadOnlyList<(string Name, int Group)> nets)
    {
        if (selfName.Length == 0) return "No self — new name required";
        if (nets.Count == 0) return selfName;
        return nets.Count == 1
            ? $"Net {nets[0].Name} · group {nets[0].Group} · self {selfName}"
            : $"Nets {string.Join(", ", nets.Select(n => n.Name))} · self {selfName}";
    }

    /// <summary>The self as the FILE spells it; "" for the synthetic row.</summary>
    public string SelfName { get; }

    /// <summary>The nets this self is associated to, in file order (C-1) —
    /// what the <see cref="Title"/> is built from. Empty for a self no net
    /// names and for the synthetic row.</summary>
    public IReadOnlyList<string> Nets { get; }

    /// <summary>The row's heading: its nets, the self's name, or the
    /// instruction the no-self file needs.</summary>
    public string Title { get; }

    public bool IsSyntheticRow { get; }

    /// <summary>Swap is not offered and the replacement is capped at three
    /// characters (D2).</summary>
    public bool IsScanGateSelf { get; }

    public bool OffersSwap { get; }

    /// <summary>"(keep)" then this row's OWN individuals (C-1,
    /// <see cref="CloneSwap.SwapCandidates"/>) — the only names a swap can
    /// produce, and exactly the set <see cref="CloneSwap.Refusal"/> accepts.</summary>
    public IReadOnlyList<string> SwapChoices { get; }

    /// <summary>The Entry's MaxLength: the view enforces it at the keyboard and
    /// the <see cref="ReplaceInput"/> setter enforces it again, because a
    /// MaxLength is markup and markup is not the contract.</summary>
    public int NameLength { get; }

    [ObservableProperty]
    private string? _swapSelection;

    [ObservableProperty]
    private string _replaceInput = "";

    partial void OnSwapSelectionChanged(string? value)
    {
        // A-1: picking a swap clears the typed name.
        if (value is not null && !string.Equals(value, KeepChoice, StringComparison.Ordinal)
            && ReplaceInput.Length > 0)
            ReplaceInput = "";
    }

    partial void OnReplaceInputChanged(string value)
    {
        // D2: the scan-gate row REFUSES the extra characters rather than
        // storing a name the gate will not accept.
        if (value.Length > NameLength)
        {
            ReplaceInput = value[..NameLength];
            return;
        }
        // A-1: typing a replacement clears the swap pick.
        if (value.Trim().Length > 0
            && SwapSelection is not null && !string.Equals(SwapSelection, KeepChoice, StringComparison.Ordinal))
            SwapSelection = KeepChoice;
    }

    /// <summary>What this row means to the transform. Both controls empty is
    /// Keep, which is what an omitted row means too.</summary>
    public SelfDisposition ToDisposition()
    {
        string typed = ReplaceInput.Trim();
        if (typed.Length > 0) return new SelfDisposition(SelfName, SelfDispositionKind.Replace, typed);
        if (SwapSelection is { Length: > 0 } pick && !string.Equals(pick, KeepChoice, StringComparison.Ordinal))
            return new SelfDisposition(SelfName, SelfDispositionKind.SwapWithIndividual, pick);
        return new SelfDisposition(SelfName, SelfDispositionKind.Keep, null);
    }
}
