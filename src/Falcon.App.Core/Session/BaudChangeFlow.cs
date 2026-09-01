using System.Globalization;
using Falcon.Core.Radio;
using Falcon.Core.Transport;

namespace Falcon.App.Core.Session;

/// <summary>Where the guarded baud change stands. Terminal states are
/// <see cref="NoOp"/>, <see cref="Done"/> and <see cref="Failed"/>.</summary>
public enum BaudChangeState
{
    Idle,
    /// <summary>Same-rate selection: nothing was sent (with the reason in
    /// <see cref="BaudChangeFlow.StatusText"/>).</summary>
    NoOp,
    Sending,
    /// <summary>PORT_R BAUD is out; the session DROPPING is the SUCCESS
    /// signal (the radio answers nothing intelligible at the old rate after
    /// the change). Detected by a sentinel ping timing out; a ping that gets
    /// ANSWERED means the radio ignored the command → Failed.</summary>
    WaitingForDrop,
    Reopening,
    Verifying,
    Done,
    Failed,
}

/// <summary>
/// The guarded radio-side baud wizard's engine (plan §7 decision 3, Stage
/// 11): warn → send <c>PORT_R BAUD n</c> via the whitelisted builder → the
/// session drops BY DESIGN (the drop is the success signal) → close the
/// port → reopen at the new rate → full connect ritual → verify the PORT_R
/// dump in the state mirror (BAUD line = target) → report the outcome.
///
/// UI TWEAKS ROUND 10 (§5, owner ruling 9) — where the confirmation lives:
/// Core NO LONGER GATES this command. <see cref="Prc138Radio.SetRemoteBaud"/>
/// took a typed confirmation token until this round; it does not now, and
/// this flow no longer passes one. The ONLY confirmation stop on the path is
/// <c>RadioPortViewModel</c>'s typed-match guard, which its command body
/// re-checks (Execute never consults CanExecute). That wizard GUI does not
/// currently exist — the backend is dormant — so the guard is kept, not
/// relied upon, until a wizard returns. Wire-level containment is unchanged:
/// "PORT_R BAUD" stays a FORBIDDEN prefix for every other sender.
///
/// Failure paths (§3 guarded-flow vocabulary — the wizard never leaves the
/// operator guessing which rate the radio is at):
/// - Same-rate selection: no-op with reason, nothing sent.
/// - The drop never happens (the sentinel is ANSWERED): the radio ignored
///   the command; the session is untouched → Failed.
/// - Reopen at the new rate fails: retry BOTH rates — new rate first, then
///   the old rate (the radio is at one of them) — and report which
///   answered. Neither answering → Failed naming front-panel recovery.
/// - Verify mismatch: connected but the dump's BAUD line is not the target
///   → Failed with what the radio reported.
///
/// Session-layer component (like <see cref="RadioSession"/>, it owns the
/// radio handle directly — ViewModels reach it only through this flow).
/// Auto-reconnect is suspended for the duration: the poller would otherwise
/// re-dial the OLD rate mid-flow. All inputs (PhaseChanged, ping callbacks)
/// arrive marshalled where a SynchronizationContext exists; a lock keeps
/// the state machine consistent when they do not (bench harness).
/// </summary>
public sealed class BaudChangeFlow
{
    private readonly Prc138Radio _radio;
    private readonly RadioSession _session;
    private readonly object _lock = new();

    private string? _port;
    private int _oldBaud;
    private int _target;
    private bool _savedAutoReconnect;
    private bool _autoReconnectSuspended;
    private readonly Queue<int> _attempts = new();
    private bool _retryPassUsed;

    public BaudChangeState State { get; private set; } = BaudChangeState.Idle;
    /// <summary>Operator-facing progress / outcome text for the current state.</summary>
    public string StatusText { get; private set; } = "";
    /// <summary>The rate the current reopen attempt is dialing (Reopening).</summary>
    public int? AttemptBaud { get; private set; }
    /// <summary>Which rate the radio ANSWERED at (set on any successful
    /// reopen — the wizard's "which rate answered" report).</summary>
    public int? AnsweredBaud { get; private set; }
    public int? TargetBaud { get; private set; }

    public bool IsRunning => State is BaudChangeState.Sending or BaudChangeState.WaitingForDrop
        or BaudChangeState.Reopening or BaudChangeState.Verifying;

    /// <summary>Sentinel timeout that detects the drop. Covers the prompt
    /// gate's 2 s release plus margin. Test hook.</summary>
    public int DropTimeoutMs { get; set; } = 5_000;
    /// <summary>Timeout for the PORT_R re-query during verification when the
    /// connect ritual's own dump was swallowed. Test hook.</summary>
    public int VerifyTimeoutMs { get; set; } = 5_000;
    /// <summary>Delay between closing the port and dialing the next reopen
    /// attempt. MEASURED on the Stage 11 live gate (2026-08-03): reopening
    /// COM20 immediately after Close fails with access-denied — the Windows
    /// driver releases the handle asynchronously (the transport's teardown
    /// runs on a background task and the FTDI handle lingers briefly even
    /// after Close returns). Also spaces the dual-rate retry attempts.
    /// Test hook.</summary>
    public int ReopenDelayMs { get; set; } = 1_000;

