using System.Globalization;
using Falcon.App.Core.Cloning;
using Falcon.Core.Protocol;
using Falcon.Core.Radio;

namespace Falcon.App.Tests;

/// <summary>
/// The clone FILE (plan round 11 §9A): its serialization round trip, its
/// per-domain read-state markers, and the LOAD REJECTION rules — unknown
/// version, malformed row, cross-kind duplicate name — each of which must
/// NAME THE OFFENDER rather than fail generically. A file that loads is a
/// file the write campaign is allowed to trust, so every rejection here is
/// load-bearing.
/// </summary>
public class CloneFileTests
{
    /// <summary>A minimal but COMPLETE file — every manifest domain marked
    /// read — so the preflight pins can subtract from a known-good baseline
    /// rather than build one up.</summary>
    internal static CloneFile Complete()
    {
        var file = new CloneFile
        {
            OperatingState = CloneDomainState.Read,
            OperatingMode = "Ssb",
            OperatingChannel = 0,
            OperatingHopNet = 0,
            BookState = CloneDomainState.Read,
            GroupState = CloneDomainState.Read,
            ScheduleState = CloneDomainState.Read,
            ChannelState = CloneDomainState.Read,
            HopNetState = CloneDomainState.Read,
            ExcludeState = CloneDomainState.Read,
            ModemState = CloneDomainState.Read,
            MessageState = CloneDomainState.Read,
            SettingState = CloneDomainState.Read,
            Lockouts = ReadLockouts(),
        };
        file.Selfs.Add(new CloneAddress { Name = "CAM", Group = 2 });
        file.Individuals.Add(new CloneAddress { Name = "BOB", Group = 2, AssociatedSelf = "CAM" });
        file.Nets.Add(new CloneNet { Name = "NET2", Group = 2, AssociatedSelf = "CAM", Members = ["BOB"] });
        return file;
    }

    /// <summary>The WHOLE 100-slot channel inventory, in the default-row shape
    /// <c>DI</c> prints for a never-written slot (protocol.md, "There is no
    /// 'unprogrammed channel' shape").
    /// <para>Needed since round 17 F6 by every fixture that asserts on
    /// <see cref="CloneFile.IncompleteDomains"/> AFTER a load:
    /// <see cref="Complete"/> carries no channels, and a <c>Read</c> channel
    /// domain with anything but 100 slots is now DOWNGRADED at load — so a file
    /// that means to be complete has to carry the inventory the radio really
    /// answers.</para></summary>
    internal static void FillChannels(CloneFile file)
    {
        file.Channels.Clear();
        for (int n = 0; n < 100; n++)
            file.Channels.Add(new CloneChannel
            {
                Number = n, RxFrequency = "01600000", TxFrequency = "01600000",
                Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO",
            });
    }

    /// <summary>A COMPLETE lockout domain: the closed 22-item inventory, read,
    /// with a MIXED state — one row locked — so a fixture cannot pass by
    /// answering one value everywhere.</summary>
    internal static CloneLockouts ReadLockouts() => new()
    {
        State = CloneDomainState.Read,
        Rows =
        [
            .. LockoutInventory.All.Select(k => new CloneLockout
            {
                Family = k.Family.ToString(),
                Section = k.Section.ToString(),
                Item = k.Item,
                State = k is { Family: LockoutFamily.Program, Section: LockoutSection.Ssb, Item: "CHAN" }
                    ? LockState.Lock.ToString()
                    : LockState.Unlock.ToString(),
            }),
        ],
    };

