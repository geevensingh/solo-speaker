using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.Composition;

/// <summary>
/// What one turn of the cycle produced, published for whoever is watching.
/// </summary>
/// <remarks>
/// <para>
/// Published by <see cref="ArbitrationLoop"/> <b>once per <c>Post</c>, after</b> the
/// conditional error re-entry, so the values are the ones the cycle finished with. Publishing
/// from inside the effect executor instead would either miss that second reduction or log
/// twice, and the event it would miss is a failed state write or a failed unmute - the two
/// highest-consequence things the product can report.
/// </para>
/// <para>
/// Row 8's log is the first subscriber and row 9's tray is the second. Fan-out happens in the
/// host, which already owns construction order, rather than here - a Core type with a
/// subscriber list is an event bus, and <c>AGENTS.md</c> §3 puts that kind of wiring on the
/// other side of the boundary.
/// </para>
/// </remarks>
/// <param name="Event">What was reduced.</param>
/// <param name="Result">The reduction, including §5.5 and §7.4's derived values.</param>
/// <param name="PreviousState">The state before, so a transition can be detected.</param>
/// <param name="Ingress">
/// The ingress verdict, when the cycle began with bytes off the wire rather than with an
/// event. <see langword="null"/> otherwise.
/// </param>
public readonly record struct CycleObservation(
    ArbitrationEvent Event,
    ReducerResult Result,
    ArbitrationState PreviousState,
    IngressResult? Ingress);