    /// <summary>Rates the wizard offers (the app-supported set).</summary>
    public static IReadOnlyList<int> SupportedRates => Prc138Radio.SupportedRemoteBaudRates;

    public event EventHandler? Changed;

    public BaudChangeFlow(Prc138Radio radio, RadioSession session)
    {
        _radio = radio;
        _session = session;
        _session.PhaseChanged += (_, _) => OnPhaseChanged();
    }

    /// <summary>Begin the guarded flow.
    ///
    /// <para>UI tweaks round 10 (§5): the confirmation TOKEN parameter is
    /// gone — Core's <see cref="Prc138Radio.SetRemoteBaud(int)"/> no longer
    /// takes one (owner ruling 9: confirmation is a GUI concern for this
    /// sender). The caller-side gate is the wizard's own typed-match guard in
    /// <c>RadioPortViewModel</c>; the preflight below is unchanged and every
    /// refusal still sends NOTHING.</para></summary>
    public void Start(int targetBaud)
    {
        lock (_lock)
        {
            if (IsRunning) return;

            // Preflight (all refusals send NOTHING).
            if (_session.Phase != SessionPhase.Ready)
            {
                Set(BaudChangeState.Idle, "Not connected — the wizard needs a Ready session.");
                return;
            }
            if (!SupportedRates.Contains(targetBaud))
            {
                Set(BaudChangeState.Idle,
                    $"Unsupported rate {targetBaud} — supported: {string.Join("/", SupportedRates)}.");
                return;
            }
            _port = _session.PortName;
            _oldBaud = _session.BaudRate ?? 0;
            if (_port is null || _oldBaud == 0)
            {
                Set(BaudChangeState.Idle, "Session port settings unavailable.");
                return;
            }
            if (targetBaud == _oldBaud)
            {
                // Same-rate selection = no-op with reason, nothing sent.
                Set(BaudChangeState.NoOp,
                    $"The radio's remote port is already at {targetBaud} — nothing sent.");
                return;
            }

            _target = targetBaud;
            TargetBaud = targetBaud;
            AnsweredBaud = null;
            AttemptBaud = null;
            _attempts.Clear();
            _retryPassUsed = false;

            // Suspend auto-reconnect: an unexpected-disconnect poller firing
            // mid-flow would re-dial the OLD rate and fight the reopen.
            //
            // Theoretical race (audit round 1, F2 — recorded so a refactor
            // doesn't "fix" it into something worse): an unexpected
            // disconnect landing INSIDE the preflight window — after the
            // Ready check, before this suspension — arms the poller with the
            // toggle still true, and the poller may then re-establish the
            // session at the OLD rate. That is self-limiting: the drop probe
            // gets ANSWERED at the old rate → the flow reports an honest
            // Failed ("still answered — baud not changed") with the session
            // intact; and on the drop path the flow's own session.Close()
            // disarms the poller before any reopen dial. No corruption is
            // reachable — only a truthful failure report.
            _savedAutoReconnect = _session.AutoReconnectEnabled;
            _session.AutoReconnectEnabled = false;
            _autoReconnectSuspended = true;

            Set(BaudChangeState.Sending, $"Sending PORT_R BAUD {_target}…");
            try
            {
                _radio.SetRemoteBaud(_target);
            }
            catch (ArgumentException ex)
            {
                // Core refused before the wire — with the token gate gone
                // (round 10 §5) the only remaining refusal is the rate
                // validation, which this method's own preflight already
                // covers. Kept as the belt behind the braces: a Core-side
                // refusal must surface as a Failed flow with the reason, not
                // as an escaping exception. Nothing reached the wire.
                Finish(BaudChangeState.Failed, ex.Message);
                return;
            }

            Set(BaudChangeState.WaitingForDrop,
                $"Waiting for the session to drop (the radio stops answering at {_oldBaud} — that IS the success signal)…");
            _radio.Ping(OnDropProbe, DropTimeoutMs);
        }
    }