    [Fact]
    public void ARoundTrip_PreservesEveryDomain_AndItsReadStateMarker()
    {
        var file = Complete();
        file.ChannelGroups.Add(new CloneChannelGroup { Group = 1, Channels = [0, 1] });
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "CAM", Interval = "03:00", Start = "13:02" });
        // THE WHOLE 100-SLOT INVENTORY, with slot 1 given values nothing else
        // has: the channel domain's marker only survives a load when the
        // inventory is whole (round 17 F6), and the distinctive row is what
        // proves the PAYLOAD round-tripped rather than merely the count.
        FillChannels(file);
        file.Channels[1] = new CloneChannel
        {
            Number = 1, RxFrequency = "14313500", TxFrequency = "14313500",
            Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO",
        };
        file.HopNets.Add(new CloneHopNet { Number = 0, NetId = "12345678", Type = "NB", CenterKHz = "11565" });
        file.ExcludeBands.Add(new CloneExcludeBand { Band = 0, LowKHz = "02000", HighKHz = "03000" });
        file.ModemPresets.Add(new CloneModemPreset { Number = 1, Fields = "T39 ASYNC DATA", Enabled = true });
        file.Messages.Add(new CloneTxMessage { Slot = 0, Text = "RADIO CHECK" });
        file.Settings.Add(new CloneSetting { Key = "PowerLevel", Value = "High" });
        file.ScheduleState = CloneDomainState.Faulted;

        var reloaded = CloneFile.Load(file.Save());

        Assert.Equal(CloneFile.CurrentVersion, reloaded.Version);
        Assert.Equal("CAM", Assert.Single(reloaded.Selfs).Name);
        Assert.Equal("BOB", Assert.Single(Assert.Single(reloaded.Nets).Members));
        Assert.Equal([0, 1], Assert.Single(reloaded.ChannelGroups).Channels);
        Assert.Equal("13:02", Assert.Single(reloaded.Schedules).Start);
        Assert.Equal(100, reloaded.Channels.Count);
        var slot1 = Assert.Single(reloaded.Channels, c => c.Number == 1);
        Assert.Equal(("14313500", "14313500", "USB", "SL", "2.7", "NO"),
            (slot1.RxFrequency, slot1.TxFrequency, slot1.Mode, slot1.Agc, slot1.Bandwidth, slot1.RxOnly));
        // …and the channel domain's own MARKER survived: a whole inventory is
        // what a `Read` marker has to be backed by since round 17 F6, and this
        // file has one, so nothing downgrades it.
        Assert.Equal(CloneDomainState.Read, reloaded.ChannelState);
        Assert.Empty(reloaded.LoadNotices);
        Assert.Equal("11565", Assert.Single(reloaded.HopNets).CenterKHz);
        Assert.Equal("03000", Assert.Single(reloaded.ExcludeBands).HighKHz);
        Assert.True(Assert.Single(reloaded.ModemPresets).Enabled);
        Assert.Equal("RADIO CHECK", Assert.Single(reloaded.Messages).Text);
        Assert.Equal("High", Assert.Single(reloaded.Settings).Value);
        // The MARKER survives too — a faulted domain must not reload as read.
        Assert.Equal(CloneDomainState.Faulted, reloaded.ScheduleState);
    }

    [Fact]
    public void TheIncompleteDomains_AreTheOnesTheWritePreflightNames()
    {
        var file = Complete();
        Assert.Empty(file.IncompleteDomains);

        file.HopNetState = CloneDomainState.Faulted;
        file.MessageState = CloneDomainState.Unread;

        Assert.Equal(["HOP nets", "stored messages"], file.IncompleteDomains);

        // Anti-vacuity: the manifest really covers every domain the campaign
        // reads, so a domain nobody marked cannot pass the preflight silently.
        // ELEVEN since clone round 12 — the operator lockouts joined (R2).
        Assert.Equal(11, file.ManifestDomains.Count);
        Assert.Contains("operator lockouts", file.ManifestDomains.Select(d => d.Name));
    }

    // ---- The LOCKOUT domain (clone round 12, owner ruling R2) ----------------

    [Fact]
    public void TheLockoutDomain_RoundTrips_KeyedAndComplete()
    {
        var reloaded = CloneFile.Load(Complete().Save());

        Assert.Equal(LockoutInventory.Count, reloaded.Lockouts!.Rows.Count);
        Assert.Equal(CloneDomainState.Read, reloaded.Lockouts.State);
        var locked = Assert.Single(reloaded.Lockouts.Rows, r => r.State == "Lock");
        Assert.Equal(("Program", "Ssb", "CHAN"), (locked.Family, locked.Section, locked.Item));
        // Keyed (family, section, item) EVERYWHERE: the item names really do
        // repeat, which is why nothing may key on the item alone.
        Assert.Equal(3, reloaded.Lockouts.Rows.Count(r => r.Family == "Select" && r.Item == "KEY"));
    }

    [Fact]
    public void AFileWithNoLockoutDomainAtAll_IsRejected_NamingIt()
    {
        // R2 + "there are no old files": MANDATORY, no compat path, no version
        // bump. The property is NULLABLE precisely so ABSENT is distinguishable
        // from a defaulted object — source-generated JSON would otherwise make
        // a missing domain look like an unread one.
        var file = Complete();
        file.Lockouts = null;
        var json = file.Save();
        // The serializer OMITS a null property, which is exactly the shape a
        // file written by an app that never had this domain would have.
        Assert.DoesNotContain("\"Lockouts\"", json, StringComparison.Ordinal);

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(json));
        Assert.Contains("operator lockouts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APresentButUNREADLockoutDomain_LOADS_AndIsRefusedByThePreflightInstead()
    {
        // The other half of the same rule, and what makes the NULLABLE property
        // worth having: absent is MALFORMED, unread is merely INCOMPLETE. A
        // radio the campaign never got to ask is not a corrupt file.
        var file = Complete();
        FillChannels(file);            // …so the lockouts are the ONLY gap (F6)
        file.Lockouts = new CloneLockouts { State = CloneDomainState.Unread };

        var loaded = CloneFile.Load(file.Save());

        Assert.Equal(CloneDomainState.Unread, loaded.Lockouts!.State);
        Assert.Equal(["operator lockouts"], loaded.IncompleteDomains);
    }

    [Theory]
    [MemberData(nameof(MalformedLockouts))]
    public void AMalformedLockoutRow_IsRejected_NamingWhatIsWrong(Action<CloneFile> corrupt, string needle)
    {
        var file = Complete();
        corrupt(file);
        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains(needle, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Action<CloneFile>, string> MalformedLockouts() => new()
    {
        // THE r3 RULE, applied to every enum this domain parses: TryParse alone
        // ACCEPTS undefined numeric text, and the value would then reach a set
        // the radio ECHOES without moving anything.
        { f => f.Lockouts!.Rows[0].Family = "99", "lockout family" },
        { f => f.Lockouts!.Rows[0].Section = "99", "lockout section" },
        { f => f.Lockouts!.Rows[0].State = "99", "lockout state" },
        { f => f.Lockouts!.Rows[0].Family = "Programme", "lockout family" },
        // An item the closed 22-item inventory does not carry.
        { f => f.Lockouts!.Rows[0].Item = "CHANN", "not an operator lockout" },
        // …and one that exists in ANOTHER section: keyed, not named.
        { f => f.Lockouts!.Rows[0].Item = "EXCLUDE", "not an operator lockout" },
        {
            f => f.Lockouts!.Rows.Add(new CloneLockout
            { Family = "Program", Section = "Ssb", Item = "CHAN", State = "Unlock" }),
            "duplicate operator lockout"
        },
        // A domain marked READ that carries fewer than the whole closed set:
        // the write leg sets what it is given, so a short file would leave the
        // rest at whatever the wipe left them and say nothing.
        { f => f.Lockouts!.Rows.RemoveAt(0), "22 rows" },
    };

    [Fact]
    public void AnUndefinedReadStateMarker_IsRejected_NamingTheDomain()
    {
        // The same r3 class one level up: JSON carries the markers as NUMBERS,
        // so `"BookState": 99` deserializes perfectly happily.
        var json = Complete().Save().Replace(
            "\"BookState\": 1", "\"BookState\": 99", StringComparison.Ordinal);
        Assert.Contains("\"BookState\": 99", json, StringComparison.Ordinal);

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(json));
        Assert.Contains("address book", ex.Message, StringComparison.Ordinal);
    }

    // ---- Setting VALUES, proven at the door (P2 audit round 1, BLOCKER) -----

    /// <summary>
    /// THE AUDITOR'S PROBE, verbatim. `"DigitalVoice": "99"` used to LOAD and
    /// pass the write PREFLIGHT: validation checked only that the KEY was a
    /// manifest key, and `Enum.TryParse` alone accepts undefined numeric text —
    /// so the row's own parser refused it in leg 6, which runs AFTER the wipe.
    /// A crafted file could therefore get a radio erased and then fail.
    /// </summary>
    [Fact]
    public void AnUndefinedNumericSettingValue_IsRejectedAtLOAD_NamingTheOffender()
    {
        var file = Complete();
        file.Settings.Add(new CloneSetting { Key = "DigitalVoice", Value = "99" });

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));

        Assert.Contains("DigitalVoice", ex.Message, StringComparison.Ordinal);
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not one this radio accepts", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadSettingValues))]
    public void AStoredSettingValueTheWriteLegCouldNotSend_IsRejectedAtLoad(string key, string value)
    {
        var file = Complete();
        file.Settings.Add(new CloneSetting { Key = key, Value = value });

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> BadSettingValues() => new()
    {
        // ---- SHAPE: not a member / not a number at all ------------------
        { "DigitalVoice", "99" },            // OnOff
        { "Rwas", "7" },                     // EnabledDisabled
        { "PowerLevel", "42" },              // PowerLevel
        { "FrequencyStep", "99" },           // FrequencyStep (6 members, so "3" IS defined)
        { "FmSquelchType", "9" },            // FmSquelchType
        { "PrePostScanRate", "5" },          // PrePostScanRate
        { "Antenna", "88" },                 // AntennaPort
        { "PrePostFilter", "MAYBE" },        // the ENABLE/DISABLE parser
        { "RfGain", "loud" },                // not a number
        { "BfoOffset", "up a bit" },         // not a number
        { "ActiveModem", "THE BIG ONE" },    // the raw-mirror mapping

        // ---- ACCEPTANCE: a well-shaped value the RADIO refuses ----------
        // AUDIT ROUND 2's NINE CASES, VERBATIM. Every one of these parsed as
        // "a number" and was admitted, because the Parse/Send split had moved
        // the SYNTAX to the door and left the ACCEPTED SET on the wire — where
        // the only place to discover it is a leg the wipe precedes.
        { "RfGain", "101" },                 // RF is 0-100
        { "BfoOffset", "10000" },            // BF is a signed FOUR-digit value
        { "CwOffset", "1" },                 // CWOFF is 0 or 1000 — a SET, not a range
        { "FmDeviation", "garbage" },        // FMDE is 5.0 / 6.5 / 8.0
        { "Contrast", "9" },                 // CONT is 0-8
        { "AleMaxScanChannels", "101" },     // MAXCH is 0-100
        { "AleLinkTimeout", "61" },          // TIME_OU is 0-60
        { "AleTuneTime", "0" },              // TUNE is 1-60 — the floor is ONE
        // The ActiveModem row is read and written at `SSB>`, whose band is 0-6
        // (F9: the HOP band's separate engagement is not a manifest row).
        { "ActiveModem", "7 BAD" },

        // …and the far side of each bound, so the rules are not one-sided.
        { "RfGain", "-1" },
        { "BfoOffset", "-10000" },
        { "Contrast", "-1" },
        { "AleLinkTimeout", "-1" },
        { "AleTuneTime", "61" },
        { "AleMaxScanChannels", "-1" },
    };

    /// <summary>
    /// THE BOUNDARY VALUES, ACCEPTED — <b>in their canonical spelling</b>. The
    /// other half of every rule: a bound drawn one step too tight, or a
    /// canonical form written wrongly, would refuse values the radio takes,
    /// and nothing in a rejection theory could tell the difference. The rule
    /// rejects SPELLINGS, never VALUES.
    /// </summary>
    [Theory]
    [InlineData("RfGain", "0")]
    [InlineData("RfGain", "100")]
    [InlineData("BfoOffset", "-9999")]
    [InlineData("BfoOffset", "+9999")]
    [InlineData("CwOffset", "0000")]
    [InlineData("CwOffset", "1000")]
    [InlineData("FmDeviation", "5.0")]
    [InlineData("FmDeviation", "8.0")]
    [InlineData("Contrast", "0")]
    [InlineData("Contrast", "8")]
    [InlineData("AleMaxScanChannels", "0")]
    [InlineData("AleMaxScanChannels", "100")]
    [InlineData("AleLinkTimeout", "0")]        // 0 is MEASURED valid despite HELP's "1-60"
    [InlineData("AleLinkTimeout", "60")]
    [InlineData("AleTuneTime", "1")]
    [InlineData("AleTuneTime", "60")]
    [InlineData("ActiveModem", "OFF")]
    public void AValueAtTheEdgeOfWhatTheRadioAccepts_StillLoads(string key, string value)
    {
        var file = Complete();
        file.Settings.Add(new CloneSetting { Key = key, Value = value });

        Assert.Single(CloneFile.Load(file.Save()).Settings, s => s.Key == key && s.Value == value);
    }

    /// <summary>
    /// <b>NONCANONICAL SPELLINGS — the third form of the door-completeness
    /// defect, and the one that tripped the circuit breaker (audit round 3).</b>
    ///
    /// <para>Every value here is VALID. The radio accepts it, the builder
    /// accepts it, the write SUCCEEDS — and then the byte-exact verify reports
    /// a difference that is not a difference, on an already-wiped radio,
    /// because the wire normalized the spelling on the way out. The reproduced
    /// case: <c>CwOffset "+0000"</c> goes out as <c>CWOFF 0000</c> and reads
    /// back <c>0000</c>.</para>
    ///
    /// <para>ONE row per manifest field, so this is a sweep and not a sample.
    /// Each is rejected AT LOAD, naming the offender AND the spelling the radio
    /// actually stores — an operator who hand-edited a file is told what to
    /// write, not merely that they were wrong.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(NoncanonicalSpellings))]
    public void ANoncanonicalSpellingOfAValidValue_IsRejectedAtLoad_NamingTheStoredForm(
        string key, string variant, string canonical)
    {
        var file = Complete();
        file.Settings.Add(new CloneSetting { Key = key, Value = variant });

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains(variant, ex.Message, StringComparison.Ordinal);
        Assert.Contains(canonical, ex.Message, StringComparison.Ordinal);

        // …and the SAME value, canonically spelled, still loads. Without this
        // half the theory would pass on a door that refused the value itself.
        var good = Complete();
        good.Settings.Add(new CloneSetting { Key = key, Value = canonical });
        Assert.Single(CloneFile.Load(good.Save()).Settings, s => s.Value == canonical);
    }

    /// <summary>(key, the variant that must be REFUSED, the canonical form).
    /// All 29 rows. The variants are the shapes a hand-edited file really
    /// grows: case aliases, the NUMERIC enum aliases (where "0" means On and
    /// "1" means Off — the nastiest of them, because it is silently a
    /// different value), signed and zero-padded integers, stray whitespace,
    /// and suffix junk.</summary>
    public static TheoryData<string, string, string> NoncanonicalSpellings() => new()
    {
        // ---- enum-named rows: case aliases and the NUMERIC alias ----------
        { "Rwas", "disabled", "Disabled" },
        { "DigitalVoice", "OFF", "Off" },
        { "DigitalSquelch", "1", "Off" },          // the numeric alias: "1" IS Off
        { "PowerLevel", "HIGH", "High" },
        { "UnkeyMask", "0", "Enabled" },           // the numeric alias again: "0" IS Enabled
        { "FrequencyStep", "onekhz", "OneKHz" },
        { "Beep", "0", "On" },                     // "0" IS On
        { "FmTone", "on", "On" },
        { "AnalogSquelch", "OFF", "Off" },
        { "AleAllCall", "ON", "On" },
        { "AleAnyCall", "on", "On" },
        { "AleAmdDisplay", "0", "On" },
        { "AleKeyToCall", "OFF", "Off" },
        { "AleListenBeforeTx", "off", "Off" },
        { "AleRadioSilence", "1", "Off" },

        // ---- payload rows: the mirror's spelling is the canonical one -----
        { "Antenna", "auto", "AUTO" },             // the radio PRINTS lower case…
        { "FmSquelchType", "tone", "TONE" },       // …and the parser uppercases it
        { "PrePostScanRate", "Slow", "SLOW" },
        { "PrePostFilter", "ENABLED", "ENABLE" },  // the REPORT's word, not the setter's
        { "PrePostRxAntenna", "OFF", "DISABLE" },   // the SETTER's word, not the report's
        { "FmDeviation", " 8.0 ", "8.0" },         // whitespace is not trimmed away

        // ---- numeric rows: sign and padding ------------------------------
        { "BfoOffset", "9999", "+9999" },          // the sign is part of the spelling
        { "CwOffset", "+0000", "0000" },           // THE REPRODUCED CASE
        { "RfGain", "+001", "1" },
        { "Contrast", "03", "3" },
        { "AleMaxScanChannels", "+8", "8" },
        { "AleLinkTimeout", "00", "0" },
        { "AleTuneTime", "01", "1" },

        // ---- the raw-mirror row ------------------------------------------
        { "ActiveModem", "off", "OFF" },
    };

    /// <summary>
    /// <c>ActiveModem</c>'s DOCUMENTED-FORM-ONLY asymmetry, pinned because it
    /// looks like an oversight and is not: the engage builder would accept
    /// <c>T39</c> — it takes a name as well as a number — but the file's value
    /// is a MIRROR STRING, and the verify compares it against what the radio
    /// reports. A name-only file would write correctly and read back as
    /// <c>1 T39</c>: a diff that is not a difference.
    ///
    /// <para>THE BARE SELECTOR is refused for the same reason and on the same
    /// evidence: the SSB <c>SH</c> block's short form is <c>MODEM 1 T39</c> and
    /// the off echo is <c>MODEM OFF</c>. The mirror ALWAYS carries the name
    /// when a preset is engaged, so <c>"1"</c> is a shape the radio never
    /// reports.</para>
    /// </summary>
    [Theory]
    [InlineData("T39")]              // the builder would take it; the mirror never says it
    [InlineData("1  T39")]           // the mirror separates with ONE space
    [InlineData("7 T39")]            // …and presets stop at six
    [InlineData("1")]                // BARE SELECTOR — never a mirror shape
    [InlineData("0006 SER")]         // the radio prints the number plainly
    public void AnActiveModemShapeTheRadioWouldNeverReport_IsRejected(string variant)
    {
        var file = WithPreset(1, "T39 ASYNC DATA");
        file.Settings.Add(new CloneSetting { Key = "ActiveModem", Value = variant });

        Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
    }

    /// <summary>
    /// <b>THE ENGAGED MODEM'S NAME IS A PROJECTION OF THE FILE'S OWN PRESET
    /// DOMAIN</b> (audit round 3 verification, BLOCKER — the one survivor of
    /// the canonical-spelling round).
    ///
    /// <para>The row's parser can only prove the SHAPE. Which name is RIGHT is
    /// a question about another domain: the campaign writes preset <c>n</c>
    /// from the file's own record, and the radio then reports the preset by the
    /// name it was just given. So a wrong name — including a perfectly
    /// valid-looking one — writes successfully and fails the byte-exact verify
    /// afterwards, on a wiped radio.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EngagedModemMismatches))]
    public void AnEngagedModemNameTheFilesOwnPresetContradicts_IsRejected_NamingWhatTheRadioWillReport(
        string engaged, string needle)
    {
        var file = WithPreset(1, "T39 ASYNC DATA   BAUD 2400  TYPE 39tone  INTER long");
        file.Settings.Add(new CloneSetting { Key = "ActiveModem", Value = engaged });

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));

        Assert.Contains(engaged, ex.Message, StringComparison.Ordinal);   // the offender
        Assert.Contains(needle, ex.Message, StringComparison.Ordinal);    // …and what to expect
    }

    public static TheoryData<string, string> EngagedModemMismatches() => new()
    {
        // CASE: the radio reports the name as the record spells it.
        { "1 t39", "the radio will report '1 T39'" },
        // A NAME THAT IS SIMPLY WRONG.
        { "1 BAD", "the radio will report '1 T39'" },
        // …AND ONE THAT LOOKS ENTIRELY VALID — a real vocabulary token, for
        // the wrong preset. This is the case that makes the rule worth having:
        // no amount of checking the VALUE could catch it.
        { "1 SE", "the radio will report '1 T39'" },
        // A preset the file does not hold at all: there is nothing to engage.
        { "3 T39", "holds no modem preset 3" },
    };

    [Theory]
    [InlineData("OFF")]              // the modem-off echo
    [InlineData("1 T39")]            // …and the engaged form, matching slot 1's record
    public void AnActiveModemValueTheRadioDOESReport_Loads(string value)
    {
        var file = WithPreset(1, "T39 ASYNC DATA");
        file.Settings.Add(new CloneSetting { Key = "ActiveModem", Value = value });

        Assert.Single(CloneFile.Load(file.Save()).Settings, s => s.Value == value);
    }

    [Fact]
    public void AnEngagedModemInAFileWhosePresetsWereNEVERREAD_IsNotJudged()
    {
        // The gate, and the criterion behind it: a file whose preset domain was
        // never read cannot be written at all — the ordinary preflight refuses
        // it — so faulting it HERE would name the wrong problem.
        var file = Complete();
        FillChannels(file);            // …so the presets are the ONLY gap (F6)
        file.ModemState = CloneDomainState.Unread;
        file.Settings.Add(new CloneSetting { Key = "ActiveModem", Value = "1 T39" });

        var loaded = CloneFile.Load(file.Save());

        Assert.Equal(["modem presets"], loaded.IncompleteDomains);
    }

    /// <summary>A complete file whose modem domain holds ONE preset, so the
    /// engaged-modem rule has a record to derive a name from.</summary>
    private static CloneFile WithPreset(int number, string fields)
    {
        var file = Complete();
        file.ModemPresets.Add(new CloneModemPreset { Number = number, Fields = fields, Enabled = true });
        return file;
    }

    /// <summary>
    /// THE ANTI-DRIFT PIN, and the reason the bounds live in <see cref="Wire"/>
    /// rather than being written out twice.
    ///
    /// <para>The cases above hard-code their numbers, which is right for a
    /// regression theory and useless against DRIFT: if Core widened `RF` to
    /// 0-120 and the clone's door did not follow, every one of them would
    /// still pass while a legal file was refused at the door. This walks the
    /// bounds THEMSELVES — the same constants the builder validates against —
    /// and requires the door to sit exactly on them.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(BoundedSettings))]
    public void TheDoorSitsExactlyOnTheBuildersOwnBound(
        string key, int min, int max, string minCanonical, string maxCanonical)
    {
        static void Load(string key, string value)
        {
            var file = Complete();
            file.Settings.Add(new CloneSetting { Key = key, Value = value });
            CloneFile.Load(file.Save());
        }

        Load(key, minCanonical);                                     // the floor is IN
        Load(key, maxCanonical);                                     // …so is the ceiling

        // One step outside, in ANY spelling: out of range is refused before
        // the spelling is ever considered.
        Assert.Throws<CloneFileFormatException>(
            () => Load(key, (min - 1).ToString(CultureInfo.InvariantCulture)));
        Assert.Throws<CloneFileFormatException>(
            () => Load(key, (max + 1).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The bounds come from <see cref="Wire"/> — that is the drift
    /// this guards. The canonical EDGE SPELLINGS are written out beside them,
    /// and what proves those right is the all-rows round-trip pin
    /// (<c>CloneServiceTests.EverySettingTheDoorAdmits_…</c>), which asks the
    /// radio rather than the author.</summary>
    public static TheoryData<string, int, int, string, string> BoundedSettings() => new()
    {
        { "RfGain", Wire.RfGainMin, Wire.RfGainMax, "0", "100" },
        { "BfoOffset", Wire.BfoOffsetMinHz, Wire.BfoOffsetMaxHz, "-9999", "+9999" },
        { "Contrast", Wire.ZeroToEightMin, Wire.ZeroToEightMax, "0", "8" },
        { "AleMaxScanChannels", Wire.MaxScanChannelsMin, Wire.MaxScanChannelsMax, "0", "100" },
        { "AleLinkTimeout", Wire.LinkTimeoutMinMinutes, Wire.LinkTimeoutMaxMinutes, "0", "60" },
        { "AleTuneTime", Wire.TuneTimeMinSeconds, Wire.TuneTimeMaxSeconds, "1", "60" },
    };

    [Fact]
    public void AGoodSettingValue_StillLoads_AndTheDoorRunsTheWriteLegsOwnParser()
    {
        // ANTI-VACUITY for the theory above — and the property that makes it
        // worth anything: the check at the door IS the write leg's parser, so
        // there is no second copy of the rules to drift.
        var file = Complete();
        file.Settings.Add(new CloneSetting { Key = "DigitalVoice", Value = "On" });
        file.Settings.Add(new CloneSetting { Key = "RfGain", Value = "100" });
        file.Settings.Add(new CloneSetting { Key = "ActiveModem", Value = "OFF" });

        Assert.Equal(3, CloneFile.Load(file.Save()).Settings.Count);

        // The same delegate, reached directly: it accepts what it accepts and
        // throws CloneValueException — the write leg's exception — otherwise.
        CloneSettingsManifest.CheckStoredValue("DigitalVoice", "Off");
        Assert.Throws<CloneValueException>(
            () => CloneSettingsManifest.CheckStoredValue("DigitalVoice", "99"));
    }

    // ---- CROSS-FIELD RELATIONSHIPS (P2 audit round 1, BLOCKER) --------------

    /// <summary>
    /// A marker that claims a domain was READ, over a payload that cannot have
    /// come from a completed read. The operating snapshot is the one place this
    /// matters, because a null there makes the finals leg SILENTLY OMIT a write
    /// rather than write fewer rows — and the omission is only discovered by a
    /// verify that runs after the wipe.
    /// </summary>
    [Theory]
    [MemberData(nameof(IncoherentOperatingSnapshots))]
    public void AnOperatingStateMarkedReadWithNoSnapshot_IsRejected_NamingWhatIsMissing(
        Action<CloneFile> corrupt, string needle)
    {
        var file = Complete();
        corrupt(file);

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("marked read", ex.Message, StringComparison.Ordinal);
        Assert.Contains(needle, ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<Action<CloneFile>, string> IncoherentOperatingSnapshots() => new()
    {
        { f => f.OperatingMode = null, "operating mode" },       // the auditor's probe
        { f => f.OperatingMode = "", "operating mode" },
        { f => f.OperatingChannel = null, "operating channel" },
        { f => f.OperatingHopNet = null, "operating HOP net" },
    };

    [Fact]
    public void AnOperatingSnapshotThatWasNEVERRead_IsLegal_BecauseItClaimsNothing()
    {
        // The other half, and the reason the rule is CROSS-FIELD rather than a
        // per-field "must not be null": a domain that says it was not read is
        // honest about carrying nothing. It is refused by the ORDINARY
        // preflight, which names it like any other incomplete domain.
        var file = Complete();
        FillChannels(file);            // …so the operating state is the ONLY gap (F6)
        file.OperatingState = CloneDomainState.Unread;
        file.OperatingMode = null;
        file.OperatingChannel = null;
        file.OperatingHopNet = null;

        var loaded = CloneFile.Load(file.Save());

        Assert.Equal(["operating state"], loaded.IncompleteDomains);
    }

    [Fact]
    public void TheDomainsWithNOCrossFieldRule_StillLoadWhenTheirPayloadIsEmpty()
    {
        // The recorded half of the sweep: an EMPTY payload under a READ marker
        // is what a post-wipe read legitimately finds for these domains, so a
        // rule here would reject honest files. They write fewer rows and the
        // EXACT verify names every difference — which is self-reporting, unlike
        // a skipped leg.
        //
        // SSB CHANNELS LEFT THIS LIST IN ROUND 17 F6, and the radio is why: a
        // post-wipe `DI 0 99` finds 100 DEFAULT ROWS, never silence, so an empty
        // channel payload is not an honest file — it is the pre-fix truncation.
        // Its own rule is pinned below.
        var file = Complete();
        FillChannels(file);
        file.Individuals.Clear();
        file.Nets.Clear();
        file.ChannelGroups.Clear();
        file.Schedules.Clear();
        file.HopNets.Clear();
        file.ExcludeBands.Clear();
        file.ModemPresets.Clear();
        file.Messages.Clear();
        file.Settings.Clear();

        var loaded = CloneFile.Load(file.Save());

        Assert.Empty(loaded.IncompleteDomains);
        Assert.Equal(100, loaded.Channels.Count);
    }

    [Fact]
    public void AnUndefinedNumericOperatingMode_IsRejected_TheR3Blocker()
    {
        // plan/plan-clone-file-validation.md §3, the deferred BLOCKER: "99"
        // parsed, loaded, passed the transformed-graph revalidation, and threw
        // at the finals leg — which runs AFTER the wipe.
        var file = Complete();
        file.OperatingMode = "99";

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("not an operating mode", ex.Message, StringComparison.Ordinal);

        // Anti-vacuity: a REAL mode name still loads, so the rule rejects the
        // undefined value rather than every value.
        file.OperatingMode = "Hop";
        Assert.Equal("Hop", CloneFile.Load(file.Save()).OperatingMode);
    }

    // ---- Load rejections (§9A file hygiene) ---------------------------------

    [Fact]
    public void AnUnknownVersion_IsRejected_NamingIt()
    {
        var json = Complete().Save().Replace("falconclone/1", "falconclone/9", StringComparison.Ordinal);
        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(json));
        Assert.Contains("falconclone/9", ex.Message, StringComparison.Ordinal);
        Assert.Contains("falconclone/1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TextThatIsNotAFileAtAll_IsRejected()
    {
        Assert.Throws<CloneFileFormatException>(() => CloneFile.Load("not json"));
        Assert.Throws<CloneFileFormatException>(() => CloneFile.Load("null"));
    }

    [Fact]
    public void ACrossKindDuplicateName_IsRejected_NamingTheOffender()
    {
        // Radio names are GLOBAL across selfs, individuals and nets, so a file
        // holding one twice could never be replayed.
        var file = Complete();
        file.Individuals.Add(new CloneAddress { Name = "cam", Group = 1, AssociatedSelf = "CAM" });

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("cam", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unique", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedRows))]
    public void AMalformedRow_IsRejected_NamingWhatIsWrong(Action<CloneFile> corrupt, string needle)
    {
        var file = Complete();
        corrupt(file);
        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains(needle, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Action<CloneFile>, string> MalformedRows() => new()
    {
        { f => f.Selfs.Add(new CloneAddress { Name = "", Group = 0 }), "empty" },
        { f => f.Selfs.Add(new CloneAddress { Name = new string('A', 16), Group = 0 }), "15 characters" },
        { f => f.Selfs.Add(new CloneAddress { Name = "XX", Group = 11 }), "0-9" },
        { f => f.Selfs.Add(new CloneAddress { Name = "XX", Group = 0, AssociatedSelf = "CAM" }), "selfs never have" },
        { f => f.Nets[0].Members.Add(""), "member name" },
        { f => f.ChannelGroups.Add(new CloneChannelGroup { Group = 3, Channels = [100] }), "0-99" },
        { f => f.Channels.Add(new CloneChannel { Number = 100 }), "0-99" },
        { f => f.HopNets.Add(new CloneHopNet { Number = 10 }), "0-9" },
        { f => f.ExcludeBands.Add(new CloneExcludeBand { Band = 10, LowKHz = "1", HighKHz = "2" }), "0-9" },
        // F9: the load bound is the UNION of the two prompt-scoped bands (0-6
        // at `SSB>`/`ALE>`, 7-9 at `HOP>`), so 7 is a legal row now and 10 is
        // the first that is not.
        { f => f.ModemPresets.Add(new CloneModemPreset { Number = 10, Fields = "x" }), "0-9" },
        { f => f.Messages.Add(new CloneTxMessage { Slot = 0, Text = new string('A', 91) }), "1-90" },
        { f => f.Schedules.Add(new CloneSchedule { Kind = "PING", Address = "CAM" }), "neither EXCHANGE nor SOUND" },
        { f => f.Settings.Add(new CloneSetting { Key = "NotAManifestKey", Value = "1" }), "manifest setting" },
    };

    [Theory]
    [MemberData(nameof(DuplicateKeys))]
    public void ADuplicateKeyedRow_IsRejected(Action<CloneFile> corrupt, string needle)
    {
        var file = Complete();
        corrupt(file);
        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains(needle, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Action<CloneFile>, string> DuplicateKeys() => new()
    {
        {
            f => { f.Channels.Add(new CloneChannel { Number = 1 }); f.Channels.Add(new CloneChannel { Number = 1 }); },
            "duplicate SSB channel"
        },
        {
            f => { f.HopNets.Add(new CloneHopNet { Number = 2 }); f.HopNets.Add(new CloneHopNet { Number = 2 }); },
            "duplicate HOP net"
        },
        {
            f =>
            {
                f.Messages.Add(new CloneTxMessage { Slot = 3, Text = "A" });
                f.Messages.Add(new CloneTxMessage { Slot = 3, Text = "B" });
            },
            "duplicate message slot"
        },
        {
            f =>
            {
                f.Settings.Add(new CloneSetting { Key = "PowerLevel", Value = "High" });
                f.Settings.Add(new CloneSetting { Key = "PowerLevel", Value = "Low" });
            },
            "duplicate setting"
        },
    };

    // ---- REFERENTIAL INTEGRITY (P6 audit round 2, BLOCKER) -------------------

    /// <summary>
    /// The auditor's exact probe: self CAM, net NET2 associated to GHOST. It
    /// used to LOAD — every row's own shape was fine and no name was
    /// duplicated — pass the transformed-graph revalidation, and be refused BY
    /// THE RADIO at <c>NETAD</c> in leg 8, which runs AFTER the leg-6
    /// <c>ERASE</c>. The operator's radio would already have been wiped and
    /// half rewritten.
    /// </summary>
    [Fact]
    public void ANetAssociatedToASelfTheFileDoesNotHold_IsRejected_NamingTheGhost()
    {
        var file = Complete();
        file.Nets[0].AssociatedSelf = "GHOST";

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("GHOST", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NET2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not hold as a self", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndividualAssociatedToASelfTheFileDoesNotHold_IsRejected_NamingIt()
    {
        var file = Complete();
        file.Individuals[0].AssociatedSelf = "GHOST";

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("GHOST", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BOB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssociationToAnINDIVIDUAL_IsAlsoDangling_BecauseTheRadioNeedsASELF()
    {
        // Not merely "is the name present": NETAD takes an existing SELF, so a
        // net hung off another individual is just as unwritable as one hung off
        // nothing.
        var file = Complete();
        file.Nets[0].AssociatedSelf = "BOB";      // an individual, not a self

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("BOB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not hold as a self", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndividualWithNoAssociatedSelfAtAll_IsRejected()
    {
        // INDAD cannot be sent without one. (No capture has ever shown a blank
        // individual: the primary-deletion case DELETES individuals rather than
        // blanking them.)
        var file = Complete();
        file.Individuals[0].AssociatedSelf = null;

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("BOB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("without one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankNetAssociation_STAYS_LEGAL_BecauseItIsWhatTheRadioProduces()
    {
        // ANTI-VACUITY for the whole family, and the line that keeps the rule
        // honest: a net with NO associated self is the documented
        // primary-deletion artifact. It must LOAD — the swap is what drops it,
        // loudly, and rejecting it here would make a real radio's file
        // unloadable.
        var file = Complete();
        file.Nets[0].AssociatedSelf = null;

        var loaded = CloneFile.Load(file.Save());

        Assert.Null(Assert.Single(loaded.Nets).AssociatedSelf);
        var dropped = CloneSwap.Apply(loaded, CloneSwapTests.Rows());
        Assert.Empty(dropped.File.Nets);
        Assert.Contains(dropped.Drops, d => d.Contains("NET2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("9:99")]
    [InlineData("24:00")]
    [InlineData("00:60")]
    [InlineData("0100")]
    [InlineData("")]
    public void AScheduleTimeCoreWouldThrowOn_IsRejectedAtTheDoor(string interval)
    {
        // EXCH/SOU STA runs in leg 8 — after the erase — and Core's hh:mm
        // validator THROWS. A throw arriving there would abandon a half
        // rewritten radio, so the shape is settled here instead.
        var file = Complete();
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "CAM", Interval = interval, Start = "13:02" });

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains("00:00 and 23:59", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CAM", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// P6 AUDIT ROUND 3, the user-facing fix
    /// (plan/plan-clone-file-validation.md §4): a schedule time that is JSON
    /// <c>null</c> used to CRASH the app — the property's non-null annotation
    /// is a promise the deserializer does not keep, and the length check
    /// dereferenced it. A user opening a corrupted or hand-damaged file is
    /// entitled to the contracted rejection, not a NullReferenceException.
    /// </summary>
    [Theory]
    [InlineData("\"Interval\": \"03:00\"", "interval")]
    [InlineData("\"Start\": \"13:02\"", "start time")]
    public void AScheduleTimeThatIsJsonNull_IsRejectedCleanly_NotACrash(string field, string what)
    {
        var file = Complete();
        file.Schedules.Add(new CloneSchedule
        { Kind = "SOUND", Address = "CAM", Interval = "03:00", Start = "13:02" });
        // Hand-damage the FILE, not the model — this is the shape a corrupted
        // file on disk really has, and the only route that reaches the bug.
        var json = file.Save().Replace(field, field.Split(':')[0] + ": null", StringComparison.Ordinal);
        Assert.Contains(": null", json, StringComparison.Ordinal);   // the damage really landed

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(json));

        Assert.Contains("CAM", ex.Message, StringComparison.Ordinal);        // the offender, named
        Assert.Contains(what, ex.Message, StringComparison.Ordinal);         // …and which field
        Assert.Contains("(missing)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("00:00 and 23:59", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadOperatingSnapshots))]
    public void AnOperatingSnapshotValueTheFinalsLegWouldThrowOn_IsRejected(
        Action<CloneFile> corrupt, string needle)
    {
        // Leg 10 is also after the erase, and these values reach CH / NET / the
        // mode switch directly.
        var file = Complete();
        corrupt(file);
        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Contains(needle, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Action<CloneFile>, string> BadOperatingSnapshots() => new()
    {
        { f => f.OperatingChannel = 100, "operating channel 100" },
        { f => f.OperatingChannel = -1, "operating channel -1" },
        { f => f.OperatingHopNet = 10, "operating HOP net 10" },
        { f => f.OperatingMode = "Sideband", "not an operating mode" },
    };

    [Fact]
    public void AGoodFile_LoadsCleanly()
    {
        // Anti-vacuity for every rejection above: the UNCORRUPTED baseline
        // really does load, so the theories are testing the corruption and not
        // a permanently broken fixture.
        var file = CloneFile.Load(Complete().Save());
        Assert.Single(file.Selfs);
    }

    [Fact]
    public void Normalize_IsOrdinalUppercase_TheRadiosOwnLookupRule()
    {
        Assert.Equal("CAM", CloneFile.Normalize(" cam "));
        Assert.Equal("CAM", CloneFile.Normalize("CAM"));
    }

    // ---- ROUND 17 F6: the SHORT-DUMP load rule ------------------------------

    /// <summary>
    /// THE HAZARD THIS EXISTS FOR: a file written before the dump-completion
    /// fix claims <c>Read</c> over 28 channels, because the campaign judged the
    /// dump at the moment its sentinel answered — which the radio does MID-DUMP
    /// for a heavy answer (r15-p1 transcript, the sentinel between `CH 27` and
    /// `CH 28`). The 8-22 `falconclone.json` on the owner's bench is exactly
    /// that file.
    /// <para>It still LOADS — nothing is rejected — but the marker is corrected
    /// to what the radio actually answered, and the file says so out loud.</para>
    /// </summary>
    [Fact]
    public void AFileClaimingREADOverAShortChannelDump_IsDowngraded_AndSaysWhy()
    {
        var file = Complete();
        for (int n = 0; n < 28; n++) file.Channels.Add(new CloneChannel { Number = n });

        var loaded = CloneFile.Load(file.Save());

        Assert.Equal(CloneDomainState.Faulted, loaded.ChannelState);
        Assert.Equal(
            "SSB channels: this file predates the dump-completion fix (only 28 of 100 slots) "
                + "— re-read the radio.",
            Assert.Single(loaded.LoadNotices));
        // The CONSEQUENCE, and why the downgrade is worth having: the existing
        // write preflight now refuses the file by name. Nothing new refuses it.
        Assert.Contains("SSB channels", loaded.IncompleteDomains);
        // …and the rows it does carry are still there to look at.
        Assert.Equal(28, loaded.Channels.Count);
    }

    /// <summary>
    /// THE ANTI-VACUITY TWIN: the whole 100-slot inventory — which is what
    /// `DI 0 99` always answers — passes through untouched and says nothing.
    /// A rule that fired on every file would be a rule nobody could read.
    /// </summary>
    [Fact]
    public void AFileWithTheWhole100SlotInventory_IsNotDowngraded_AndCarriesNoNotice()
    {
        var file = Complete();
        FillChannels(file);

        var loaded = CloneFile.Load(file.Save());

        Assert.Equal(CloneDomainState.Read, loaded.ChannelState);
        Assert.Empty(loaded.LoadNotices);
        Assert.Empty(loaded.IncompleteDomains);
        Assert.Equal(100, loaded.Channels.Count);
    }

    /// <summary>An EMPTY channel list under a <c>Read</c> marker is the same
    /// defect at its limit — a post-wipe read finds 100 default rows, never
    /// none — so it downgrades too, and names the zero.</summary>
    [Fact]
    public void AFileClaimingREADOverNOChannelsAtAll_IsDowngradedToo()
    {
        var loaded = CloneFile.Load(Complete().Save());

        Assert.Equal(CloneDomainState.Faulted, loaded.ChannelState);
        Assert.Contains("only 0 of 100 slots", Assert.Single(loaded.LoadNotices), StringComparison.Ordinal);
    }

    /// <summary>A domain that never claimed to be read is left alone: the rule
    /// corrects a FALSE <c>Read</c>, it does not re-judge an honest
    /// <c>Unread</c> or a fault someone else already found. Both are refused by
    /// the ordinary preflight, which is where they belong.</summary>
    [Theory]
    [InlineData(CloneDomainState.Unread)]
    [InlineData(CloneDomainState.Faulted)]
    public void AChannelDomainThatNEVERClaimedRead_IsLeftExactlyAsItIs(CloneDomainState state)
    {
        var file = Complete();
        file.ChannelState = state;

        var loaded = CloneFile.Load(file.Save());

        Assert.Equal(state, loaded.ChannelState);
        Assert.Empty(loaded.LoadNotices);
        Assert.Contains("SSB channels", loaded.IncompleteDomains);
    }

    // ---- ROUND 17 D4/D6: the DefaultChannelsElided discriminator ------------
    // The rules above are the LEGACY half and stay byte-identical: every pin in
    // this class between "ROUND 17 F6" and here runs UNMODIFIED, because a file
    // without the marker is a file the 100-row rule still owns. What follows is
    // the other half — a file that says it dropped the factory-default rows.

    /// <summary>
    /// A SPARSE file that CARRIES THE MARKER loads clean: it is short ON
    /// PURPOSE, so the round-17 F6 downgrade must not touch it. Without the
    /// discriminator this file would be Faulted and refused by the write
    /// preflight — which is the whole reason D6 is a marker addition and not a
    /// silent behaviour change.
    /// </summary>
    [Fact]
    public void AnELIDEDFile_IsNotDowngraded_AndCarriesNoNotice()
    {
        var file = Complete();
        file.DefaultChannelsElided = true;
        file.Channels.Add(new CloneChannel
        {
            Number = 1, RxFrequency = "14313500", TxFrequency = "14313500",
            Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO",
        });

        var loaded = CloneFile.Load(file.Save());

        Assert.True(loaded.DefaultChannelsElided);          // …and the marker round-trips
        Assert.Equal(CloneDomainState.Read, loaded.ChannelState);
        Assert.Empty(loaded.LoadNotices);
        Assert.Empty(loaded.IncompleteDomains);
        Assert.Equal([1], loaded.Channels.Select(c => c.Number));
    }

    /// <summary>
    /// THE DISCRIMINATION, stated as a pair: the SAME one-channel file WITHOUT
    /// the marker is the round-17 F6 short dump, and is downgraded exactly as
    /// it always was. A rule that fired the same way either way would not be a
    /// discriminator at all.
    /// </summary>
    [Fact]
    public void TheSameShortFile_WithoutTheMarker_IsStillDowngraded()
    {
        var file = Complete();
        file.Channels.Add(new CloneChannel
        {
            Number = 1, RxFrequency = "14313500", TxFrequency = "14313500",
            Mode = "USB", Agc = "SL", Bandwidth = "2.7", RxOnly = "NO",
        });

        var loaded = CloneFile.Load(file.Save());

        Assert.False(loaded.DefaultChannelsElided);
        Assert.Equal(CloneDomainState.Faulted, loaded.ChannelState);
        Assert.Contains("only 1 of 100 slots", Assert.Single(loaded.LoadNotices), StringComparison.Ordinal);
    }

    /// <summary>
    /// An ELIDED file carrying a FACTORY-DEFAULT row describes itself wrongly,
    /// and the write leg would send a slot the wipe already set. It is REJECTED
    /// at the door, naming the offending slot.
    /// </summary>
    [Fact]
    public void AnELIDEDFile_HoldingADefaultRow_IsRejected_NamingTheSlot()
    {
        var file = Complete();
        file.DefaultChannelsElided = true;
        var d = Wire.DefaultChannel;
        file.Channels.Add(new CloneChannel
        {
            Number = 42, RxFrequency = d.RxFrequency, TxFrequency = d.TxFrequency,
            Mode = d.Mode, Agc = d.Agc, Bandwidth = d.Bandwidth, RxOnly = d.RxOnly,
        });

        var ex = Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save()));
        Assert.Equal(
            "Malformed row: SSB channel 42 holds the factory default values, and this file records "
                + "that default channels were not stored.",
            ex.Message);
    }

    /// <summary>The same rule, CASE-INSENSITIVELY: a hand-edited <c>usb</c> is
    /// the same slot the radio would print as <c>USB</c>, so it must not slip
    /// past the door and become ~90 pointless write sequences.</summary>
    [Fact]
    public void AnELIDEDFile_HoldingADefaultRowInAnotherCase_IsRejectedToo()
    {
        var file = Complete();
        file.DefaultChannelsElided = true;
        file.Channels.Add(new CloneChannel
        {
            Number = 3, RxFrequency = "01600000", TxFrequency = "01600000",
            Mode = "usb", Agc = "sl", Bandwidth = "2.7", RxOnly = "no",
        });

        Assert.Contains("SSB channel 3 holds the factory default values",
            Assert.Throws<CloneFileFormatException>(() => CloneFile.Load(file.Save())).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NO-TRIM HALF of the same predicate, and the OTHER side of the
    /// case-insensitivity above. Whitespace inside a stored value is a
    /// DIFFERENT VALUE — the rest of this file's rules treat it that way — so a
    /// row that differs from the factory default only by surrounding
    /// whitespace is not a default row, and an elided file may hold it.
    ///
    /// <para>Audit round 1 found this uncovered: trimming both operands of the
    /// comparison stayed green across the whole suite. The two halves of the
    /// rule are deliberately different — CASE is the radio's own (the dump
    /// prints upper), WHITESPACE is the file's, and a value the radio never
    /// printed must not be silently read as one it did.</para>
    /// </summary>
    [Theory]
    [InlineData("RxFrequency")]
    [InlineData("Mode")]
    [InlineData("Agc")]
    [InlineData("Bandwidth")]
    [InlineData("RxOnly")]
    public void AnELIDEDFile_HoldingARowThatDiffersOnlyByWhitespace_Loads(string field)
    {
        var d = Wire.DefaultChannel;
        var row = new CloneChannel
        {
            Number = 3, RxFrequency = d.RxFrequency, TxFrequency = d.TxFrequency,
            Mode = d.Mode, Agc = d.Agc, Bandwidth = d.Bandwidth, RxOnly = d.RxOnly,
        };
        switch (field)
        {
            case "RxFrequency": row.RxFrequency = d.RxFrequency + " "; break;
            case "Mode": row.Mode = " " + d.Mode; break;
            case "Agc": row.Agc = d.Agc + " "; break;
            case "Bandwidth": row.Bandwidth = " " + d.Bandwidth; break;
            default: row.RxOnly = d.RxOnly + " "; break;
        }
        Assert.False(row.IsFactoryDefault());

        var file = Complete();
        file.DefaultChannelsElided = true;
        file.Channels.Add(row);

        // It LOADS — no rejection, and the row is kept verbatim.
        Assert.Equal([3], CloneFile.Load(file.Save()).Channels.Select(c => c.Number));
    }

    /// <summary>ANTI-VACUITY for both rejections: an elided file whose rows
    /// differ from the default in ONE field each loads perfectly well, so the
    /// rule is about the WHOLE tuple and not about any single column.</summary>
    [Theory]
    [InlineData("RxFrequency")]
    [InlineData("TxFrequency")]
    [InlineData("Mode")]
    [InlineData("Agc")]
    [InlineData("Bandwidth")]
    [InlineData("RxOnly")]
    public void AnELIDEDFile_HoldingARowThatDiffersInOneField_Loads(string field)
    {
        var d = Wire.DefaultChannel;
        var row = new CloneChannel
        {
            Number = 3, RxFrequency = d.RxFrequency, TxFrequency = d.TxFrequency,
            Mode = d.Mode, Agc = d.Agc, Bandwidth = d.Bandwidth, RxOnly = d.RxOnly,
        };
        switch (field)
        {
            case "RxFrequency": row.RxFrequency = "14313500"; break;
            case "TxFrequency": row.TxFrequency = "14313500"; break;
            case "Mode": row.Mode = "LSB"; break;
            case "Agc": row.Agc = "ME"; break;
            case "Bandwidth": row.Bandwidth = "3.0"; break;
            default: row.RxOnly = "YES"; break;
        }
        Assert.False(row.IsFactoryDefault());

        var file = Complete();
        file.DefaultChannelsElided = true;
        file.Channels.Add(row);

        Assert.Equal([3], CloneFile.Load(file.Save()).Channels.Select(c => c.Number));
    }

    /// <summary>The marker is a real serialized field — ABSENT means false, so
    /// every file written before this round means "the 100-row rule applies",
    /// and no version bump was needed to say it.</summary>
    [Fact]
    public void AFileWrittenWithoutTheMarker_ReadsAsNotElided()
    {
        var json = Complete().Save();
        Assert.Contains("\"DefaultChannelsElided\": false", json, StringComparison.Ordinal);

        var stripped = json.Replace("  \"DefaultChannelsElided\": false,\r\n", "", StringComparison.Ordinal)
            .Replace("  \"DefaultChannelsElided\": false,\n", "", StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultChannelsElided", stripped, StringComparison.Ordinal);

        Assert.False(CloneFile.Load(stripped).DefaultChannelsElided);
        Assert.Equal(CloneFile.CurrentVersion, CloneFile.Load(stripped).Version);
    }

    /// <summary>The notice is a fact about THIS LOAD, not a field of the format:
    /// it is never serialized, so it can neither be forged into a file nor
    /// outlive the condition that produced it.</summary>
    [Fact]
    public void TheLoadNotice_IsNeverWrittenIntoTheFile()
    {
        var file = Complete();
        for (int n = 0; n < 28; n++) file.Channels.Add(new CloneChannel { Number = n });
        var loaded = CloneFile.Load(file.Save());
        Assert.Single(loaded.LoadNotices);

        var json = loaded.Save();

        Assert.DoesNotContain("LoadNotices", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("predates the dump-completion fix", json, StringComparison.Ordinal);
    }
}
