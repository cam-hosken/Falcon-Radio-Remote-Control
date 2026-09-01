using System.Globalization;
using System.Text.RegularExpressions;
using Falcon.Core.Radio;

namespace Falcon.Core.Protocol;

public sealed class ParseResult
{
    /// <summary>Uppercased first word of the line ("" for blank lines).</summary>
    public string Token { get; internal set; } = "";
    /// <summary>Uppercased payload after the token; null when absent.</summary>
    public string? Payload { get; internal set; }
    /// <summary>Original-case payload (message text etc.).</summary>
    public string? RawPayload { get; internal set; }
    public bool Handled { get; internal set; }
    /// <summary>True if applying the line changed a state value.</summary>
    public bool Changed { get; internal set; }
    /// <summary>Set when a recognized token carried an unparseable payload.</summary>
    public FormatException? PayloadError { get; internal set; }
}

/// <summary>
/// Maps radio response lines onto <see cref="RadioState"/>. Table-driven and
/// free of I/O. EVERY line is parsed standalone — no request/response
/// pairing. That is the radio-native model (challenge register Q1, validated
/// by the R2 triple-prompt capture: prompt arrival cannot pair a response to
/// its request on this radio). Async lines (POWER CUTBACK, sync states,
/// SCANNING, prompts) may arrive at any point.
/// The parser recognizes every token the radio can emit (it must survive
/// anything arriving); the COMMAND surface is v1-scoped separately.
/// </summary>
public sealed class ResponseParser
{
    private readonly RadioState _state;
    private readonly Dictionary<string, Action<Ctx>> _handlers;

    private enum Continuation { None, TxMsgText, RxMsgText, RankReport, ChgChans, HoplistFreqs }

    /// <summary>The received-AMD header awaiting its text line (Text still
    /// empty until <see cref="ApplyRxMsgText"/> fills it).</summary>
    private RxAmdMessage? _pendingRxMsg;
    private Continuation _continuation = Continuation.None;

    // ChgChans continuation state: the group whose CHANS listing may wrap, and
    // the channels accumulated so far (captured 2026-08-17: 20 channels per
    // line, so a full group is up to five lines).
    private int _chgGroup;
    private List<int> _chgChannels = [];

    // HoplistFreqs continuation state (captured 2026-08-17, phase 3): a
    // HOPLIST answer wraps at EIGHT frequencies per line, continuation lines
    // being bare 5-digit values - including inside a DIS record, where the
    // HOPLIST line is a LIST net's value line.
    private int _hoplistNet;
    private List<string> _hoplistFreqs = [];
    private int _pendingTxMsgSlot = -1;
    private string? _rankStation;
    private bool _inHelpBlock;

    /// <summary>Reset block-tracking state (used on connect).</summary>
    public void Reset()
    {
        _continuation = Continuation.None;
        _pendingTxMsgSlot = -1;
        _rankStation = null;
        _inHelpBlock = false;
        _lockoutSection = null;
    }

    /// <summary>End a WRAP continuation on a path that returns EARLY, before
    /// the continuation blocks can see the line (Sol audit finding 1). Scoped
    /// deliberately to the two wrap states: <c>TxMsgText</c>/<c>RankReport</c>
    /// have the same exposure but PREDATE the wrap work, and changing their
    /// blank/help behavior is a separate decision.</summary>
    private void EndWrapContinuation()
    {
        if (_continuation is Continuation.ChgChans or Continuation.HoplistFreqs)
            _continuation = Continuation.None;
    }

    // Async tokens that may interleave a RANK report without ending it
    // (bench: POWER / KEY / TUNE / scan chatter arrives at any time).
    private static readonly HashSet<string> RankReportSurvivors = new(StringComparer.Ordinal)
    {
        "CHAN:", "RANK", "POWER", "KEY", "TUNING", "TUNE",
        "NO_SYNC", "IN_SYNC", "AWAITING_SYNC", "SENDING_SYNC_REQ",
        "SYNC_REQ_RCV", "SENDING_SYNC_RSP", "SYNC_FAILED",
        "SCANNING", "SCAN", "CALLING", "SENDING", "LINKED",
        // Round 15 item I (critic F68): a SCHEDULED LQA can start while a RANK
        // listing is still printing. Its progress lines and the SH first line
        // must survive the continuation, or the report loses every remaining
        // "CHAN:" row.
        "SOUNDING", "EXCHANGE", "LQA/SOUND",
    };

    /// <summary>
    /// ROUND 16 FIXES S1 — a line that may INTERLEAVE a WRAPPED LISTING
    /// without ending it. Shared by BOTH wrap blocks below.
    ///
    /// <para>FULL-LINE matches over the trimmed, upper-cased line — never
    /// token membership (as <see cref="RankReportSurvivors"/> does). A wrap
    /// line is all-digit, so nothing here could be mistaken for one; the
    /// strictness is so that NO OTHER line is mistaken for an async one.</para>
    ///
    /// <para>Spelling: the radio prints <c>Wait...</c>,
    /// <c>Generating Hopset...</c>, <c>WB_Invalid</c> in mixed case and
    /// <c>KEY OFF </c> / <c> TUNING COUPLER </c> with padding; the predicates
    /// run over the TRIMMED UPPER line, so they are written upper-case and
    /// unpadded.</para>
    /// </summary>
    private static readonly Regex[] WrapSurvivors =
    [
        new(@"^SCANNING$"), new(@"^SCAN STOPPED$"), new(@"^KEY OFF$"), new(@"^IN_PROG$"),
        new(@"^BATTERY STATUS FULL \d+(?:\.\d+)?V$"),          // the voltage varies
        new(@"^POWER CUTBACK$"),
        new(@"^WAIT\.\.\.$"), new(@"^WB_INVALID$"), new(@"^GENERATING HOPSET\.\.\.$"),
        new(@"^TUNING COUPLER$"), new(@"^TUNE COMPLETE$"), new(@"^TUNE FAULT$"),
        // LQA progress. The COLUMN RUN between the station and `CHANNEL:` is
        // matched with ` +` rather than the plan's single space: the captured
        // line is `SOUNDING W6HOS            CHANNEL: 30`
        // (p14c-sounding-clean-20260822-132151.jsonl), and a single space would
        // make this row dead against every capture of it. A schedule row
        // (`… INTERVAL …`) still does NOT match.
        new(@"^(?:SOUNDING|EXCHANGE) \S+ +CHANNEL: *\d+$"),
    ];

    private static bool SurvivesWrap(string upper) => WrapSurvivors.Any(r => r.IsMatch(upper));

    /// <summary>
    /// ROUND 16 FIXES S2 — the async lines that may arrive where a
    /// <c>TXMSG nn</c> header's TEXT is expected. FULL-LINE, over the trimmed
    /// upper line, like <see cref="WrapSurvivors"/>.
    ///
    /// <para>DELIBERATELY SHORTER than the wrap set. Message text is free
    /// operator text, so these six lines are read as async events even though
    /// an operator could have stored one verbatim — an IRREDUCIBLE ambiguity,
    /// bounded by the whole-line match (<c>SCANNING NOW</c> and
    /// <c>KEY OFF AT NOON</c> are still text). Every extra predicate would
    /// widen it, and the HOP-only lines cannot arrive here at all: the whole
    /// TXMSG family is <c>ALE&gt;</c>-only (protocol.md's TXMSG row).</para>
    /// </summary>
    private static readonly Regex[] TxMsgAsyncLines =
    [
        new(@"^SCANNING$"), new(@"^SCAN STOPPED$"), new(@"^KEY OFF$"), new(@"^IN_PROG$"),
        new(@"^BATTERY STATUS FULL \d+(?:\.\d+)?V$"), new(@"^POWER CUTBACK$"),
    ];

    private static bool IsTxMsgAsyncLine(string upper) => TxMsgAsyncLines.Any(r => r.IsMatch(upper));

    private sealed class Ctx
    {
        public required string Raw;
        public required string Upper;
        public string? Payload;
        public string? RawPayload;
        public required ParseResult Result;
    }

    public ResponseParser(RadioState state)
    {
        _state = state;
        _handlers = BuildTable();
    }

