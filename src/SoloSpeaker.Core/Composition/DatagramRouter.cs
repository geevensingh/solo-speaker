using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.Composition;

/// <summary>
/// Turns an ingress outcome into the arbitration event it means, and builds the per-datagram
/// ingress context from reducer state.
/// </summary>
/// <remarks>
/// <para>
/// Static and pure. This is the half of the cycle that genuinely wants to be a function: it
/// holds nothing, touches no seam, and is the one place the nine <see cref="IngressResult"/>
/// values are mapped, so a new value cannot be added without this switch failing to compile.
/// </para>
/// <para>
/// It lives in <c>Composition/</c> rather than <c>PeerLink/</c> for a dependency reason, not
/// a naming one. <c>PeerLink</c> references neither <c>StateMachine</c> nor
/// <c>Abstractions</c>; <c>StateMachine</c> references <c>PeerLink</c>. Routing to an
/// <see cref="ArbitrationEvent"/> means referencing <c>StateMachine</c>, so putting this in
/// <c>PeerLink</c> would invert a one-way graph into a cycle.
/// </para>
/// </remarks>
public static class DatagramRouter
{
    /// <summary>
    /// Builds the ingress context for one datagram, binding
    /// <see cref="IngressContext.LocalSeq"/> to live reducer state.
    /// </summary>
    /// <remarks>
    /// This is the single place that binding happens. <see cref="IngressContext"/> is
    /// documented as valid for exactly one datagram, and a cached one would evaluate §7.1
    /// step 4's bound against a <c>seq</c> the reducer had already moved past.
    /// </remarks>
    public static IngressContext ContextFor(ArbitrationState state, IConfigStore config, bool pairingWindowOpen)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(config);

        return IngressContext.ForDatagram(config.PairId, config.Roster, state.Seq, pairingWindowOpen);
    }

    /// <summary>
    /// Maps an ingress outcome to the event it means, or <see langword="null"/> when the
    /// reducer has nothing to learn from it.
    /// </summary>
    /// <remarks>
    /// The <c>bye</c>/non-<c>bye</c> split happens here rather than inside the reducer,
    /// which is what lets <see cref="ArbitrationEvent.PeerDeparted"/> carry no <c>seq</c> and
    /// no owner. §5.4 says a departure's ordering fields "are ignored in both directions",
    /// and review finding B-1 was a Critical caused by that being sender discipline the
    /// receiver did not enforce.
    /// </remarks>
    public static ArbitrationEvent? Route(in IngressOutcome outcome) => outcome.Result switch
    {
        IngressResult.Accepted => outcome.Datagram.IsDeparture
            ? new ArbitrationEvent.PeerDeparted()
            : new ArbitrationEvent.PeerStateReceived(
                outcome.Datagram.ActiveOwner, outcome.Datagram.Seq, outcome.Datagram.MicLive),

        // §7.1 steps 4 and 5 are loud, and the BadMac rate feeds the unverifiable-peer
        // producer, so all three reach the reducer as rejections.
        IngressResult.BadMac or IngressResult.SeqOutOfBounds or IngressResult.UnknownVersion =>
            new ArbitrationEvent.PeerDatagramRejected(outcome.Result),

        // Silent, and carrying no information the reducer acts on. Foreign traffic and our
        // own broadcasts are both expected in ordinary operation.
        IngressResult.Unreadable or IngressResult.ForeignPairId or
        IngressResult.SelfOrigin or IngressResult.NotInRoster => null,

        // ADR 0011's enrollment writes the roster through IConfigStore.TryCompleteRoster,
        // which row 10 implements. A loud unimplemented path is safer than a silent wrong
        // one: silently dropping this would make a machine unpairable with no signal.
        IngressResult.PairingEnrollment => throw new NotSupportedException(
            "Pairing enrollment arrives at work item 10; see ADR 0011."),

        _ => throw new NotSupportedException($"Unhandled ingress result '{outcome.Result}'."),
    };

    /// <summary>Builds the datagram an effect describes. The only construction path.</summary>
    /// <remarks>
    /// Row 6's <c>bye</c> emission adds a <em>caller</em> here rather than a second path into
    /// a format §8 freezes in phase 1.
    /// </remarks>
    public static PeerDatagram DatagramFor(
        IConfigStore config, MachineId activeOwner, ulong seq, bool micLive, bool bye, DateTimeOffset sentUtc)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new PeerDatagram(
            WireProtocol.Version, config.PairId, config.Roster.Self, seq, activeOwner, micLive, bye, sentUtc);
    }
}