    /// <summary>Return a terminal flow to Idle (wizard close/reopen).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (IsRunning) return;
            AttemptBaud = null;
            AnsweredBaud = null;
            TargetBaud = null;
            Set(BaudChangeState.Idle, "");
        }
    }

    // ---- Drop detection ---------------------------------------------------

    private void OnDropProbe(bool answered)
    {
        lock (_lock)
        {
            if (State != BaudChangeState.WaitingForDrop) return;

            if (answered)
            {
                // The radio is still talking at the old rate: PORT_R BAUD did
                // not take effect. The session is intact — report and stop.
                Finish(BaudChangeState.Failed,
                    $"The radio still answered at {_oldBaud} — the baud was not changed; the session is unchanged.");
                return;
            }

            // Drop confirmed (sentinel unanswered / connection torn down):
            // the change took. Reopen at the new rate.
            _attempts.Clear();
            _attempts.Enqueue(_target);
            NextAttempt();
        }
    }

    // ---- Reopen (with the dual-rate retry) ----------------------------------

    /// <summary>Caller holds _lock.</summary>
    private void NextAttempt()
    {
        if (_attempts.Count == 0)
        {
            if (!_retryPassUsed)
            {
                // Reopen failed: retry BOTH rates — the radio is at one of
                // them. New rate first, then the old rate.
                _retryPassUsed = true;
                _attempts.Enqueue(_target);
                _attempts.Enqueue(_oldBaud);
            }
            else
            {
                Finish(BaudChangeState.Failed,
                    $"The radio answered at neither {_target} nor {_oldBaud} — the remote port may need front-panel recovery.");
                return;
            }
        }

        int rate = _attempts.Dequeue();
        AttemptBaud = rate;
        // Audit round 1, F3: the baud-scaled init watchdog stretches the
        // ritual at low rates — say so (unconditionally: simpler than
        // rate-gating one hint string).
        Set(BaudChangeState.Reopening,
            $"Reopening {_port} at {rate}… (reconnect attempts can take several minutes at low rates)");
        _session.Close();
        // The port handle is released asynchronously after Close (live-gate
        // measured: an immediate reopen gets access-denied) — dial after the
        // release delay. The timer is a singleton-lifetime resource, parked
        // between attempts (same pattern as HopViewModel's escape timer).
        _reopenTimer ??= new Timer(_ => DialPendingAttempt(), null,
            Timeout.Infinite, Timeout.Infinite);
        _reopenTimer.Change(Math.Max(ReopenDelayMs, 1), Timeout.Infinite);
    }

    private Timer? _reopenTimer;

    private void DialPendingAttempt()
    {
        string? port;
        int rate;
        lock (_lock)
        {
            if (State != BaudChangeState.Reopening || AttemptBaud is not int pending) return;
            port = _port;
            rate = pending;
        }
        // Connect outside the lock: it raises PhaseChanged inline when no
        // SynchronizationContext is installed (bench harness), and the
        // handler takes the lock itself.
        _session.Connect(new PortSettings { PortName = port, BaudRate = rate });
    }

    private void OnPhaseChanged()
    {
        lock (_lock)
        {
            switch (State)
            {
                case BaudChangeState.Reopening when _session.Phase == SessionPhase.Ready:
                    AnsweredBaud = AttemptBaud;
                    if (AttemptBaud == _target)
                    {
                        Set(BaudChangeState.Verifying,
                            $"Connected at {_target} — verifying the radio's PORT_R dump…");
                        Verify();
                    }
                    else
                    {
                        // The OLD rate answered: the change did not survive.
                        // The session is re-established there — report which
                        // rate answered, honestly, as a failure.
                        Finish(BaudChangeState.Failed,
                            $"The baud change did not take — the radio answered at {AttemptBaud} and the session was re-established there.");
                    }
                    break;

                case BaudChangeState.Reopening when _session.Phase == SessionPhase.Failed:
                    NextAttempt();
                    break;

                case BaudChangeState.Verifying when _session.Phase is SessionPhase.Failed or SessionPhase.Disconnected:
                    Finish(BaudChangeState.Failed,
                        $"The session dropped during verification — the radio last answered at {AnsweredBaud}.");
                    break;
            }
        }
    }

    // ---- Verify (the PORT_R dump in the state mirror) ------------------------

    /// <summary>Caller holds _lock. The connect ritual queries PORT_R before
    /// its sentinel, so the dump is normally already mirrored at Ready; if it
    /// was swallowed, one re-query + sentinel bounds the wait.</summary>
    private void Verify()
    {
        if (EvaluateDump(final: false)) return;
        _radio.QueryPortConfig();
        _radio.Ping(_ =>
        {
            lock (_lock)
            {
                if (State != BaudChangeState.Verifying) return;
                EvaluateDump(final: true);
            }
        }, VerifyTimeoutMs);
    }

    /// <summary>Caller holds _lock. Returns true when a verdict was reached.</summary>
    private bool EvaluateDump(bool final)
    {
        var baud = _radio.State.PortBaud;
        if (baud.IsConfirmed)
        {
            var expected = _target.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(baud.Value?.Trim(), expected, StringComparison.Ordinal))
                Finish(BaudChangeState.Done,
                    $"Radio baud changed to {_target} — PORT_R dump verified (BAUD {baud.Value?.Trim()}).");
            else
                Finish(BaudChangeState.Failed,
                    $"Connected at {_target} but the radio reports BAUD {baud.Value?.Trim()} — dump mismatch.");
            return true;
        }
        if (final)
        {
            Finish(BaudChangeState.Failed,
                $"Connected at {_target} but the PORT_R dump did not answer during verification.");
            return true;
        }
        return false;
    }

    // ---- Plumbing --------------------------------------------------------------

    /// <summary>Caller holds _lock.</summary>
    private void Finish(BaudChangeState terminal, string text)
    {
        if (_autoReconnectSuspended)
        {
            _session.AutoReconnectEnabled = _savedAutoReconnect;
            _autoReconnectSuspended = false;
        }
        Set(terminal, text);
    }

    /// <summary>Caller holds _lock (or is in Start's preflight).</summary>
    private void Set(BaudChangeState state, string text)
    {
        State = state;
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