    public ParseResult Parse(string? line)
    {
        var result = new ParseResult();
        if (line is null) return result;

        // CONTROL BYTES ARE STRIPPED BEFORE PARSING (clone round 12, audit
        // round 1 finding 2). The radio really does emit them: the captured
        // zeroize settle window (bench/transcripts/r12-p1-20260818-222442)
        // contains a NUL-ONLY poll answer and terminates its ZEROIZE-COMPLETE
        // banner with three BELs — `*** ZEROIZE COMPLETE ***\a\a\a`. Left in,
        // the NULs framed into a line that matched no token and raised
        // "Unrecognized message" at the operator, and the BELs rode into the
        // banner's payload and out to the operator's own error text.
        //
        // Stripped HERE, not in the framer, so the EVIDENCE stays verbatim:
        // Prc138Radio raises MessageReceived with the untouched line before
        // this method ever runs, so the Console keeps the bytes exactly as they
        // arrived. Parsing is the only thing that gets the sanitized view.
        var raw = StripControlCharacters(line).Trim();
        if (raw.Length == 0)
        {
            // A blank line ends any WRAP continuation (Sol audit 2026-08-17,
            // finding 1): the captured wrap lines arrive consecutively, so a
            // blank between them is not part of the listing — without this a
            // continuation could survive into unrelated numeric lines.
            // (TxMsgText/RankReport blank behavior predates the wrap work and
            // is unchanged here.)
            EndWrapContinuation();
            result.Handled = true;
            return result;
        }

        var upper = raw.ToUpperInvariant();

        // HELP output is a menu, not radio state: its lines begin with real
        // tokens carrying junk payloads ("SCAn - start scanning" would set
        // LinkState). Skip the whole block, banner to the next mode prompt.
        if (_inHelpBlock)
        {
            if (upper.EndsWith('>')) _inHelpBlock = false;   // prompt ends the block
            result.Handled = true;
            return result;
        }

        if (upper.StartsWith("---") ||
            upper.EndsWith("COMMANDS:") ||
            upper.EndsWith("COMMANDS CONSIST OF:") ||
            upper.StartsWith("EMBEDDED ADAPTIVE") ||
            upper.StartsWith("** HELP") || upper.StartsWith("*** CAPITAL"))
        {
            _inHelpBlock = true;
            // Entering a help block ends any WRAP continuation (Sol audit
            // finding 1): without this, ChgChans/HoplistFreqs survived the
            // whole block and could consume a numeric line far later.
            EndWrapContinuation();
            result.Handled = true;
            return result;
        }

        // Banner lines: "** ERROR **" — and every OTHER `**`-fenced line.
        //
        // ROUND-12 §9 B2 — THE DISCRIMINATION. This branch used to swallow the
        // whole family: any line starting with `**` was fed to the ALE refusal
        // mirror as the literal "** ERROR **" and its own payload was DROPPED,
        // so a banner like the receive-only keying refusal arrived at the
        // operator as a generic syntax error with its content gone.
        //   * ONLY the exact `** ERROR **` is the generic syntax reject, and
        //     ONLY it feeds NoteProgrammingRefusal — an ALE programming
        //     bracket may attribute a syntax reject to its write; it may not
        //     attribute an unrelated banner.
        //   * ANY OTHER `**` line is recognized, keeps its payload VERBATIM
        //     (RawPayload, original case), and Prc138Radio raises it carrying
        //     that payload rather than rebadging it.
        // THE RX-ONLY REFUSAL WAS CAPTURED 2026-08-19 (owner-present re-run of
        // round-12 P-2 step f; three transcripts under bench/transcripts/), so
        // the note this comment used to carry — "its exact bytes are still
        // UNCAPTURED" — is false and is retired here (round 13 D1). The bytes
        // are `***RX Only***`, and this branch already yields them exactly:
        // Token `**`, RawPayload `RX Only`, original case, NO programming
        // refusal (only the literal `** ERROR **` feeds that mirror).
        //   * The recognizer built on those bytes is the CONSUMER'S, not this
        //     one's, and deliberately: the parser stays a RECOGNIZER. Operator
        //     wording, the edge/bounce distinction and the clock all live in
        //     Prc138Radio's `**` arm, where a policy belongs.
        //   * The captured framing hazard is handled UPSTREAM, not here: many
        //     instances arrive glued to a prompt (`SSB> ***RX Only***`), and
        //     LineFramer splits that on the prompt's own '>' — so this branch
        //     sees a clean banner line either way and needs no special case.
        //     That shape belongs to ASYNC LINES GENERALLY (`SSB> KEY OFF` was
        //     caught the same way), which is why the tolerance sits in the
        //     framer rather than in a refusal-specific rule.
        if (upper.StartsWith("**"))
        {
            result.Token = "**";
            result.Payload = upper.Trim('*', ' ');
            result.RawPayload = raw.Trim('*', ' ');
            result.Handled = true;
            _continuation = Continuation.None;
            if (IsGenericErrorBanner(upper)) _state.Ale.NoteProgrammingRefusal("** ERROR **");
            return result;
        }

        int space = upper.IndexOf(' ');
        var token = space > 0 ? upper[..space] : upper;
        var payload = space > 0 ? upper[(space + 1)..].Trim() : null;
        var rawPayload = space > 0 ? raw[(space + 1)..].Trim() : null;

        result.Token = token;
        result.Payload = payload;
        result.RawPayload = rawPayload;

        var ctx = new Ctx { Raw = raw, Upper = upper, Payload = payload, RawPayload = rawPayload, Result = result };

        // TXMSG header/text pairs are adjacent, and message text is free
        // operator text that may BEGIN with a protocol keyword ("KEY OFF AT
        // NOON"). The line after a TXMSG header is always consumed as message
        // text — unless it is a mode prompt (block ended without text).
        if (_continuation == Continuation.TxMsgText)
        {
            bool isPrompt = token is "SSB>" or "ALE>" or "HOP>";
            // ROUND 16 FIXES S2: an ENUMERATED async line is routed as ITSELF
            // and the header stays ARMED, so the real text still reaches the
            // slot. Without this the async line BECAME the message and the
            // text behind it surfaced unrecognized. The PROMPT still ends the
            // block, as it always has.
            if (isPrompt || !IsTxMsgAsyncLine(upper))
            {
                _continuation = Continuation.None;
                if (!isPrompt)
                {
                    ApplyTxMsgText(raw);
                    result.Handled = true;
                    result.Changed = true;
                    return result;
                }
            }
            // prompt, or an async line: fall through to normal handling
        }

        // The received-AMD text line — the SAME rule as TxMsgText above
        // (captured 2026-08-24: `RXMSG 00   FROM KC1HAS1 …` then `  TESTING  `
        // 1 ms behind). The six-shape async predicate is shared deliberately:
        // this header arrives at a linked ALE prompt where the scan chatter
        // is the same, and a wider set would widen the same irreducible
        // text-vs-async ambiguity the TXMSG rule bounds.
        if (_continuation == Continuation.RxMsgText)
        {
            bool isPrompt = token is "SSB>" or "ALE>" or "HOP>";
            if (isPrompt || !IsTxMsgAsyncLine(upper))
            {
                _continuation = Continuation.None;
                if (!isPrompt)
                {
                    ApplyRxMsgText(raw);
                    result.Handled = true;
                    result.Changed = true;
                    return result;
                }
            }
            // prompt, or an async line: fall through to normal handling
        }

        // A wrapped CHGROUP CHANS listing continues with lines of BARE channel
        // numbers (CAPTURED 2026-08-17, phase 2: a 40-channel group prints
        // "CHGROUP 03 CHANS 00 … 19" then "20 21 … 39" — 20 per line). Armed
        // only by a just-parsed CHGROUP line; ANY line that is not purely
        // in-domain channel numbers ends it and is processed normally, so the
        // trailing gate/prompt lines cannot be misread as channels.
        if (_continuation == Continuation.ChgChans)
        {
            var contTokens = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Exactly the CAPTURED token shape: two ASCII digits, zero-padded
            // (Sol audit finding 5 — NumberStyles.Integer also accepted signed
            // and one-digit forms no capture has ever shown). Two digits is
            // also the 0-99 domain check, so the range test is now redundant.
            bool allChannels = contTokens.Length > 0 && contTokens.All(t =>
                t.Length == 2 && t.All(char.IsAsciiDigit));
            if (allChannels)
            {
                foreach (var t in contTokens)
                    _chgChannels.Add(int.Parse(t, CultureInfo.InvariantCulture));
                _state.Ale.ApplyChannelGroup(_chgGroup, _chgChannels);
                result.Handled = true;
                result.Changed = true;
                return result;                 // continuation stays armed — more wrap lines may follow
            }
            // ROUND 16 FIXES S1: an ENUMERATED async line SUSPENDS the
            // listing — it falls through to its own handler and the
            // continuation STAYS ARMED, so the wrap line behind it still
            // lands. Without this, an async line here published the group at
            // 20 channels AND sent the following `20 21 … 39` down the
            // unrecognized path. A suspension is NOT a commit: nothing is
            // re-applied here.
            if (!SurvivesWrap(upper))
                _continuation = Continuation.None; // not a wrap line: fall through to normal handling
        }

        // A wrapped HOPLIST answer continues with lines of bare 5-DIGIT
        // frequencies (captured 2026-08-17, phase 3: 8 per line, in DIS
        // records too). Distinct from ChgChans by digit count, so neither
        // continuation can consume the other's lines.
        if (_continuation == Continuation.HoplistFreqs)
        {
            var freqTokens = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool allFreqs = freqTokens.Length > 0 && freqTokens.All(t =>
                t.Length == 5 && t.All(char.IsAsciiDigit));
            if (allFreqs)
            {
                _hoplistFreqs.AddRange(freqTokens);
                _state.Hop.SetHopList(_hoplistNet, [.. _hoplistFreqs]);
                result.Handled = true;
                result.Changed = true;
                return result;                 // stays armed - more wrap lines may follow
            }
            if (!SurvivesWrap(upper))          // S1's suspension, identically
                _continuation = Continuation.None;
        }

        if (_handlers.TryGetValue(token, out var handler))
        {
            if (_continuation == Continuation.RankReport && !RankReportSurvivors.Contains(token))
                _continuation = Continuation.None;

            try
            {
                // Handled defaults true for a recognized token; a handler
                // may opt OUT (set false) when the payload proves the line
                // is not actually the shape the token implies — the line
                // then flows through the unrecognized path (honest surface;
                // audit round 1, NIT-1).
                result.Handled = true;
                handler(ctx);
            }
            catch (FormatException ex)
            {
                result.PayloadError = ex;
                result.Handled = true;
            }
            catch (OverflowException ex)
            {
                result.PayloadError = new FormatException(ex.Message, ex);
                result.Handled = true;
            }
            return result;
        }

        _continuation = Continuation.None;
        return result;   // Handled = false → unrecognized
    }

    // ------------------------------------------------------------------

