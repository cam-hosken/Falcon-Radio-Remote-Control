using CommunityToolkit.Maui.Storage;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views;

public partial class RadioSettingsPage : ContentPage
{
    private readonly ConsoleViewModel _console;
    private readonly DeviceSettingsViewModel _device;
    private readonly CloneViewModel _clone;

    public RadioSettingsPage(RadioSettingsViewModel viewModel, ConsoleViewModel console,
        DeviceSettingsViewModel device, CloneViewModel clone)
    {
        _console = console;
        _device = device;
        _clone = clone;
        InitializeComponent();
        BindingContext = viewModel;
        // E4: the relocated Console sub-tab and the Settings sub-tab each bind
        // their own VM; the page root keeps the tab-state VM (the SettingsPage
        // RadioPortSection pattern).
        ConsoleSection.BindingContext = console;
        SettingsSection.BindingContext = device;
        // Round 11 §9A: the Cloning card gets its own VM the same way.
        CloningSection.BindingContext = clone;
        // D13: the one-shot legacy sweep, at the first Cloning-card use of the
        // app run (this page is transient behind a singleton VM, so its
        // constructor is the earliest seam that runs once the operator has
        // actually reached the card — and the sweep's own latch makes it once).
        SweepLegacyStoredClones();
    }

    /// <summary>Plan N4 (device-settings lazy load): query the mode-free set
    /// once per session when the tab appears while Ready. EnsureLoaded is
    /// once-per-session-guarded and idempotent, so re-appearing does not
    /// re-query (the mirror is the cache).</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _device.EnsureLoaded();
    }

    private async void OnCopyClicked(object? sender, EventArgs e)
        => await Clipboard.Default.SetTextAsync(_console.GetLogText());

    /// <summary>
    /// D22 (plan-clone-write-structural.md §2; manager finding on the 2026-08-30
    /// solo bench run): WHAT THE OUTCOME SLOT SAYS WHILE AN EXPORT IS IN FLIGHT.
    /// A save or share picker can end up BEHIND the app window — on that run a
    /// hidden "This file is in use" retry dialog held the gate for minutes — and
    /// the in-flight bit greys Store and Share for exactly as long, with nothing
    /// on screen to say why. "The store just stops working" is what that looks
    /// like from the operator's chair.
    ///
    /// <para>ONE string for BOTH cards (the manifest's own row,
    /// plan-clone-pane-cleanup §7), because the same press must not be called two
    /// things in one app. It is a CAPTION, never the error style: waiting is not a
    /// failure. It is written where the gate is TAKEN and is replaced by the
    /// outcome row, or CLEARED, at every ending — see each press.</para>
    ///
    /// <para>I-3/R13: no radio token. The picker is the operator's own system
    /// dialog and the sentence names nothing else.</para>
    /// </summary>
    private const string ExportWaitText = "Waiting on the save dialog…";

    // ---- D17: the Console's export, on the D13 model ------------------------
    // Stage 8 (plan §4.5), relocated with the Console (E4): export the log as a
    // text file — a leftover filter must never silently narrow a bench report.
    // The `falcon-console-<timestamp>.txt` name and the line format are
    // unchanged.
    //
    // D19 (plan-clone-write-structural.md §2, 2026-08-30) CHANGED THE SOURCE
    // and nothing else: both presses read `GetSessionLogText()` — the feed's
    // FULL-SESSION log — where they used to read `GetFullLogText()`, the
    // display's own 500-line store. The 2026-08-30 live gate failed and could
    // not be diagnosed because the failing write's root window had scrolled out
    // of that 500-line store before the operator pressed Store. The DISPLAY is
    // untouched (still 500, still pausable, still filterable) and Copy above
    // still copies the VISIBLE filtered log.
    //
    // WHAT CHANGED (D17, owner 2026-08-30 — "look at the console… unify the
    // save functionality across that too"): the single platform-split press is
    // GONE. It shared a file on Android and, on Windows, wrote SILENTLY into
    // the operator's own Documents folder — the last silent durable write left
    // in the app after D13 took the Cloning card's away. The Console now
    // carries the SAME PAIR as the card: "Store file…" through the system
    // save-location picker (the operator chooses, on both platforms), "Share…"
    // through the share sheet from a purgeable CACHE copy. Same outcome rows,
    // same silences, same error styling.
    //
    // MIRRORED, NOT SHARED. The two cards' presses are the same shape line for
    // line, but the card's report their outcome through `CloneViewModel`
    // (`NoteReadFileOutcome`, which also PROMOTES a stored name onto the Write
    // tab's file line) while the Console's report through a view-only label and
    // promote nothing. Folding the two into one helper would move the card's
    // body out of the handler its D13 pins read, so the card's behaviour is
    // left byte-identical and the Console mirrors it.

    /// <summary>
    /// D17: STORE the console log — through the SYSTEM SAVE-LOCATION PICKER,
    /// exactly as the Cloning card's Store does. The operator picks the
    /// destination and the toolkit writes the bytes through its own stream;
    /// this app never picks a path and never writes into Documents.
    ///
    /// <para>A DISMISSED PICKER SAYS NOTHING — the toolkit reports a cancel as
    /// a failed result carrying a <see cref="FileSaveException"/>, the only
    /// discriminator the library offers, and anything else takes the error
    /// row.</para>
    /// </summary>
    private async void OnConsoleStoreClicked(object? sender, EventArgs e)
    {
        // THE IN-FLIGHT GATE, the card's one-at-a-time rule in the Console's
        // own scope: both presses are `async void`, so nothing stops a
        // double-tap from opening a second picker — or a picker over a share
        // sheet — on top of the first. The bit is a VIEW field rather than the
        // card's VM bit: the Console section binds `ConsoleViewModel`, and
        // reaching into `CloneViewModel` from here would tie two unrelated
        // cards together. Given back in a `finally`, so a throw cannot wedge it.
        if (_consoleExporting) return;
        _consoleExporting = true;
        try
        {
            // D22: the wait is VISIBLE, from the moment the gate is taken. Every
            // ending below replaces it — with the outcome, or with nothing.
            ShowExportNotice(ExportWaitText, isError: false);
            var name = ConsoleExportFileName();
            using var bytes = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(_console.GetSessionLogText()));
            var result = await FileSaver.Default.SaveAsync(name, bytes, CancellationToken.None);

            if (result.IsSuccessful)
                ShowExportNotice($"stored: {result.FilePath}", isError: false);
            else if (result.Exception is FileSaveException or OperationCanceledException)
            {
                // D22: dismissed still says NOTHING — so the wait line is CLEARED
                // rather than left standing over a picker that is gone.
                ShowExportNotice("", isError: false);
                return;
            }
            else
                ShowExportNotice($"save failed: {result.Exception?.Message}", isError: true);
        }
        catch (Exception ex)
        {
            ShowExportNotice($"save failed: {ex.Message}", isError: true);
        }
        finally
        {
            _consoleExporting = false;
        }
    }

    /// <summary>
    /// D17: SHARE the console log — the system share sheet, from a CACHE copy,
    /// which is the known-good FileProvider path. The cache is purgeable and
    /// that is the point: the copy exists only long enough for the receiving
    /// app to take it.
    ///
    /// <para>A DISMISSED SHEET IS NOT A FAILURE and does not throw — the
    /// request simply completes — so the notice reads the same either way, as
    /// it did before D17. A share that really failed takes the error row.
    /// WINDOWS CAVEAT, shared with the card: an unpackaged exe has no share
    /// broker, so the sheet may throw there and the press lands on
    /// "share failed" — which is why Store is the press that works
    /// everywhere.</para>
    /// </summary>
    private async void OnConsoleShareClicked(object? sender, EventArgs e)
    {
        if (_consoleExporting) return;
        _consoleExporting = true;
        try
        {
            // D22: the wait is VISIBLE from the gate. The share sheet has no
            // cancel of its own (a dismissal completes the request), so both of
            // this press's endings REPLACE this line.
            ShowExportNotice(ExportWaitText, isError: false);
            var name = ConsoleExportFileName();
            // CACHE ONLY (D13/D17). Nothing durable is written by this file.
            var copy = Path.Combine(FileSystem.CacheDirectory, name);
            await File.WriteAllTextAsync(copy, _console.GetSessionLogText());
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = name,
                File = new ShareFile(copy),
            });
            ShowExportNotice($"shared: {name}", isError: false);
        }
        catch (Exception ex)
        {
            ShowExportNotice($"share failed: {ex.Message}", isError: true);
        }
        finally
        {
            _consoleExporting = false;
        }
    }

    /// <summary>The name both Console presses offer — the buffer's existing
    /// timestamped convention, unchanged by D17. ONE copy, so the picker and
    /// the share sheet cannot drift into naming the same log two different
    /// things (the card's <c>ExportFileName</c> rule; this one promotes
    /// nothing, because the Console keeps no file identity).</summary>
    private static string ConsoleExportFileName()
        => $"falcon-console-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

    /// <summary>The Console's export in-flight bit — see the gate note on the
    /// Store press. View state, not VM state: it guards only these two.</summary>
    private bool _consoleExporting;

    /// <summary>ONE notice slot, TWO labels (the Cloning card's idiom): a clean
    /// export never appears in the error style, and a failure never appears as
    /// an ordinary caption.
    ///
    /// <para>D22: an EMPTY message empties the slot, both labels with it — the
    /// same <c>Length &gt; 0</c> rule <c>CloneViewModel.ShowsReadFileNotice</c>
    /// applies on the card's side, so the two slots hide on the same condition.
    /// It is what a dismissed picker leaves behind after the wait line.</para>
    /// </summary>
    private void ShowExportNotice(string message, bool isError)
    {
        ExportNotice.Text = message;
        ExportNotice.IsVisible = message.Length > 0 && !isError;
        ExportError.Text = message;
        ExportError.IsVisible = message.Length > 0 && isError;
    }

    // ---- Round 11 §9A / D13: the Cloning card's file I/O --------------------
    // The campaigns are the ViewModel's; the PATH is the view's. The VM never
    // touches a path, which is what keeps it MAUI-free and testable.
    //
    // D13 (plan-clone-write-structural, owner 2026-08-30) REPLACED THE
    // PERSISTENCE MODEL, deliberately. Until D12 a read wrote a timestamped
    // copy into Android app-private storage (or, on Windows, silently into
    // Documents) and popped the share sheet once. That is gone: app storage was
    // ballooning with reads the operator could not browse, and the share sheet's
    // only sensible destination dumped files at the root of a Drive. NOW:
    //
    //   * a read persists NOTHING — it lives in memory, and the Write tab's
    //     file line says "(not saved)" until the operator does something;
    //   * "Store file…" opens the SYSTEM SAVE-LOCATION PICKER, so the operator
    //     chooses the destination and the file is written through the picker's
    //     own stream — this app never picks the path;
    //   * "Share…" offers the share sheet from a CACHE copy.
    //
    // NOTHING DURABLE IS WRITTEN BY THIS FILE. The only staging path is
    // `FileSystem.CacheDirectory`, which the OS may purge; `AppDataDirectory`
    // is touched exactly once, to DELETE the legacy files (see
    // `SweepLegacyStoredClones`), and Documents is not touched at all — a file
    // there would be the operator's, and this app does not put files in it
    // behind their back.

    /// <summary>
    /// D13: the ONE-SHOT LEGACY SWEEP. Before D13 every Android read left a
    /// <c>falcon-clone-&lt;timestamp&gt;.falconclone.json</c> in
    /// <c>AppDataDirectory</c>, invisible to the operator and never cleaned up —
    /// the owner's phone was carrying six stranded reads. This deletes them, and
    /// any later stray, once per app run.
    ///
    /// <para>ANDROID ONLY, and DELETE ONLY. The Windows build's old copies went
    /// to the operator's own Documents folder: those are THEIRS, and an app that
    /// deleted files out of a user's Documents would be doing something far
    /// worse than leaving them there.</para>
    ///
    /// <para>Every failure is swallowed. A sweep that threw would take the whole
    /// page's construction with it, and a stranded file is not worth a card that
    /// will not open.</para>
    /// </summary>
    private static void SweepLegacyStoredClones()
    {
        if (_sweptLegacyClones) return;
        _sweptLegacyClones = true;
#if ANDROID
        try
        {
            foreach (var stale in Directory.EnumerateFiles(
                         FileSystem.AppDataDirectory, "falcon-clone-*.falconclone.json"))
            {
                try { File.Delete(stale); }
                catch (Exception) { /* one stubborn file must not stop the rest */ }
            }
        }
        catch (Exception) { /* silent by design — see the summary */ }
#endif
    }

    /// <summary>The sweep's once-per-app-run latch. The page is transient behind
    /// a singleton VM, so its constructor runs on every visit to the tab.</summary>
    private static bool _sweptLegacyClones;

    /// <summary>
    /// Run the READ campaign — AND STOP. A campaign that came back with gaps
    /// still leaves its file in hand: the file carries the FAULTED markers, and
    /// the write preflight is what refuses it. Losing the read would be worse
    /// than keeping an honest partial.
    ///
    /// <para>D13: THE HANDLER OWNS NO EXPORT AT ALL. A read used to persist its
    /// file and pop the share sheet from here; now it saves nothing, shares
    /// nothing, shows no dialog and writes no file notice — there is no file
    /// step to report. The Write tab's file line says
    /// <c>"Read from this radio (not saved)"</c>, which is the true state, and
    /// the two presses below are how a file leaves the app. This also retires
    /// the audit-round-1 stale-read guard by construction: a read that installed
    /// nothing can no longer save the PREVIOUSLY LOADED file under a fresh
    /// "read from this radio" name, because it cannot save anything.</para>
    /// </summary>
    private async void OnCloneReadClicked(object? sender, EventArgs e)
    {
        if (!_clone.CanRead) return;
        await _clone.ReadCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// D13: STORE the file in hand — through the SYSTEM SAVE-LOCATION PICKER.
    /// The operator picks the destination (a Drive folder, the phone's
    /// Downloads, anywhere the picker offers) and the toolkit writes the bytes
    /// there through its own stream; this app learns only the location the
    /// picker hands back.
    ///
    /// <para>The picker is SEEDED with the file's name — the one it is already
    /// stored under when a previous Store landed, a fresh
    /// <c>falcon-clone-&lt;timestamp&gt;.falconclone.json</c> otherwise — so
    /// storing the same read twice offers the same name rather than a new
    /// timestamp per press.</para>
    ///
    /// <para>A DISMISSED PICKER SAYS NOTHING. The toolkit reports a cancel as a
    /// failed result carrying a <see cref="FileSaveException"/> (its message is
    /// the platform's own "…doesn't exist" wording), which is the only
    /// discriminator the library offers; anything else is a real failure and
    /// takes the error row. Classifying an unexpected
    /// <c>FileSaveException</c> as a cancel is the safe direction: nothing was
    /// written either way, and a silent no-op cannot claim a file was stored
    /// when it was not.</para>
    ///
    /// <para>Both impossible presses are harmless: the button is bound to
    /// <c>CanStore</c>, and a forced press with no file gets a null
    /// <c>BuildJson</c> and does nothing.</para>
    /// </summary>
    private async void OnCloneStoreClicked(object? sender, EventArgs e)
    {
        if (!_clone.CanStore) return;
        var json = _clone.BuildJson();
        if (json is null) return;

        // THE IN-FLIGHT GATE (audit round 1, kept through D13). Both presses are
        // `async void`, so nothing stops a double-tap from opening a second
        // picker — or a picker over a share sheet — on top of the first. ONE at
        // a time; the bit lives on the VM so BOTH buttons grey while one runs,
        // and it is given back in a `finally` so a throw can neither wedge them
        // grey nor let the next press through as if it were the first.
        if (_clone.IsExporting) return;
        _clone.SetExporting(true);
        try
        {
            // D22: the wait is VISIBLE, from the moment the gate is taken —
            // through the SAME notice channel the outcome rows use (no parallel
            // channel), with no name promoted and never in the error style.
            _clone.NoteReadFileOutcome(ExportWaitText, null, isError: false);
            var name = ExportFileName();
            using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var result = await FileSaver.Default.SaveAsync(name, bytes, CancellationToken.None);

            if (result.IsSuccessful)
                // THE PROMOTION POINT (D13). A file the operator has just placed
                // somewhere really is stored under this name, which is what lets
                // the Write tab stop calling it "not saved".
                _clone.NoteReadFileOutcome("", name, isError: false);   // silent success (owner 2026-08-30); the name still promotes
            else if (result.Exception is FileSaveException or OperationCanceledException)
            {
                // D22: dismissed still says NOTHING — so the wait line is CLEARED
                // rather than left standing over a picker that is gone. An empty
                // message empties the slot (ShowsReadFileNotice/Error both go
                // false) and promotes no name.
                _clone.NoteReadFileOutcome("", null, isError: false);
                return;
            }
            else
                // No name is promoted: the Write tab must not name a file that
                // was never written.
                _clone.NoteReadFileOutcome(
                    $"save failed: {result.Exception?.Message}", null, isError: true);
        }
        catch (Exception ex)
        {
            _clone.NoteReadFileOutcome($"save failed: {ex.Message}", null, isError: true);
        }
        finally
        {
            _clone.SetExporting(false);
        }
    }

    /// <summary>
    /// D13: SHARE the file in hand — the system share sheet, from a CACHE copy,
    /// which is the known-good FileProvider path. The cache is purgeable and
    /// that is the point: the copy exists only long enough for the receiving app
    /// to take it.
    ///
    /// <para>It PROMOTES NO NAME. A shared file has gone somewhere this app
    /// cannot see and cannot re-open, so the card would be inventing a claim by
    /// calling the file "stored"; the save-location picker is the one press that
    /// knows where the file landed.</para>
    ///
    /// <para>A DISMISSED SHEET IS NOT A FAILURE and does not throw — the request
    /// simply completes — so the notice reads the same either way. A share that
    /// really failed DOES take the error row now: nothing else kept a copy of
    /// this file, which is exactly what changed under D13.</para>
    /// </summary>
    private async void OnCloneShareClicked(object? sender, EventArgs e)
    {
        if (!_clone.CanStore) return;
        var json = _clone.BuildJson();
        if (json is null) return;

        if (_clone.IsExporting) return;
        _clone.SetExporting(true);
        try
        {
            // D22: the wait is VISIBLE from the gate. A dismissed share sheet is
            // not a cancel — the request simply completes — so both of this
            // press's endings REPLACE this line.
            _clone.NoteReadFileOutcome(ExportWaitText, null, isError: false);
            var name = ExportFileName();
            // CACHE ONLY (D13). Nothing durable is written by this app.
            var copy = Path.Combine(FileSystem.CacheDirectory, name);
            await File.WriteAllTextAsync(copy, json);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = name,
                File = new ShareFile(copy),
            });
            _clone.NoteReadFileOutcome("", null, isError: false);   // silent success (owner 2026-08-30)
        }
        catch (Exception ex)
        {
            _clone.NoteReadFileOutcome($"share failed: {ex.Message}", null, isError: true);
        }
        finally
        {
            _clone.SetExporting(false);
        }
    }

    /// <summary>The name both presses offer for the file in hand: the one it is
    /// ALREADY stored under when a picker save has landed, and a fresh
    /// timestamped name otherwise. ONE copy, so the picker and the share sheet
    /// cannot drift into naming the same file two different things.</summary>
    private string ExportFileName()
        => _clone.LastStoredFileName ?? $"falcon-clone-{DateTime.Now:yyyyMMdd-HHmmss}.falconclone.json";

    /// <summary>Pick a clone file back in. A REJECTED file (unknown version,
    /// malformed row, duplicate name) leaves the previously loaded one in
    /// place — and its identity with it — and puts the reason, with the
    /// offender named, on the card in the error style.</summary>
    private async void OnCloneOpenClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await FilePicker.Default.PickAsync();
            if (picked is null) return;                     // cancelled: nothing is said
            var json = await File.ReadAllTextAsync(picked.FullPath);
            var rejection = _clone.LoadJson(json);
            if (rejection is null)
                _clone.NoteOpenFileOutcome($"loaded: {picked.FileName}", picked.FileName, isError: false);
            else
                _clone.NoteOpenFileOutcome(rejection, null, isError: true);
        }
        catch (Exception ex)
        {
            _clone.NoteOpenFileOutcome($"open failed: {ex.Message}", null, isError: true);
        }
    }
}
