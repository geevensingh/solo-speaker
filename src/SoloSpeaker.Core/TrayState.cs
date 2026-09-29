namespace SoloSpeaker.Core;

/// <summary>
/// The tray states of <c>docs/design.md</c> §7.4, together with the producers that raise
/// them. Every state in this enum must have at least one producer in the state machine;
/// revision 1 of the design listed an <c>error</c> state that nothing ever raised.
/// </summary>
public enum TrayState
{
    /// <summary>Raised when <c>activeOwner == self</c>.</summary>
    Active,

    /// <summary>Raised when the §5.5 mute predicate is true.</summary>
    Muted,

    /// <summary>
    /// Raised when <c>activeOwner</c> is <c>MachineId.None</c> and a peer is present -
    /// nobody has claimed yet, so both machines are audible.
    /// </summary>
    /// <remarks>
    /// Added in design revision 7. Revision 6 reserved <c>MachineId.None</c> as the
    /// pre-claim value and left §7.4's five-state table non-total: with that owner and a
    /// peer present, every one of the five was false by its own definition. It gets a state
    /// of its own rather than being folded into <see cref="Active"/> or <see cref="Alone"/>
    /// because it is neither - <see cref="Active"/> means "I hold the latch, so the peer is
    /// silent" and <see cref="Alone"/> means "there is no peer", and both are diagnostics
    /// this enum exists to keep distinct.
    /// <para>
    /// In phase 1 this is not a transient. §8 ships phase 1 without mic detection, so the
    /// only §5.1 writer is a manual claim and a freshly paired pair stays here until
    /// somebody presses the hotkey.
    /// </para>
    /// </remarks>
    Unclaimed,

    /// <summary>Raised when no peer heartbeat has arrived within the presence window.</summary>
    Alone,

    /// <summary>Raised during the §7.6 rejoin window, before state is reconciled.</summary>
    Quarantine,

    /// <summary>
    /// Raised by any of: <c>activeOwner</c> outside the roster and not
    /// <c>MachineId.None</c>, hotkey registration failure, <c>seq</c> bound exceeded, an
    /// unknown <c>v</c> from an authenticated peer, ledger replay failure, or endpoint
    /// enumeration failure. Sticky until acknowledged, and the tooltip must name the
    /// specific cause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MachineId.None</c> is excluded deliberately: it is the ordinary pre-claim state
    /// of §5, outside the roster by construction, and without the carve-out a freshly
    /// paired pair would sit in sticky error from the moment the ceremony completed.
    /// </para>
    /// <para>
    /// "A datagram missing <c>micLive</c>" was listed here through design revision 5 and is
    /// struck: <c>docs/wire-format.md</c> rule 2 makes the receiver re-canonicalize parsed
    /// values, so a datagram missing any signed field cannot be reconstructed and dies at
    /// §7.1 step 2 - silent, and counted toward the unverifiable-peer producer instead.
    /// Leaving it would have been the defect this enum's own summary forbids.
    /// </para>
    /// </remarks>
    Error,
}