    private Dictionary<string, Action<Ctx>> BuildTable()
    {
        return new Dictionary<string, Action<Ctx>>(StringComparer.Ordinal)
        {
            // Prompts — also serve as mode indication.
            ["SSB>"] = c => Mode(c, OperatingMode.Ssb),
            ["ALE>"] = c => Mode(c, OperatingMode.Ale),
            ["HOP>"] = c =>
            {
                // A HOP prompt also ends any hopset generation: the EXCLUDE
                // path prints "Generating Hopset..." with none of the usual
                // clearing lines (bench session-16).
                _state.Hop.SetGeneratingHopset(false);
                Mode(c, OperatingMode.Hop);
            },

            // ---- General / SSB --------------------------------------------
            ["POWER"] = c =>
            {
                // PA thermal management chatter, may arrive at any time.
                if (c.Payload == "CUTBACK") { Track(c, _state.SetPowerCutback(true)); return; }
                if (c.Payload == "RESTORED") { Track(c, _state.SetPowerCutback(false)); return; }
                var level = Wire.ParsePowerLevel(Require(c))
                    ?? throw Bad(c, "POWER");
                Track(c, _state.SetPowerLevel(level));
            },
            ["TXFR"] = c => Track(c, _state.SetTxFrequency(Require(c))),
            ["RXFR"] = c => Track(c, _state.SetRxFrequency(Require(c))),
            ["CHAN"] = c => Track(c, _state.SetOperatingChannel(ParseInt(c, Require(c)))),
            ["KEY"] = c => Track(c, _state.SetKeyline(Wire.ParseKeyline(Require(c)) ?? throw Bad(c, "KEY"))),
            ["TUNING"] = c => { _state.SetTuning(true); c.Result.Changed = true; },
            ["TUNE"] = c =>
            {
                // This radio says "TUNE FAULT"; the RF-5022 document says
                // "FAIL" (a different radio). Both accepted — a real tune
                // failure once threw a payload error instead of lighting the
                // indicator (shipped-bug lesson, session-24).
                switch (c.Payload)
                {
                    case "COMPLETE": _state.SetTuneComplete(); break;
                    case "MARGINAL": _state.SetTuneMarginal(); break;
                    case "FAULT" or "FAIL": _state.SetTuneFail(); break;
                    default: throw Bad(c, "TUNE");
                }
                _state.Hop.SetGeneratingHopset(false);
                c.Result.Changed = true;
            },
            ["MODE"] = c => Track(c, _state.SetModulationMode(Wire.ParseModulation(Require(c)) ?? throw Bad(c, "MODE"))),
            ["BAND"] = c => Track(c, _state.SetBandwidth(Wire.NormalizeBandwidth(Require(c)) ?? throw Bad(c, "BAND"))),
            ["AGC"] = c => Track(c, _state.SetAgcSpeed(Wire.ParseAgcSpeed(Require(c)) ?? throw Bad(c, "AGC"))),
            ["RXONLY"] = c => Track(c, _state.SetChannelRxOnly(Wire.ParseYesNo(Require(c)) ?? throw Bad(c, "RXONLY"))),
            ["SQUELCH"] = c => Track(c, _state.SetAnalogSquelch(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "SQUELCH"))),
            ["STEP"] = c => Track(c, _state.SetFrequencyStep(Wire.ParseFrequencyStep(Require(c)) ?? throw Bad(c, "STEP"))),
            ["BATTERY"] = c => Track(c, _state.SetBatteryStatus(c.RawPayload ?? "")),

            // Phase R (plan-gui-rejigger.md round 4): settings with CAPTURED
            // answer shapes are mirrored; answers never captured stay
            // recognized-as-noise (no invented parses — replay doctrine).
            // RFG rides along with AGC answers (probe R4: "AG MED" →
            // "AGC MED" + "RFG 100").
            ["RFG"] = c => Track(c, _state.SetRfGain(ParseInt(c, Require(c)))),
            // DV answers carry a DGT_SQUELCH rider line (probe R4) — each
            // line parses standalone into its own INDEPENDENT mirror
            // (protocol.md: DGT_S is not a DV sub-setting).
            ["DV"] = c => Track(c, _state.SetDigitalVoice(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "DV"))),
            ["DGT_SQUELCH"] = c => Track(c, _state.SetDigitalSquelch(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "DGT_SQUELCH"))),
            ["SQ_LEVEL"] = c => Track(c, _state.SetSquelchLevel(Require(c))),
            ["FMSQUELCH"] = c => Track(c, _state.SetFmSquelch(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "FMSQUELCH"))),
            ["FMSQ_TYPE"] = c => Track(c, _state.SetFmSquelchType(Require(c))),
            ["FMTONE"] = c => Track(c, _state.SetFmTone(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "FMTONE"))),
            ["FMDEV"] = c => Track(c, _state.SetFmDeviation(Require(c))),
            ["BFO"] = c => Track(c, _state.SetBfoOffset(Require(c))),
            ["CWOFFSET"] = c => Track(c, _state.SetCwOffset(Require(c))),
            ["COMPRESS"] = c => Track(c, _state.SetCompression(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "COMPRESS"))),
            ["ANTENNA"] = c => Track(c, _state.SetAntenna(Require(c))),
            ["RWAS"] = c => Track(c, _state.SetRwas(Wire.ParseEnabledDisabled(Require(c)) ?? throw Bad(c, "RWAS"))),
            ["UNKEY_M"] = c => Track(c, _state.SetUnkeyMask(Wire.ParseEnabledDisabled(Require(c)) ?? throw Bad(c, "UNKEY_M"))),
            // Verbatim, not an enum: "RETRANS DISABLED" is the only captured
            // spelling (bench item: capture the ENABLED answer).
            ["RETRANS"] = c => Track(c, _state.SetRetransmit(Require(c))),
            // Verbatim: "OFF"/"ON"/"NOT INSTALLED" — SH prints "AVS OFF"
            // even cardless; only the direct query reports availability.
            ["AVS"] = c => Track(c, _state.SetAvs(Require(c))),
            ["ENCRYPT"] = c => Track(c, _state.SetEncryption(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "ENCRYPT"))),
            ["ENCRYPTION"] = c => Track(c, _state.SetEncryptionAvailability(Require(c))),
            ["CUR_KEY"] = c => Track(c, _state.SetCurrentEncryptionKey(Require(c))),
            // LIGHT / INTENSITY — PROVISIONAL, OLD-APP-DERIVED (UI-tweaks
            // round 4, AC / R4-Q1). This project's bench has never captured
            // either PAYLOAD (round 3 recorded them as noise, and its comment
            // that there was "no old-app evidence either" was wrong): the
            // WinForms app parses both (old repo
            // src/Falcon.Core/Protocol/ResponseParser.cs:269 and :271) with the
            // spellings in its Wire.cs:182-186 (OFF|MOMENTARY) and
            // Wire.cs:187-197 (Intensities "00".."08"), and its settings window
            // queries both when it opens (src/Falcon.Gui/Configuration.cs:41-42
            // -> Prc138Radio.cs:997-998). Mirrored VERBATIM, not through an
            // enum and not through ParseInt: the spellings — INTENSITY's
            // zero-padding in particular — are precisely what the bench must
            // confirm. Bench items: docs/bench-checklist.md "Radio settings:
            // device queries".
            ["LIGHT"] = c => Track(c, _state.SetBacklightFunction(Require(c))),
            ["INTENSITY"] = c => Track(c, _state.SetBacklightIntensity(Require(c))),
            ["CONTRAST"] = c => Track(c, _state.SetContrast(ParseInt(c, Require(c)))),
            // PREAMP / INTCOUPLER / KWATT — PROVISIONAL, OLD-APP-DERIVED
            // (plan-ui-tweaks-round3.md V7). This project's bench has never
            // captured these answers; the WinForms app's parser table maps
            // them (old repo src/Falcon.Core/Protocol/ResponseParser.cs:272,
            // 273, 274 with Wire.cs:38-42 / :28-32 for the spellings), and
            // its settings window queries all three on open
            // (src/Falcon.Gui/Configuration.cs:44-46). Mirrored VERBATIM, not
            // through an enum: the old app's ENABLED/BYPASSED and YES/NO
            // spellings are exactly what the bench must confirm, so parsing
            // them into an enum here would turn an assumption into a fact.
            // Bench items: docs/bench-checklist.md "SSB settings queries".
            ["PREAMP"] = c => Track(c, _state.SetRxPreamp(Require(c))),
            ["INTCOUPLER"] = c => Track(c, _state.SetInternalCoupler(Require(c))),
            ["KWATT"] = c => Track(c, _state.SetOneKilowattPa(Require(c))),
            ["BEEP"] = c => Track(c, _state.SetBeep(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, "BEEP"))),
            ["LEVEL"] = Noise,          // "LEVEL rs-232" — port level, out of scope
            ["MODULE"] = Noise,         // "Module 01A  Revision 8214B" (TE 3)
            ["PORT_DATA"] = Noise,
            // "PREPOST FILTER ENABLE" / "RXANTENNA DISABLE" / "SCAN SLOW"
            // (session-20 capture) — mirrored verbatim per sub-parameter.
            ["PREPOST"] = HandlePrePost,
            // Radio clock: TIME is mirrored verbatim for the HOP pane's TOD
            // display (Stage 5); TI and each of TIME/DAT/DAY answer the full
            // DAY/DATE/TIME triplet. DATE/DAY stay noise — v1 shows TOD only.
            ["TIME"] = c => Track(c, _state.SetRadioTimeOfDay(Require(c))),
            ["DATE"] = Noise,
            ["DAY"] = Noise,
            ["WAIT..."] = Noise,        // "Wait..." busy notice

            // "CH 00 RxFr 04123000 TxFr 04123000 MODE USB AGC SL BA 2.7  RXONLY NO" (DI)
            ["CH"] = c =>
            {
                // Round 11 §8: keyed UPSERT on the channel number, so a
                // targeted `DI n n` no longer wipes the other channels.
                _state.UpsertChannelLine(c.RawPayload ?? "");
                c.Result.Changed = true;
            },

            ["PORT_REMOTE"] = HandlePortRemote,
            ["PORT_R"] = Noise,          // echo of our own command while echo is still on
            ["MODEM"] = HandleModem,

            // ---- ALE --------------------------------------------------
            ["ALE_INST"] = Noise,        // controller ident banner ("rf5122")

            // NOT a fill flag: probe R7 (2026-08-02) — IN_PROG keeps appearing
            // with a verified-complete, scanning fill. Informational noise;
            // only the specific gate lines below indicate fill state.
            ["IN_PROG"] = Noise,

            ["PRG"] = c => { _state.Ale.SetFillState(AleFillState.NeedSelfAddress); c.Result.Changed = true; },
            ["IND"] = c =>
            {
                if (c.Payload is not null && c.Payload.StartsWith("NOT PROGRMD"))
                {
                    _state.Ale.SetFillState(AleFillState.NeedIndividual);
                    c.Result.Changed = true;
                }
            },
            ["NO"] = c =>
            {
                if (c.Payload is null) return;
                if (c.Payload.StartsWith("CHANS TO SCAN"))
                {
                    _state.Ale.SetFillState(AleFillState.NeedChannels);
                    c.Result.Changed = true;
                }
                else if (c.Payload.StartsWith("HOPSET"))    // async "No Hopset"
                {
                    _state.Hop.SetHopNum(0);
                    _state.Hop.SetGeneratingHopset(false);
                    _state.Hop.NotifyNoHopset();   // always signalled: HopNum may already be 0 (audit F4)
                    c.Result.Changed = true;
                }
                // async "NO NET ID" — a net WITH a hopset but WITHOUT a net ID
                // refuses to generate (captured 2026-08-16). Same shape as the
                // No-Hopset branch above: generation is over and nothing was
                // produced. Before this the line fell through every branch and
                // was silently discarded.
                else if (c.Payload.StartsWith("NET ID"))
                {
                    _state.Hop.SetHopNum(0);
                    _state.Hop.SetGeneratingHopset(false);
                    _state.Hop.NotifyNoNetId();
                    c.Result.Changed = true;
                }
                // The two POSITIVE empty-state markers (captured 2026-08-17).
                // Both are the radio SAYING "none", which is a different fact
                // from "nothing arrived" — they mark the active read's
                // accumulator empty; the commit publishes read-empty either
                // way, so these are the honesty half, not the mechanism.
                else if (c.Payload.StartsWith("MEMBERS PRGMD"))
                {
                    _state.Ale.NoteNoMembersProgrammed();
                    c.Result.Changed = true;
                }
                else if (c.Payload.StartsWith("LQA SCHEDULED"))
                {
                    _state.Ale.NoteNoLqaScheduled();
                    c.Result.Changed = true;
                }
                // " NO RESPONSE     " — the ANY-call answer window expiring
                // (CAPTURED 2026-08-23, probe P20b: `CAL ANY 12` at 68 752 ms
                // and `SE 9 ANY 12` at 68 827 ms into their listen windows,
                // bench/transcripts/p20b-any-with-channel-20260823-233951.jsonl
                // notes cal-any-chan-summary / se-any-chan-summary). RECOGNIZED
                // and NOT mirrored, for the TERMINATING LINK reason: the
                // radio's own `SCANNING` rides in the same chunk and owns the
                // state move. Claimed EXACTLY (the INVALID-branch idiom), so
                // no later prefix branch may inherit this reading.
                else if (c.Payload == "RESPONSE") return;
                // "NO VALID KEY", "NO KEY, ENCR OFF", "NO PRESETS ENABLED",
                // "NO PA INSTALLED", " NO CHANS IN GRP " (the bare-ANY refusal,
                // P20): recognized, not mirrored (COMSEC/modem programming are
                // out of the v1 surface; the refusal surfaces in the Console).
            },
            ["SCANNING"] = c =>
            {
                _state.Ale.SetLinkState(AleLinkState.Scanning);
                // The radio only auto-scans with a complete fill
                // (docs/protocol.md, ZERO corollary) — SCANNING is the
                // positive fill indicator now that IN_PROG is known noise.
                _state.Ale.SetFillState(AleFillState.Complete);
                c.Result.Changed = true;
            },
            // "SCAN STOPPED" is the only captured SCAN line — any other
            // payload is NOT a link-state fact and surfaces as unrecognized
            // rather than silently flipping the banner (audit NIT-1).
            ["SCAN"] = c =>
            {
                if (c.Payload == "STOPPED")
                {
                    _state.Ale.SetLinkState(AleLinkState.Stopped);
                    c.Result.Changed = true;
                }
                else
                {
                    c.Result.Handled = false;
                }
            },
            // `TERMINATING LINK` — CAPTURED 2026-08-23 (probe P20b leg A,
            // bench/transcripts/p20b-any-with-channel-20260823-233951.jsonl
            // record 4: `SCA` against a held ALL link answered
            // "\n\r\n\rALE> TERMINATING LINK\r\n", prompt-glued, and the
            // radio's own `SCANNING` followed ~2 s later). RECOGNIZED, and
            // NOTHING is mirrored: the SCANNING that follows is what clears
            // the link state, so a state write here would only race the
            // radio's own report. Guarded on the EXACT captured payload
            // (the LQA/INDIV/SELF idiom, Sol audit finding 4): any other
            // TERMINATING payload has no capture and keeps today's behavior
            // — the unrecognized-line path.
            ["TERMINATING"] = c => { if (c.Payload != "LINK") c.Result.Handled = false; },

            // The INBOUND handshake — CAPTURED 2026-08-24 (field transcript
            // field-ale-first-contact-20260824-2144.txt): ` SIGNAL RECEIVED `
            // (leading and trailing spaces) announces detected ALE energy,
            // `RECEIVING CALL  ` follows when the call decodes, and the pair
            // resolves to LINKED (21:56) or back to a bare SCANNING when the
            // call was for someone else (22:01). Both claim ONLY their
            // captured payload — the TERMINATING discipline.
            ["SIGNAL"] = c =>
            {
                if (c.Payload != "RECEIVED") { c.Result.Handled = false; return; }
                _state.Ale.SetLinkState(AleLinkState.SignalReceived);
                c.Result.Changed = true;
            },
            ["RECEIVING"] = c =>
            {
                // `RECEIVING AMD   ` (field capture 22:06:58) arrives between
                // RECEIVING CALL and the RXMSG record — recognized, no state
                // change: the handshake state stands and the AMD itself lands
                // via the RXMSG mirror. Any other payload stays unclaimed.
                if (c.Payload == "AMD") return;
                if (c.Payload != "CALL") { c.Result.Handled = false; return; }
                _state.Ale.SetLinkState(AleLinkState.ReceivingCall);
                c.Result.Changed = true;
            },
            // SIGNAL / RECEIVING: the old parser guessed these
            // LINK-STATE shapes; neither has a captured LIFECYCLE. Two-station
            // behavior is GATED, not guessed (plan §5.6) — such lines flow
            // through the unrecognized-line path until the two-station session
            // pins their real shapes. Audit round 1, F5. ("SIGNAL RECEIVED"
            // WAS captured once — P14 run 1, 2026-08-22, scan stopped, no
            // partner — and stays deferred as a one-off with no lifecycle.)
            // EXCHANGE/SOUND left that list on 2026-08-17: they ARE captured,
            // as the bare-EXCH SCHEDULE LISTING's row tokens (below) — a
            // schedule fact, never a link state.
            // SOUNDING left it on 2026-08-22 (round 15 item I): probe P14b/P14c
            // captured the bare-STA LQA lifecycle single-station, and EXCHANGE
            // turns out to carry BOTH shapes on one token.
            ["CALLING"] = c => HandleCallProgress(c, AleLinkState.Calling),
            ["SENDING"] = c => HandleCallProgress(c, AleLinkState.Sending),
            // "SOUNDING W6HOS            CHANNEL: 30" — one line per channel of
            // the self's group while a bare `SOU STA` runs (P14c).
            ["SOUNDING"] = c => HandleLqaProgress(c, AleLinkState.Sounding),
            // The SH block's first line while an LQA runs — one token, no
            // payload, in the seat SCANNING holds otherwise (P14b/P14c). It
            // does not say WHICH kind is running, so the mirror does not
            // pretend to know: Lqa is the kind-unknown state and the
            // station/channel slot is left exactly as it was.
            //
            // IT NEVER REPLACES A KIND THE APP ALREADY KNOWS (manager ruling,
            // 2026-08-23, on the phase-5 wire leg: an operator's mid-run `SH`
            // replaced the banner's "SOUNDING W6HOS — CH 28" with the
            // kind-unknown "LQA IN PROGRESS" for 11 s, until the next channel
            // line restored it). A LESS specific report is not news: from
            // Sounding or Exchanging this line CONFIRMS the run and is handled
            // with no state change, and the same holds for a call's states,
            // which no capture shows it printed in. From Scanning, Stopped or
            // an unreported state it is the only thing the app knows, and it
            // sets Lqa.
            //
            // The EIGHTH prior state is Lqa ITSELF (audit round 1, MINOR 1):
            // an `SH` repeated during one run - the pane's status read, a
            // Console read, a campaign's lap - lands this line on a mirror
            // that already says Lqa. Nothing moves, so `Changed` must stay
            // FALSE: this parser's contract is that Changed means a mirror
            // VALUE changed, and SetLinkState's own equality guard would have
            // dropped the write silently while the result claimed otherwise.
            ["LQA/SOUND"] = c =>
            {
                if (c.Payload is not null) { c.Result.Handled = false; return; }
                var s = _state.Ale.LinkState;
                if (s.IsConfirmed && s.Value.IsOnAir())
                    return;                    // recognized; confirms, does not downgrade
                _state.Ale.SetLinkState(AleLinkState.Lqa);
                c.Result.Changed = true;
            },
            // "LINKED ALL               CHANNEL: 29" — the LINKED payload CAN
            // carry the link's own channel (CAPTURED 2026-08-23, probe P20:
            // the `CAL ALL` completion, cal-all-summary at 18 180 ms; and as
            // the SH block's FIRST line, P20b record 3, where the sticky ALL
            // link survived two `ST`s and a serial-session close). When it
            // does, that channel is the fact; only a payload WITHOUT one falls
            // back to the last CALLING/SENDING channel, which is the whole of
            // the pre-P20 behavior. The station reads the same either way —
            // ProgressShape's first group and FirstWord agree on this shape.
            ["LINKED"] = c =>
            {
                _state.Ale.SetLinkState(AleLinkState.Linked);
                if (!string.IsNullOrEmpty(c.Payload))
                {
                    var m = ProgressShape.Match(c.Payload);
                    // AUDIT ROUND 1 (MAJOR): ProgressShape's channel group is
                    // `(\S+)` — neither end-anchored nor digit-restricted — so
                    // reusing it raw would have mirrored ARBITRARY tokens:
                    // `LINKED ALL               CHANNEL: XX` left "XX" in the
                    // slot, which a consumer renders as "CH XX". The payload's
                    // channel is adopted ONLY in the CAPTURED spelling (exactly
                    // two digits — every P20/P20b row prints "29"/"12"/"01"),
                    // which is this file's rule of claiming only what a capture
                    // pins. Anything else falls back to the without-a-channel
                    // behavior: keep the last CALLING/SENDING channel, the whole
                    // of the pre-P20 reading. Scoped to THIS branch on purpose —
                    // ProgressShape is untouched, so CALLING/SENDING/SOUNDING
                    // keep their pre-existing tolerance (out of this round).
                    _state.Ale.SetLinkedStation(
                        m.Success ? m.Groups[1].Value : FirstWord(c.Payload),
                        m.Success && IsCapturedChannel(m.Groups[2].Value)
                            ? m.Groups[2].Value
                            : _state.Ale.LinkedChannel);
                }
                c.Result.Changed = true;
            },

            // ALE settings lines (SH block + query answers) — mirrored
            // (Phase R): all nine are reported in the ALE SH block and are
            // confirmed query+set on the bench (protocol.md).
            ["RAD_SIL"] = c => AleOnOff(c, _state.Ale.SetRadioSilence),
            ["RAD"] = Noise,
            ["ALL_CALL"] = c => AleOnOff(c, _state.Ale.SetAllCall),
            ["ANY_CALL"] = c => AleOnOff(c, _state.Ale.SetAnyCall),
            ["LSTN"] = c => AleOnOff(c, _state.Ale.SetListenBeforeTx),
            ["KEY_TO_CALL"] = c => AleOnOff(c, _state.Ale.SetKeyToCall),
            ["AMD_DISPLAY"] = c => AleOnOff(c, _state.Ale.SetAmdDisplay),
            ["TIME_OUT"] = c => { _state.Ale.SetLinkTimeoutMinutes(ParseInt(c, Require(c))); c.Result.Changed = true; },
            ["MAXCH"] = c => { _state.Ale.SetMaxScanChannels(ParseInt(c, Require(c))); c.Result.Changed = true; },
            ["TUNETIME"] = c => { _state.Ale.SetTuneTimeSeconds(ParseInt(c, Require(c))); c.Result.Changed = true; },
            ["CHGROUP"] = HandleChannelGroup,

            ["SLFAD"] = c => HandleAddress(c, AleAddressKind.Self),
            ["INDAD"] = c => HandleAddress(c, AleAddressKind.Individual),
            ["NETAD"] = c => HandleAddress(c, AleAddressKind.Net),
            // "     MEMBER 01  I2" — the TARGETED NETAD read's indented
            // continuation lines (captured 2026-08-17). The line names no net,
            // so the only honest attribution is the active member read's own
            // net; AleState ignores it outside one.
            ["MEMBER"] = HandleNetMember,
            // The bare-EXCH schedule listing's two row forms (captured
            // 2026-08-17): "EXCHANGE I1              INTERVAL 01:00 START
            // TIME 22:34" and the SOUND twin.
            // ONE token, TWO captured shapes (round 15 item I). The SCHEDULE
            // row is tried FIRST and is byte-identical in behaviour: it is the
            // anchored, sized shape, so a listing row can never fall through
            // into the progress branch and a progress line can never write the
            // schedule mirror (the invariant the branch ORDER exists to hold).
            // Only then the bare-STA progress shape
            // "EXCHANGE KC1HAS           CHANNEL: 30" (P14b); anything else
            // stays unrecognized.
            ["EXCHANGE"] = c =>
            {
                if (TryLqaSchedule(c, LqaScheduleKind.Exchange)) return;
                HandleLqaProgress(c, AleLinkState.Exchanging);
            },
            // SOUND is TWO shapes now (field capture 2026-08-24 #2,
            // field-ale-sounding-lqa-20260824-2312.txt): the schedule listing
            // row, and `SOUND FROM:   KC1HAS1         CHANNEL: 27` — another
            // station's SOUNDING HEARD on that channel (the passive-discovery
            // lifecycle: SIGNAL RECEIVED → SOUND FROM → SCANNING, captured
            // five times). Mirrored into the heard-event carrier (the LQA
            // Heard-stations table); the LINK STATE stays
            // where the surrounding lines put it.
            ["SOUND"] = c =>
            {
                var heard = HeardFromShape.Match(c.Payload ?? "");
                if (heard.Success)
                {
                    _state.Ale.SetLastHeard(new AleHeard(AleHeardKind.Sounding,
                        heard.Groups[1].Value, heard.Groups[2].Value));
                    c.Result.Changed = true;
                    return;
                }
                HandleLqaSchedule(c, LqaScheduleKind.Sound);
            },
            // `RESP  FROM:   KC1HAS1         CHANNEL: 29` — the partner's
            // ANSWER during a live LQA exchange (same capture: one per
            // EXCHANGE channel leg). Mirrored into the heard-event carrier —
            // the next
            // EXCHANGE line moves the channel; the collected scores are read
            // back with RANK. Claims ONLY the captured payload.
            ["RESP"] = c =>
            {
                var heard = HeardFromShape.Match(c.Payload ?? "");
                if (!heard.Success) { c.Result.Handled = false; return; }
                _state.Ale.SetLastHeard(new AleHeard(AleHeardKind.Response,
                    heard.Groups[1].Value, heard.Groups[2].Value));
                c.Result.Changed = true;
            },
            ["TXMSG"] = HandleTxMsgHeader,
            // The Stage 9 gate CLOSED 2026-08-24: the two-station session
            // captured the async arrival shape (field transcript, 22:06:59):
            // `RXMSG 00   FROM KC1HAS1          DATE: 24-AUG-26  TIME: 22:06`
            // with the message text on the NEXT line — mirrored below. Any
            // OTHER RXMSG payload keeps the pre-capture behavior (recognized,
            // surfaced raw, not mirrored): the bare-RXM listing shape is
            // still uncaptured and this handler claims only what it has seen.
            ["RXMSG"] = HandleRxMsgHeader,
            ["RANK"] = c =>
            {
                _state.Ale.ClearLqaReport();
                _rankStation = FirstWord(c.Payload ?? "");
                _continuation = Continuation.RankReport;
                c.Result.Changed = true;
            },
            ["CHAN:"] = HandleRankLine,
            // Radio rejection lines — answers to something the operator just
            // asked; the radio class surfaces them as errors, and the ALE
            // programming families are ALSO recorded in the mirror's refusal
            // slot (plan-ale-programming.md §4.1) so the app-layer gate can
            // attribute one to the write it brackets.
            ["INV"] = HandleRefusal,     // " INV SELF ADDRESS ", " INV ASSOC SELF ", …
            // " INVALID ADDRESS " — the schedule/membership family's refusal
            // (a STO with nothing queued; a bare ADDM <net>). Routed round 11.
            // "INVALID ENCR KEY" / "INVALID MODEM PRESET" are OTHER domains'
            // rejects and STAY on the Noise path — hence the EXACT payload
            // match rather than a prefix: this branch may never widen to them.
            ["INVALID"] = c =>
            {
                if (c.Payload == "ADDRESS") HandleRefusal(c);
            },
            ["ADDRESS"] = HandleRefusal, // " ADDRESS EXISTS " — names are global
            // The phase-1/2 refusal tokens (2026-08-17): duplicate ADDM, a
            // re-STA on a queued target, the full LQA queue, and the per-kind
            // schedule gates. Routed so the programming/schedule surfaces can
            // attribute them; INDIV/SELF guard on payload because bare
            // "SELF"-token lines could someday mean something else.
            ["DUPLICATE"] = HandleRefusal,   // " DUPLICATE MEMBER "
            ["ADR"] = HandleRefusal,         // " ADR ALREADY QUED "
            // Guarded routes: the EXACT captured payload is the refusal; any
            // other payload opts OUT of Handled (the HandleChannelGroup /
            // SCAN precedent documented at the dispatch site) so it surfaces
            // through the unrecognized-line path instead of vanishing (Sol
            // audit finding 4 — the prefix form left unmatched payloads
            // half-handled: Handled true, nothing recorded).
            ["LQA"] = c => { if (c.Payload == "QUEUE FULL") HandleRefusal(c); else c.Result.Handled = false; },
            ["INDIV"] = c => { if (c.Payload == "CHANS REQD") HandleRefusal(c); else c.Result.Handled = false; },
            ["SELF"] = c => { if (c.Payload == "CHANS REQD") HandleRefusal(c); else c.Result.Handled = false; },

            // ---- HOP --------------------------------------------------
            // " NET CHANS REQD " (phase 2, 2026-08-17) is a SCHEDULE refusal
            // that begins with the HOP net token - guard it before ParseInt
            // throws a payload error on "CHANS".
            ["NET"] = c =>
            {
                // EXACT captured payload (Sol audit finding 6): the prefix form
                // recorded "NET CHANS REQD EXTRA" as the known refusal. An
                // unmatched NET form now falls to the numeric path below and
                // surfaces as a payload error — correct-and-loud.
                if (c.Payload == "CHANS REQD") { HandleRefusal(c); return; }
                Track(c, _state.Hop.SetCurrentNet(ParseInt(c, FirstWord(Require(c)))));
            },
            ["NETID"] = HandleNetId,
            ["HOPTYPE"] = HandleHopType,
            ["CENTER"] = HandleHopCenter,
            ["HOPNUM"] = c =>
            {
                _state.Hop.SetHopNum(ParseInt(c, Require(c)));
                _state.Hop.SetGeneratingHopset(false);
                c.Result.Changed = true;
            },
            ["GENERATING"] = c => { _state.Hop.SetGeneratingHopset(true); c.Result.Changed = true; },
            ["NO_HOPSET"] = c =>        // SH-block form ("No_Hopset"); async form is "No Hopset"
            {
                _state.Hop.SetHopNum(0);
                _state.Hop.SetGeneratingHopset(false);
                _state.Hop.NotifyNoHopset();   // always signalled: HopNum may already be 0 (audit F4)
                c.Result.Changed = true;
            },
            // SH-block form ("No_Net_ID"); async form is "NO NET ID", handled
            // in the ["NO"] branch above. Captured 2026-08-16 — a net with a
            // hopset but no NETID refuses to generate. Previously UNRECOGNIZED.
            ["NO_NET_ID"] = c =>
            {
                _state.Hop.SetHopNum(0);
                _state.Hop.SetGeneratingHopset(false);
                _state.Hop.NotifyNoNetId();
                c.Result.Changed = true;
            },
            ["NO_SYNC"] = c => Sync(c, HopSyncState.NoSync),
            ["IN_SYNC"] = c => Sync(c, HopSyncState.InSync),
            ["AWAITING_SYNC"] = c => Sync(c, HopSyncState.AwaitingSync),
            ["SENDING_SYNC_REQ"] = c => Sync(c, HopSyncState.SendingSyncRequest),
            ["SYNC_REQ_RCV"] = c => Sync(c, HopSyncState.SyncRequestReceived),
            ["SENDING_SYNC_RSP"] = c => Sync(c, HopSyncState.SendingSyncResponse),
            ["SYNC_FAILED"] = c => Sync(c, HopSyncState.SyncFailed),
            // "List_Invalid" — LIST-type net the radio refuses to sync on
            // (hoplist too short; bench 2026-08-01). Operator-facing.
            ["LIST_INVALID"] = c =>
            {
                _state.Hop.SetHopListInvalid(true);
                _state.Hop.SetGeneratingHopset(false);
                c.Result.Changed = true;
            },
            // "Bad Hopset" (async, WITH a space:
            // bench/transcripts/r14-coupler-20260820-121753.jsonl record 240)
            // and "Bad_Hopset" (the SH sync-state slot, record 265): the FIFTH
            // generation refusal (protocol.md, the Bad_Hopset section). BOTH
            // spellings were unrecognized, so every HOP `SH` of such a net
            // raised an "Unrecognized message" banner at the operator — the
            // class the WB_INVALID/EXCLUSIONS keys closed below.
            //
            // Recognised; generation is over; NOTHING ELSE is mirrored — the
            // WB_INVALID precedent: what the refusal MEANS for the fill is a
            // probe question (the span boundary is located only to
            // (1000, 2000]). `BAD` with any OTHER payload opts out of Handled
            // (the NIT-1 idiom) so an unseen `BAD …` line still surfaces.
            ["BAD"] = c => { if (c.Payload == "HOPSET") EndGeneration(c); else c.Result.Handled = false; },
            ["BAD_HOPSET"] = EndGeneration,
            // "HOPLIST 03   11010  11015  11020" (session-16)
            ["HOPLIST"] = c =>
            {
                var parts = (c.Payload ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) return;
                int net = ParseInt(c, parts[0]);
                _state.Hop.SetHopList(net, parts[1..]);
                // Arm the wrap continuation (captured 2026-08-17): the answer
                // wraps at 8 frequencies/line, continuations being bare 5-digit
                // values. Without this a >8-frequency list silently truncated.
                _continuation = Continuation.HoplistFreqs;
                _hoplistNet = net;
                _hoplistFreqs = [.. parts[1..]];
                c.Result.Changed = true;
            },
            // "Hopset 00  XXXXXX  XXXXXX" (DIS output / HOPSET DEL echo) —
            // the WB band edges. Round-5 BD3: mirrored, not discarded.
            ["HOPSET"] = HandleHopset,
            // "Exclude 00  02000   03000 " (session-16; re-captured
            // 2026-08-17 as BOTH the set echo and the bulk listing's row).
            // Round 11 (R11/X9): mirrored — the exclusion-band editor renders
            // from it. 8-digit Hz goes IN, 5-digit kHz comes back OUT.
            ["EXCLUDE"] = HandleExcludeBand,
            // Two bare, payload-less HOP markers captured 2026-08-18
            // (bench/transcripts/r11-exclude-*): the HOP `SH` block ends with
            // "Exclusions" when the exclusion table is non-empty, and
            // "WB_Invalid" rides both `SH` and every `EXC` write's regeneration
            // answer. Both were UNRECOGNIZED — i.e. every exclusion write and
            // every HOP `SH` raised an "Unrecognized message" banner at the
            // operator. Recognized as noise, deliberately not mirrored:
            // "Exclusions" says nothing the table itself does not, and
            // WB_Invalid's CAUSE is an OPEN probe (round-11 EXCLUDE family), so
            // a mirror would be a guess about what it means.
            ["EXCLUSIONS"] = Noise,
            ["WB_INVALID"] = Noise,

            // ---- Clone round 12 §3: OPERATOR LOCKOUTS -------------------
            // The two global state reports, sectioned by their own headers.
            // The header is the ONLY thing that attributes a row to a section
            // (item names repeat across sections), so it is tracked here and
            // the rows are refused when no header has arrived.
            [">>SSB_PROGRAMMABLE_PARAMETERS"] = c => LockoutHeader(c, LockoutFamily.Program, LockoutSection.Ssb),
            [">>HOP_PROGRAMMABLE_PARAMETERS"] = c => LockoutHeader(c, LockoutFamily.Program, LockoutSection.Hop),
            [">>EAM_PROGRAMMABLE_PARAMETERS"] = c => LockoutHeader(c, LockoutFamily.Program, LockoutSection.Eam),
            [">>SSB_SELECTABLE_PARAMETERS"] = c => LockoutHeader(c, LockoutFamily.Select, LockoutSection.Ssb),
            [">>HOP_SELECTABLE_PARAMETERS"] = c => LockoutHeader(c, LockoutFamily.Select, LockoutSection.Hop),
            [">>EAM_SELECTABLE_PARAMETERS"] = c => LockoutHeader(c, LockoutFamily.Select, LockoutSection.Eam),
            ["PROGRAM"] = c => LockoutRowLine(c, LockoutFamily.Program),
            ["SELECT"] = c => LockoutRowLine(c, LockoutFamily.Select),

            // §9 A1: "PRESET DISABLED" — the answer to selecting a modem preset
            // the radio has locked out. It had NO dispatch key at all, so the
            // app raised the verbatim "Unrecognized message" banner (which is
            // how its spelling was captured). Any OTHER payload opts OUT of
            // Handled, the SCAN/CHGROUP precedent: an unseen PRESET form
            // surfaces honestly instead of being swallowed by this branch.
            ["PRESET"] = c => { if (c.Payload != "DISABLED") c.Result.Handled = false; },

            // §9 C3: "FORCE WAKEUP ENABLED" — the ONE direction this radio
            // reports. Round 11 discarded it as noise because a mirror could
            // latch stale (DIS is silent, a bare query answers nothing); round
            // 12 mirrors it as a BOUNDED SESSION LATCH instead — see
            // RadioState.ForceWakeup for exactly what it may and may not claim.
            // Guarded on the captured payload so an unseen FORCE form still
            // surfaces (RE-CONFIRMED 2026-08-18, P-2 step e: a SECOND
            // `FORCE_W ENA` re-answers the same line).
            ["FORCE"] = c =>
            {
                if (c.Payload == "WAKEUP ENABLED") Track(c, _state.SetForceWakeupEnabled());
            },
        };
    }

    /// <summary>Only the EXACT generic banner is the syntax reject (§9 B2).
    /// Written as its own predicate so the discrimination is one named rule
    /// rather than a condition buried in a branch.</summary>
    private static bool IsGenericErrorBanner(string upper)
        => upper.Trim() == "** ERROR **";

    // ---- Lockout report parsing (clone round 12 §3) ----------------------

    /// <summary>The section the CURRENT report is inside, or null before any
    /// header has arrived. Reset like every other block state on connect.</summary>
    private (LockoutFamily Family, LockoutSection Section)? _lockoutSection;

    private void LockoutHeader(Ctx c, LockoutFamily family, LockoutSection section)
    {
        _lockoutSection = (family, section);
        c.Result.Changed = true;
    }

    /// <summary>
    /// One <c>PROGRAM &lt;ITEM&gt; LOCK|UNLOCK</c> / <c>SELECT …</c> line.
    ///
    /// <para>Refused — opting OUT of Handled, so the line surfaces through the
    /// unrecognized path — when it does not carry the two-token item/state
    /// shape, when no section header has been seen, when the header belongs to
    /// the OTHER family, or when the row is outside the CLOSED 22-item
    /// inventory. That last case is invariant 2's whole point: a
    /// twenty-third item is a loud fact, never a silently grown mirror.</para>
    /// </summary>
    private void LockoutRowLine(Ctx c, LockoutFamily family)
    {
        var parts = (c.Payload ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) { c.Result.Handled = false; return; }

        LockState state;
        if (parts[1] == "LOCK") state = LockState.Lock;
        else if (parts[1] == "UNLOCK") state = LockState.Unlock;
        else { c.Result.Handled = false; return; }

        // An `ALL LOCK`/`ALL UNLOCK` echo names no item — recognized (it IS a
        // lockout line) but it mirrors nothing, and it moves the whole table,
        // so it invalidates the mirror exactly like any other unattributable
        // set echo.
        if (parts[0] == "ALL")
        {
            if (!_state.IsLockoutReadActive) _state.InvalidateLockouts();
            c.Result.Changed = true;
            return;
        }

        // Outside the CLOSED inventory for this family in EVERY section — so
        // it is not a row this radio has, whatever the section turns out to be.
        // Refused before the attribution logic, because "unattributable" and
        // "not a thing" are different answers and only the second is a defect.
        if (!LockoutInventory.ContainsItem(family, parts[0]))
        {
            c.Result.Handled = false;
            return;
        }

        if (_lockoutSection is not { } header || header.Family != family)
        {
            // No header: this is a SET ECHO, not a report row. Its section is
            // unattributable from the line alone, so nothing is mirrored — the
            // store marks itself unread and the campaign re-reads.
            if (!_state.IsLockoutReadActive) _state.InvalidateLockouts();
            c.Result.Changed = true;
            return;
        }

        if (!_state.ApplyLockoutRow(header.Family, header.Section, parts[0], state))
        {
            c.Result.Handled = false;
            return;
        }
        c.Result.Changed = true;
    }

    // ------------------------------------------------------------------

    /// <summary>Remove every control character from a framed line. The wire
    /// vocabulary is printable ASCII and the framer has already consumed the
    /// CR/LF that carry meaning, so anything left is line noise or a bell —
    /// see the note at the call site for the captures that forced this.
    /// Returns the input unchanged when there is nothing to strip (the common
    /// case allocates nothing).</summary>
    internal static string StripControlCharacters(string line)
    {
        bool any = false;
        foreach (var ch in line)
            if (char.IsControl(ch)) { any = true; break; }
        if (!any) return line;

        var sb = new System.Text.StringBuilder(line.Length);
        foreach (var ch in line)
            if (!char.IsControl(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static void Noise(Ctx c) { }

    private static void Track(Ctx c, bool changed) { if (changed) c.Result.Changed = true; }

    /// <summary>ALE ON/OFF settings line ("ALL_CALL    ON  " — the SH block
    /// pads with spaces; the payload is already trimmed).</summary>
    private static void AleOnOff(Ctx c, Action<OnOff> apply)
    {
        apply(Wire.ParseOnOff(Require(c)) ?? throw Bad(c, c.Result.Token));
        c.Result.Changed = true;
    }

    private static string Require(Ctx c) =>
        c.Payload ?? throw new FormatException($"{c.Result.Token} line carried no payload: '{c.Raw}'");

    private static FormatException Bad(Ctx c, string token) =>
        new($"Unrecognized {token} payload: '{c.Payload}'");

    private static int ParseInt(Ctx c, string s)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"Unrecognized {c.Result.Token} payload: '{c.Payload}'");
        return v;
    }

    private void Mode(Ctx c, OperatingMode mode)
    {
        // A prompt ENDS a lockout report (protocol.md: multi-line blocks end at
        // the next prompt). Without this, a set ECHO arriving later would be
        // attributed to the last report's section — inventing exactly the fact
        // the (family, section, item) keying exists to protect.
        _lockoutSection = null;
        Track(c, _state.SetOperatingMode(mode));
    }

    private void Sync(Ctx c, HopSyncState s) { _state.Hop.SetSyncState(s); c.Result.Changed = true; }

    /// <summary>A generation-refusal token: the generation is over and nothing
    /// else is claimed (round 16 fixes S3).</summary>
    private void EndGeneration(Ctx c) { _state.Hop.SetGeneratingHopset(false); c.Result.Changed = true; }

    private static string FirstWord(string s)
    {
        int i = s.IndexOf(' ');
        return i > 0 ? s[..i] : s;
    }

    /// <summary>The captured progress shape both call and LQA lines share:
    /// <c>&lt;station&gt;   CHANNEL: nn</c>.</summary>
    private static readonly Regex ProgressShape = new(@"^(\S+)\s+CHANNEL:\s*(\S+)", RegexOptions.None);

    /// <summary>The channel spelling every captured progress row prints:
    /// EXACTLY two digits (`CHANNEL: 29`, `CHANNEL: 01`). Used by the
    /// <c>LINKED</c> branch to refuse anything <see cref="ProgressShape"/>'s
    /// deliberately loose `(\S+)` would otherwise mirror verbatim.</summary>
    private static bool IsCapturedChannel(string channel) =>
        channel.Length == 2 && char.IsAsciiDigit(channel[0]) && char.IsAsciiDigit(channel[1]);

    private void HandleCallProgress(Ctx c, AleLinkState newState)
    {
        _state.Ale.SetLinkState(newState);
        // "CALLING  BOB              CHANNEL: 00"
        var m = ProgressShape.Match(c.Payload ?? "");
        if (m.Success)
            _state.Ale.SetLinkedStation(m.Groups[1].Value, m.Groups[2].Value);
        c.Result.Changed = true;
    }

    /// <summary>
    /// One LQA progress line — <c>SOUNDING W6HOS            CHANNEL: 30</c> or
    /// <c>EXCHANGE KC1HAS           CHANNEL: 30</c> (round 15 item I, probes
    /// P14b/P14c): the radio prints one per channel of the target's group while
    /// a bare <c>STA</c> runs. The same shape <c>CALLING</c> uses, but UNLIKE
    /// <see cref="HandleCallProgress"/> the state moves only when the shape
    /// MATCHES — a bare "SOUNDING XYZ" is not a line this parser claims to
    /// understand, and a malformed one must not flip the banner (it takes the
    /// unrecognized-line path instead). The station/channel land in the LQA's
    /// own slot, never the call slot (critic F73).
    /// </summary>
    private void HandleLqaProgress(Ctx c, AleLinkState newState)
    {
        var m = ProgressShape.Match(c.Payload ?? "");
        if (!m.Success) { c.Result.Handled = false; return; }
        // Slot BEFORE state, matching the terminator's own order in
        // AleState.SetLinkState: the banner is composed from the state AND the
        // slot, so no notification ever announces an LQA state whose
        // station/channel have not landed yet.
        _state.Ale.SetLqaProgress(m.Groups[1].Value, m.Groups[2].Value);
        _state.Ale.SetLinkState(newState);
        c.Result.Changed = true;
    }

    private void HandleAddress(Ctx c, AleAddressKind kind)
    {
        // "CAM               CHGROUP 01" | "BOB               CHGROUP 01   ASSOC SELF CAM"
        var m = Regex.Match(c.Payload ?? "", @"^(\S+)\s+CHGROUP\s+(\d+)(?:\s+ASSOC SELF\s+(\S+))?");
        if (!m.Success) return;    // fill-gate trailer or bare query echo — ignore

        _state.Ale.UpsertAddress(kind, new AleAddress
        {
            Address = m.Groups[1].Value,
            ChannelGroup = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            AssociatedSelf = m.Groups[3].Success ? m.Groups[3].Value : null,
        });
        c.Result.Changed = true;
    }

    /// <summary>
    /// The scan channel-group answer: "CHGROUP 01 CHANS 00 01 " (probe R7,
    /// trailing space and all). Every channel arrives on ONE line — any
    /// count — because that is what is captured.
    ///
    /// <para><b>DOMAIN ENFORCEMENT.</b> A group outside 0-9 or ANY channel
    /// outside 0-99 makes the whole line un-honest-parseable: it is ignored
    /// WITHOUT mutating the group table, and (like the SCAN payload rule,
    /// audit NIT-1) the line opts OUT of Handled so it surfaces through the
    /// existing unrecognized-line path instead of vanishing. Same for a
    /// CHGROUP line carrying no CHANS token.</para>
    ///
    /// <para><b>THE WRAP IS REAL — captured 2026-08-17</b> (phase 2, closing
    /// the A7c stated limitation): a 40-channel group prints 20 channels per
    /// line, the continuation lines being BARE numbers with no token. Parsed
    /// via the ChgChans continuation armed below; an orphan wrap line (no
    /// CHGROUP line before it) still surfaces as unrecognized.</para>
    /// </summary>
    private void HandleChannelGroup(Ctx c)
    {
        var m = Regex.Match(c.Payload ?? "", @"^(\d+)\s+CHANS((?:\s+\d+)*)\s*$");
        if (!m.Success) { c.Result.Handled = false; return; }

        int group = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (group is < 0 or > 9) { c.Result.Handled = false; return; }

        var channels = new List<int>();
        foreach (var token in m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel)
                || channel is < 0 or > 99)
            {
                c.Result.Handled = false;      // whole line ignored, nothing mutated
                return;
            }
            channels.Add(channel);             // radio's order, un-deduplicated
        }

        _state.Ale.ApplyChannelGroup(group, channels);
        // Arm the wrap continuation (captured 2026-08-17): the next line may be
        // bare channel numbers continuing THIS group's listing. The stated
        // limitation above is now half-history — the wrap exists at >20
        // channels and is parsed; a wrap line still surfaces as unrecognized
        // only if it arrives with no CHGROUP line before it.
        _continuation = Continuation.ChgChans;
        _chgGroup = group;
        _chgChannels = channels;
        c.Result.Changed = true;
    }

    /// <summary>
    /// One <c>MEMBER nn  &lt;addr&gt;</c> continuation of a targeted NETAD read
    /// (captured 2026-08-17: five leading spaces, two between the number and
    /// the address). ANCHORED at both ends and sized — a line that is not
    /// exactly "index then one address token" is not this shape, and opts OUT
    /// of Handled so it surfaces through the unrecognized path rather than
    /// entering a membership mirror as something invented (the HandleChannel-
    /// Group precedent).
    /// </summary>
    private void HandleNetMember(Ctx c)
    {
        var m = Regex.Match(c.Payload ?? "", @"^(\d+)\s+(\S+)$");
        if (!m.Success) { c.Result.Handled = false; return; }
        _state.Ale.ApplyNetMember(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value);
        c.Result.Changed = true;
    }

    /// <summary>
    /// One schedule row from the bare-<c>EXCH</c> listing:
    /// <c>EXCHANGE I1              INTERVAL 01:00 START TIME 22:34</c>
    /// (captured 2026-08-17, both the EXCHANGE and SOUND spellings). Anchored
    /// and sized like every other listing shape; interval/start are stored
    /// VERBATIM because the radio does not validate them (<c>00:00</c> and
    /// <c>24:00</c> both store).
    /// </summary>
    private void HandleLqaSchedule(Ctx c, LqaScheduleKind kind)
    {
        if (!TryLqaSchedule(c, kind)) c.Result.Handled = false;
    }

    /// <summary>The heard-station payload both `SOUND FROM:` and `RESP FROM:`
    /// print (field capture 2026-08-24 #2) — anchored, two-digit channel,
    /// the LINKED discipline.</summary>
    private static readonly Regex HeardFromShape = new(
        @"^FROM:\s+(\S+)\s+CHANNEL:\s*(\d\d)$", RegexOptions.Compiled);

    /// <summary>The schedule-row half of <see cref="HandleLqaSchedule"/>, split
    /// out for the <c>EXCHANGE</c> token's two-shape branch (round 15 item I).
    /// Returns false — having written NOTHING — when the payload is not the
    /// anchored listing shape, so the caller decides what the line is instead.</summary>
    private bool TryLqaSchedule(Ctx c, LqaScheduleKind kind)
    {
        var m = Regex.Match(c.Payload ?? "",
            @"^(\S+)\s+INTERVAL\s+(\d{2}:\d{2})\s+START TIME\s+(\d{2}:\d{2})$");
        if (!m.Success) return false;
        _state.Ale.ApplyLqaSchedule(new LqaSchedule(
            kind, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
        c.Result.Changed = true;
        return true;
    }

    /// <summary>
    /// One exclusion band: <c>Exclude 00  02000   03000 </c> — a band slot then
    /// two 5-digit kHz edges (the radio takes 8-digit Hz on the way IN and
    /// prints kHz on the way OUT). Anchored and SIZED, the HandleHopset
    /// discipline: a PROVISIONAL multi-row shape is exactly the wrong place to
    /// be liberal, so a line with a wrong-width value or anything trailing is
    /// not one of the shapes this parser claims to understand.
    /// </summary>
    private void HandleExcludeBand(Ctx c)
    {
        var m = Regex.Match(c.Payload ?? "", @"^(\d+)\s+(\d{5})\s+(\d{5})$");
        if (!m.Success) { c.Result.Handled = false; return; }
        int band = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (band is < 0 or > 9) { c.Result.Handled = false; return; }
        _state.Hop.ApplyExcludeBand(band, m.Groups[2].Value, m.Groups[3].Value);
        c.Result.Changed = true;
    }

    /// <summary>Record a radio refusal line VERBATIM (already trimmed) in the
    /// ALE mirror. The mirror records every one it routes; deciding whether a
    /// given refusal belongs to a programming write is the app-layer gate's
    /// job, never the parser's.</summary>
    private void HandleRefusal(Ctx c)
    {
        _state.Ale.NoteProgrammingRefusal(c.Raw);
        c.Result.Changed = true;
    }

    private void HandleTxMsgHeader(Ctx c)
    {
        // "TXMSG 09" header — message text follows on the NEXT line.
        if (int.TryParse(c.Payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
        {
            _pendingTxMsgSlot = slot;
            _continuation = Continuation.TxMsgText;
        }
    }

    /// <summary>The received-AMD announcement's captured shape (field
    /// transcript 2026-08-24, 22:06:59). Only the FROM/DATE/TIME payload
    /// arms the text continuation; any other RXMSG payload keeps the
    /// pre-capture behavior — recognized and surfaced raw.</summary>
    private static readonly Regex RxMsgHeaderShape = new(
        @"^(\d\d)\s+FROM\s+(\S+)\s+DATE:\s*(\S+)\s+TIME:\s*(\S+)$",
        RegexOptions.Compiled);

    private void HandleRxMsgHeader(Ctx c)
    {
        var m = RxMsgHeaderShape.Match((c.Payload ?? "").Trim());
        if (!m.Success) { c.Result.Changed = true; return; }
        _pendingRxMsg = new RxAmdMessage
        {
            Slot = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            From = m.Groups[2].Value,
            Date = m.Groups[3].Value,
            Time = m.Groups[4].Value,
            Text = "",
        };
        _continuation = Continuation.RxMsgText;
        c.Result.Changed = true;
    }

    private void ApplyRxMsgText(string text)
    {
        if (_pendingRxMsg is null) return;
        _state.Ale.AppendRxMessage(_pendingRxMsg with { Text = text.Trim() });
        _pendingRxMsg = null;
    }

    private void ApplyTxMsgText(string text)
    {
        if (_pendingTxMsgSlot < 0) return;
        _state.Ale.UpsertTxMessage(new AmdMessage { Slot = _pendingTxMsgSlot, Text = text.Trim() });
        _pendingTxMsgSlot = -1;
    }

    private void HandleRankLine(Ctx c)
    {
        if (_continuation != Continuation.RankReport) return;
        // "00  SCORE: ---    MEASURED SNR --  RECEIVED SNR --"
        var m = Regex.Match(c.Payload ?? "", @"^(\S+)\s+SCORE:\s*(\S+)\s+MEASURED SNR\s+(\S+)\s+RECEIVED SNR\s+(\S+)");
        if (!m.Success) return;
        _state.Ale.AppendLqaScore(new LqaScore
        {
            Station = _rankStation ?? "",
            Channel = m.Groups[1].Value,
            Score = m.Groups[2].Value,
            MeasuredSnr = m.Groups[3].Value,
            ReceivedSnr = m.Groups[4].Value,
        });
        c.Result.Changed = true;
    }

    private void HandleNetId(Ctx c)
    {
        // "00  12345678" | "00  XXXXXXXX"
        var m = Regex.Match(c.Payload ?? "", @"^(\d+)\s+(\S+)");
        if (!m.Success) throw Bad(c, "NETID");
        int net = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string? id = m.Groups[2].Value.StartsWith('X') ? null : m.Groups[2].Value;
        // The X-form is the radio REPORTING the net unprogrammed — the only
        // honest signal there is (a wiped net also reports a Hoptype line, so
        // "record with only a type" proves nothing). Mark it, so consumers can
        // tell it from a net whose ID was simply never mentioned; a real ID
        // report clears the marker in the same assignment.
        _state.Hop.UpdateNet(net, n => n with { NetId = id, IsReportedUnprogrammed = id is null });
        c.Result.Changed = true;
    }

    private void HandleHopType(Ctx c)
    {
        var m = Regex.Match(c.Payload ?? "", @"^(\d+)\s+(\S+)");
        if (!m.Success) throw Bad(c, "HOPTYPE");
        int net = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var type = Wire.ParseHopType(m.Groups[2].Value) ?? throw Bad(c, "HOPTYPE");
        _state.Hop.UpdateNet(net, n => n with { Type = type });
        c.Result.Changed = true;
    }

    private void HandleHopCenter(Ctx c)
    {
        var m = Regex.Match(c.Payload ?? "", @"^(\d+)\s+(\S+)");
        if (!m.Success) throw Bad(c, "CENTER");
        int net = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string? center = m.Groups[2].Value.StartsWith('X') ? null : m.Groups[2].Value;
        _state.Hop.UpdateNet(net, n => n with { CenterKHz = center });
        c.Result.Changed = true;
    }

    private void HandleHopset(Ctx c)
    {
        // The DIS answer's WB value line, and the HOPSET n DEL echo:
        //   "Hopset 02  02000  08000"   band edges, 5-digit kHz
        //   "Hopset 00  XXXXXX  XXXXXX" the CAPTURED wiped/unprogrammed form
        //                               (probe R9b — DIS output AND the echo)
        //
        // PROVISIONAL (round-5 §2.1.3): only the placeholder form is captured.
        // The programmed form is patterned off it (same columns, kHz values —
        // the peer Exclude line prints its band the same way, session-16); the
        // previous-generation app never parsed this line at all, so it settles
        // nothing. docs/protocol.md carries the marking and the bench item.
        //
        // A line fitting NEITHER shape stays noise instead of raising a
        // PayloadError against a shape we are guessing (the HandleAddress
        // precedent) — which is also exactly what this token did before.
        //
        // ANCHORED AND SIZED, both ends (C1 audit round 1, BLOCKER). The first
        // version matched `^(\d+)\s+(\S+)\s+(\S+)` and so accepted ANY two
        // non-space tokens with anything trailing: the auditor mirrored
        // "Hopset 02  2000  08000" (4-digit) and "Hopset 02  020000  08000"
        // (6-digit) as if they were band edges. A PROVISIONAL shape is exactly
        // the wrong place to be liberal — if the real radio prints something
        // else, this must ignore it and leave the cell unreported (which the
        // bench item then catches), not mirror a value of unknown units.
        var m = Regex.Match(c.Payload ?? "", HopsetLine);
        if (!m.Success) return;
        int net = int.Parse(m.Groups["net"].Value, CultureInfo.InvariantCulture);
        // The wiped alternative captures no edges, so its groups are empty —
        // that IS the "both null" case, and no mixed form can reach here.
        // Both fields are mutated in ONE record update, i.e. BEFORE the single
        // HopNets raise (round-4 Phase-D precedent): no Changed handler can
        // observe one edge without the other, or a new low against the
        // previous high.
        bool programmed = m.Groups["low"].Success;
        _state.Hop.UpdateNet(net, n => n with
        {
            WidebandLowKHz = programmed ? m.Groups["low"].Value : null,
            WidebandHighKHz = programmed ? m.Groups["high"].Value : null,
        });
        c.Result.Changed = true;
    }

    /// <summary>The DIS <c>Hopset</c> value line, EXACTLY the two declared
    /// shapes and nothing else: a net number then either two 5-digit kHz edges
    /// (the PROVISIONAL programmed form) or two six-character X placeholders
    /// (the CAPTURED wiped form, probe R9b). Anchored at both ends — a line
    /// with a wrong-width value, a third value or any trailing text is not one
    /// of the shapes this parser claims to understand.</summary>
    private const string HopsetLine =
        @"^(?<net>\d+)\s+(?:(?<low>\d{5})\s+(?<high>\d{5})|X{6}\s+X{6})$";

    private void HandlePrePost(Ctx c)
    {
        // "PREPOST FILTER ENABLE" | "PREPOST RXANTENNA DISABLE" |
        // "PREPOST SCAN SLOW" (session-20 capture). Values verbatim.
        if (string.IsNullOrEmpty(c.Payload)) return;   // bare query/echo — ignore

        var sub = FirstWord(c.Payload);
        var rest = c.Payload.Length > sub.Length ? c.Payload[sub.Length..].Trim() : "";
        if (rest.Length == 0) throw Bad(c, "PREPOST");

        switch (sub)
        {
            case "FILTER": Track(c, _state.SetPrePostFilter(rest)); break;
            case "RXANTENNA": Track(c, _state.SetPrePostRxAntenna(rest)); break;
            case "SCAN": Track(c, _state.SetPrePostScanRate(rest)); break;
            default: throw Bad(c, "PREPOST");
        }
    }

    private void HandlePortRemote(Ctx c)
    {
        // "ECHO OFF" | "BAUD 9600" | … (bare PORT_R dumps the configuration)
        var sub = FirstWord(c.Payload ?? "");
        var rest = c.Payload is not null && c.Payload.Length > sub.Length
            ? c.Payload[sub.Length..].Trim() : "";

        if (sub == "ECHO")
        {
            var v = Wire.ParseOnOff(rest) ?? throw Bad(c, "PORT_REMOTE ECHO");
            Track(c, _state.SetPortRemoteEcho(v));
            return;
        }
        _state.SetPortConfig(sub, rest);
        c.Result.Changed = true;
    }

    private void HandleModem(Ctx c)
    {
        var sub = FirstWord(c.Payload ?? "");

        if (sub == "OFF")
        {
            Track(c, _state.SetActiveModem("OFF"));
        }
        else if (sub == "PRESET")
        {
            // "MODEM PRESET 1 T39  ASYNC DATA   BAUD 2400  TYPE 39tone …" —
            // a stored-preset line. Round 8 (EE, scope amendment X7): the
            // LISTING form (and the programming echo, which shares it) feeds
            // the ModemPresets mirror. The MODEM SH STATUS form is the same
            // fields REORDERED — the bench-pinned discriminator is field
            // ORDER, not arrival order (protocol.md "MODEM SH vs MODEM PRE"):
            // a listing carries ASYNC/SYNC before TYPE; the status form
            // carries TYPE first. Status lines stay recognized-only (the
            // active preset is learned from the short selection echo / the
            // SH-block short form, never from this line).
            // Discriminate on the UPPER form; STORE the raw-case payload —
            // the radio writes "39tone"/"long" in its own casing and the
            // mirror is verbatim. R8-review MAJOR 2: BOTH tokens must be
            // present — the pin is "ASYNC/SYNC BEFORE TYPE", and a line
            // missing either token is an UNCAPTURED shape that stays
            // recognized-only (never guessed into the mirror).
            var rest = c.Payload is not null && c.Payload.Length > sub.Length
                ? c.Payload[sub.Length..].Trim() : "";
            var rawRest = c.RawPayload is not null && c.RawPayload.Length > sub.Length
                ? c.RawPayload[sub.Length..].Trim() : "";
            var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int data = Array.FindIndex(tokens, t => t is "ASYNC" or "SYNC");
            int type = Array.FindIndex(tokens, t => t == "TYPE");
            int baud = Array.FindIndex(tokens, t => t == "BAUD");
            // CLONE-FIELD ROUND 2 F9 — the SHORT `HOP>` FORM.
            // `MODEM PRE 7` at a `HOP>` prompt answers
            // `MODEM PRESET 7 DAT7 ASYNC REMOTE BAUD 300` — the same listing
            // line MINUS the TYPE and INTER columns, because a HOP preset has
            // no type field at all (P5/P5b; transcripts
            // bench/transcripts/p5-hop-modem-presets-20260821-180547.jsonl and
            // p5b-hop-modem-preset-write-20260821-181018.jsonl). The round-8
            // discriminator required TYPE to be present, so every 7-9 row was
            // dropped as an uncaptured shape and the clone could not carry
            // them.
            //
            // The discriminator is still STRUCTURAL and still refuses to guess:
            //   * TYPE present  → the LISTING order rule stands, ASYNC/SYNC
            //     BEFORE TYPE (the `MODEM SH` STATUS form puts TYPE first and
            //     stays recognized-only);
            //   * TYPE absent   → the short form, and it must carry BAUD AFTER
            //     the mode phrase, which is the whole of the captured shape.
            //     A line missing either is an uncaptured shape and still rides
            //     the recognized-only path.
            //
            // At `HOP>` the `MODEM SH` answer IS this same short line (P5d2),
            // so there is nothing to discriminate there and the upsert records
            // a true fact about the preset either way.
            bool listingForm = type >= 0
                ? data >= 0 && data < type
                : data >= 0 && baud > data;
            if (rawRest.Length > 0 && listingForm)
            {
                _state.UpsertModemPresetLine(rawRest);
                c.Result.Changed = true;
            }
        }
        else if (int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            // Selection echo / SH-block short form: "MODEM 1 T39".
            Track(c, _state.SetActiveModem((c.Payload ?? sub).Trim()));
        }
        else
        {
            throw Bad(c, "MODEM");
        }
    }
}
