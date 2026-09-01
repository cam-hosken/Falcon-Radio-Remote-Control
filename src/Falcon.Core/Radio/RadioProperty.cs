namespace Falcon.Core.Radio;

/// <summary>Identifies which piece of radio state changed in a StateChanged event.</summary>
public enum RadioProperty
{
    ConnectionOpen,
    ConnectionState,
    ModeChangePending,
    OperatingMode,
    PowerLevel,
    PowerCutback,
    Keyline,
    Tuning,
    TuneComplete,
    TuneMarginal,
    TuneFail,
    RxFrequency,
    TxFrequency,
    OperatingChannel,
    ModulationMode,
    Bandwidth,
    AgcSpeed,
    ChannelRxOnly,
    FrequencyStep,
    AnalogSquelch,
    ActiveModem,
    BatteryStatus,
    PortRemoteEcho,
    PortConfig,
    ChannelList,
    RadioTimeOfDay,

    // Phase R mirror additions (plan-gui-rejigger.md round 4 — read/write
    // parity): every SETTING whose answer shape is captured gets a mirror.
    DigitalVoice,
    DigitalSquelch,
    SquelchLevel,
    FmSquelch,
    FmSquelchType,
    FmTone,
    FmDeviation,
    BfoOffset,
    CwOffset,
    Compression,
    Antenna,
    Retransmit,
    Rwas,
    UnkeyMask,
    Avs,
    Encryption,
    EncryptionAvailability,
    CurrentEncryptionKey,
    RfGain,
    Contrast,
    Beep,
    PrePostFilter,
    PrePostRxAntenna,
    PrePostScanRate,

    // UI-tweaks round-3 V7 mirror additions (plan-ui-tweaks-round3.md): the
    // three settings whose ANSWER shapes are OLD-APP-DERIVED and
    // bench-UNCONFIRMED (protocol.md provisional section). Mirrored verbatim
    // so nothing is invented; graduate or correct after the bench confirm.
    RxPreamp,
    InternalCoupler,
    OneKilowattPa,

    // UI-tweaks round-4 AC mirror additions (plan-ui-tweaks-round4.md): the
    // two DEVICE answers whose payload shapes are OLD-APP-DERIVED and
    // bench-UNCONFIRMED (docs/protocol.md round-4 provisional subsection).
    // Mirrored verbatim, same discipline as the round-3 three above.
    BacklightFunction,
    BacklightIntensity,

    // UI-tweaks round-8 EE (plan-ui-tweaks-round8.md, scope amendment X7):
    // the MODEM PRE stored-preset listing mirror (one raw line per stored
    // preset, the ChannelList idiom).
    ModemPresets,

    // ALE
    AleLinkState,
    AleLinkedStation,
    // Round 15 item I: the LQA run's own station/channel slot. Separate from
    // AleLinkedStation because a progress line must not overwrite the CALL
    // slot (critic F73) — and separate as an EVENT because the second and
    // every later channel line of one run leaves the link state unchanged, so
    // AleLinkState would raise nothing and the banner would freeze on CH 30.
    AleLqaProgress,
    AleFillState,
    AleSelfAddresses,
    AleIndividualAddresses,
    AleNetAddresses,
    AleTxMessages,
    AleRxMessages,
    AleLastHeard,
    AleLqaReport,
    // Phase R: the nine ALE settings the ALE SH block reports.
    AleAllCall,
    AleAnyCall,
    AleAmdDisplay,
    AleKeyToCall,
    AleListenBeforeTx,
    AleRadioSilence,
    AleMaxScanChannels,
    AleLinkTimeout,
    AleTuneTime,

    // ALE programming (plan-ale-programming.md §4.1): the scan channel-group
    // table, the radio's refusal lines, and the completion records of the
    // three sentinel-scoped read operations (book / groups / bare sync).
    AleChannelGroups,
    AleProgrammingRefusal,
    AleBookRead,
    AleGroupRead,
    AleSync,

    // UI-tweaks round 11 §8: the two NEW ALE read stores — per-net membership
    // (targeted NETAD) and the LQA schedule queue (bare EXCH) — each with its
    // mirror change and its sentinel-scoped completion record.
    AleNetMembers,
    AleMemberRead,
    AleLqaSchedules,
    AleScheduleRead,

    // UI-tweaks round 11 §8: the modem preset PRESENCE store (bulk MODEM PRE
    // lists only ENABLED presets — the only captured enabled/disabled signal)
    // and the completion of one modem read operation, targeted or presence.
    ModemPresetPresence,
    ModemPresetRead,

    // HOP
    HopCurrentNet,
    HopNets,
    HopNum,
    HopSyncState,
    HopGeneratingHopset,
    HopListInvalid,
    HopLists,
    HopNoHopset,
    HopNoNetId,

    // UI-tweaks round 11 §8 (R11/X9): the WB exclusion-band mirror and its
    // sentinel-scoped read completion. The empty table answers NOTHING at all,
    // so only the sentinel can tell read-empty from unread.
    HopExcludeBands,
    HopExcludeRead,

    // ---- Clone round 12 P1 (plan-clone-round12.md §3) --------------------

    /// <summary>The OPERATOR LOCKOUT mirror (bare <c>PROGRAM</c> + bare
    /// <c>SELECT</c> — a global state dump keyed (family, section, item)).</summary>
    Lockouts,
    /// <summary>Completion record of one sentinel-bracketed lockout read.</summary>
    LockoutRead,

    /// <summary>Force-wakeup burst-on-key (§9 C3): a bounded SESSION LATCH —
    /// confirmed-Enabled only, set by the <c>FORCE WAKEUP ENABLED</c> line,
    /// marked unconfirmed by the DIS send (a silent direction), cleared on
    /// reconnect like every mirror.</summary>
    ForceWakeup,

    /// <summary>The X13 zeroize settle state machine (§3 leg 2): the radio
    /// answered the prompt again after <c>ZERO</c>, or the bound expired.</summary>
    ZeroizeSettle,
}
