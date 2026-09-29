using SoloSpeaker.Core.PeerLink.Wire;

namespace SoloSpeaker.Core.PeerLink;

/// <summary>
/// The outcome of the ordered ingress pipeline of <c>docs/design.md</c> §7.1, naming which
/// check a datagram first failed.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>reason</em> and nothing more. It deliberately carries no indication of
/// whether the outcome is silent or raises <see cref="TrayState.Error"/>, because that is
/// not a property of the reason: §7.1 makes a MAC failure silent <em>per datagram</em> and
/// loud <em>in aggregate</em>, so the disposition is a property of the reducer's accumulated
/// view. §7.4 publishes one enumerated producer table for <c>error</c>, and encoding a
/// second copy of it here would give the wire layer authority over what the user sees.
/// </para>
/// <para>
/// The reason-to-tray-state mapping lands with the reducer, alongside the rate-based
/// producer that reads <see cref="BadMac"/>.
/// </para>
/// </remarks>
public enum IngressResult
{
    /// <summary>Passed every check. Ready for the presence layer or §5.4, per <see cref="PeerDatagram.IsDeparture"/>.</summary>
    Accepted,

    /// <summary>
    /// Passed every check except roster membership, while the roster held one entry and the
    /// §7.7 pairing window was open. The sender is enrolled rather than dropped - see
    /// ADR 0011. Authentication is unaffected: step 2 already ran, so enrollment still
    /// requires possession of <c>pairKey</c>.
    /// </summary>
    PairingEnrollment,

    /// <summary>
    /// Not attributable to any numbered step, because steps 1 and 2 both presuppose a parse
    /// and this datagram yielded no <c>pairId</c>. Silent: it is indistinguishable from
    /// ordinary foreign traffic on the port.
    /// </summary>
    Unreadable,

    /// <summary>Step 1. Another pairing's datagram, or a stranger's. Silent.</summary>
    ForeignPairId,

    /// <summary>
    /// Step 2. The MAC did not verify, or the document was not canonical v1 and therefore
    /// could not be reconstructed to verify against.
    /// </summary>
    /// <remarks>
    /// Silent per datagram. The <em>rate</em> of this reason, while nothing valid has been
    /// accepted within the presence window, is the only signal a wire-version mismatch
    /// produces - a later-version peer fails here having passed step 1, whereas foreign
    /// traffic dies at step 1. Counting it separately from <see cref="ForeignPairId"/> is
    /// what makes §7.1's unverifiable-peer producer possible.
    /// </remarks>
    BadMac,

    /// <summary>
    /// Step 3, evaluated first within that step. Our own broadcast, heard back off the
    /// subnet. Silent - it is ordinary local traffic.
    /// </summary>
    /// <remarks>
    /// Dropping this is what keeps §7.1's presence rule honest. A machine that counted its
    /// own heartbeat as a peer datagram would remain permanently present to itself, so a
    /// machine whose peer vanished without a <c>bye</c> would satisfy §5.5 forever and stay
    /// muted for a peer that no longer exists.
    /// </remarks>
    SelfOrigin,

    /// <summary>Step 3. A valid signature from a machine that is not on the roster. Silent.</summary>
    NotInRoster,

    /// <summary>
    /// Step 4. The <c>seq</c> exceeds the local value by more than
    /// <see cref="WireProtocol.MaxSeqDelta"/>. Loud: it can only be corruption or an attack,
    /// and unbounded it would pin ownership forever on both machines.
    /// </summary>
    SeqOutOfBounds,

    /// <summary>
    /// Step 5. A wire version this machine does not know, from an authenticated peer - the
    /// only shape that reaches step 5, because anything missing a field fails at step 2.
    /// Loud.
    /// </summary>
    UnknownVersion,
}
