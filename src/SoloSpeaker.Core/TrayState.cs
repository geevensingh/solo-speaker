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

    /// <summary>Raised when no peer heartbeat has arrived within the presence window.</summary>
    Alone,

    /// <summary>Raised during the §7.6 rejoin window, before state is reconciled.</summary>
    Quarantine,

    /// <summary>
    /// Raised by any of: <c>activeOwner</c> outside the roster, hotkey registration
    /// failure, <c>seq</c> bound exceeded, a datagram missing <c>micLive</c>, ledger
    /// replay failure, or endpoint enumeration failure. Sticky until acknowledged, and
    /// the tooltip must name the specific cause.
    /// </summary>
    Error,
}
