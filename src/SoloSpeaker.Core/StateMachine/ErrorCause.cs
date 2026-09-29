namespace SoloSpeaker.Core.StateMachine;

/// <summary>
/// The causes of <see cref="TrayState.Error"/>, one member per row of
/// <c>docs/design.md</c> §7.4's producer table.
/// </summary>
/// <remarks>
/// <para>
/// This enum is the machine-readable copy of that table. §7.4 requires the tooltip to name
/// the specific cause, and four of the producers are raised outside the reducer, so a
/// stringly-typed or boolean cause is how the table rots.
/// </para>
/// <para>
/// Causes divide into two kinds and are acknowledged differently - see
/// <see cref="ErrorCauseExtensions.IsContinuous"/>.
/// </para>
/// </remarks>
public enum ErrorCause
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>
    /// §5.5: <c>activeOwner</c> names a machine outside the roster, and is not the reserved
    /// <c>MachineId.None</c>. Continuous. Raised by the reducer.
    /// </summary>
    ActiveOwnerOutsideRoster,

    /// <summary>
    /// §7.7 / ADR 0011: the pairing window expired with one roster entry. Continuous.
    /// Raised at row 10, because the window's lifetime lives there.
    /// </summary>
    RosterIncompleteAfterPairing,

    /// <summary>
    /// §7.5: <c>config.json</c> and <c>state.json</c> carry different <c>pairId</c>.
    /// Continuous. Raised at row 5.
    /// </summary>
    StatePairIdMismatch,

    /// <summary>§7.4: <c>RegisterHotKey</c> failed. Edge. Raised at row 9.</summary>
    HotkeyRegistrationFailed,

    /// <summary>§5.4 / §7.1 step 4: a datagram's <c>seq</c> exceeded the bound. Edge.</summary>
    SeqBoundExceeded,

    /// <summary>§7.1 step 5: an authenticated peer sent a wire version we do not know. Edge.</summary>
    UnknownWireVersion,

    /// <summary>
    /// §7.1: sustained unverifiable traffic on our <c>pairId</c> with nothing valid
    /// accepted within the presence window. Edge.
    /// </summary>
    PeerUnverifiable,

    /// <summary>§7.3: the mutation ledger could not be replayed. Edge. Raised at row 7.</summary>
    LedgerReplayFailed,

    /// <summary>§7.3: the render endpoint could not be enumerated. Edge. Raised at row 7.</summary>
    EndpointEnumerationFailed,

    /// <summary>§7.5: <c>config.json</c> is unreadable on this profile. Continuous. Raised at row 5.</summary>
    ConfigUnreadable,

    /// <summary>
    /// §7.5: <c>state.json</c> is present but refused - malformed, a bad <c>pairId</c>, or a
    /// non-canonical <c>activeOwner</c>. Continuous. Raised at row 5.
    /// </summary>
    StateUnreadable,

    /// <summary>
    /// §7.5: a persisted file carries a <c>schema</c> this build does not recognise.
    /// Continuous - refused, never migrated and never coerced. Raised at row 5.
    /// </summary>
    PersistedSchemaUnknown,

    /// <summary>
    /// §7.5: the configuration store refused the <c>pairKey</c> as structurally unsound -
    /// the wrong length, or every byte identical. Continuous. Raised at row 5.
    /// </summary>
    PairKeyRefused,

    /// <summary>
    /// §7.5: a tunable field failed validation and fell back to its documented default.
    /// Edge. The tooltip names the field, because the machine keeps working and the user
    /// needs to know which value was ignored.
    /// </summary>
    TunableFellBackToDefault,

    /// <summary>
    /// §7.5: the atomic write of <c>state.json</c> failed with the process still alive - a
    /// full disk, or a file locked by a backup agent. Edge. Raised at row 5.
    /// </summary>
    /// <remarks>
    /// Added in design revision 7. The peer can then hold a claim our disk never recorded,
    /// and the effect list had no channel to report it.
    /// </remarks>
    StatePersistFailed,
}

/// <summary>Classification helpers for <see cref="ErrorCause"/>.</summary>
public static class ErrorCauseExtensions
{
    /// <summary>
    /// Whether the cause is re-derived on every evaluation rather than latched.
    /// </summary>
    /// <remarks>
    /// Continuous causes <b>cannot be acknowledged while they remain true</b>. Clearing one
    /// would drop the tray back to an ordinary state while the machine sits in exactly the
    /// condition the error exists to expose - and §7.4 is explicit that the tray is "the
    /// only visible explanation for why a machine is silent". Edge causes latch until
    /// acknowledged, because nothing re-raises them.
    /// </remarks>
    public static bool IsContinuous(this ErrorCause cause) => cause switch
    {
        ErrorCause.ActiveOwnerOutsideRoster => true,
        ErrorCause.RosterIncompleteAfterPairing => true,
        ErrorCause.StatePairIdMismatch => true,
        ErrorCause.ConfigUnreadable => true,
        ErrorCause.StateUnreadable => true,
        ErrorCause.PersistedSchemaUnknown => true,
        ErrorCause.PairKeyRefused => true,
        _ => false,
    };
}
