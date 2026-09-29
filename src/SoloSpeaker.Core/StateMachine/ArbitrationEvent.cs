using System.Collections.Immutable;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;

namespace SoloSpeaker.Core.StateMachine;

/// <summary>
/// The configuration the reducer reads while judging <b>one</b> event.
/// </summary>
/// <remarks>
/// <b>Valid for exactly one event. Never cache one.</b> The roster is mutable at runtime -
/// ADR 0011 completes it on the peer's first ordinary heartbeat - so a context held across
/// events can evaluate §5.5's predicate against a roster the machine has already replaced.
/// This is the same hazard <c>IngressContext</c> documents, and it is why
/// <see cref="ReducerResult"/> returns the derived values rather than letting each consumer
/// recompute them from its own snapshot.
/// </remarks>
public readonly record struct ArbitrationContext
{
    private ArbitrationContext(Roster roster, ArbitrationTunables tunables)
    {
        Roster = roster;
        Tunables = tunables;
    }

    /// <summary>§5.3's roster - the right-hand side of §5.5's predicate.</summary>
    public Roster Roster { get; }

    /// <summary>The §7.1 and §7.6 windows.</summary>
    public ArbitrationTunables Tunables { get; }

    /// <summary>Builds a context for a single event.</summary>
    public static ArbitrationContext ForEvent(Roster roster, ArbitrationTunables? tunables = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return new ArbitrationContext(roster, tunables ?? ArbitrationTunables.Default);
    }
}

/// <summary>Where a §7.4 manual claim came from. All three are the same claim.</summary>
/// <remarks>
/// §7.4: "Three input surfaces all map to the same claim event." One event with a tag
/// rather than three events, so the surfaces cannot drift apart - and so row 8 can make
/// ownership changes name their source without the reducer growing three code paths.
/// </remarks>
public enum ClaimSource
{
    /// <summary>The global hotkey of §7.4.</summary>
    Hotkey,

    /// <summary>A tray left-click.</summary>
    TrayClick,

    /// <summary>
    /// An external unmute of an endpoint we muted, per §9.1's decision D-1. The 250 ms
    /// self-change suppression that distinguishes this from our own write is the
    /// reconciler's rule (§7.3) and has already run before this reaches the reducer.
    /// </summary>
    ExternalUnmute,
}

/// <summary>
/// Everything that can move the arbitration latch, or the facts it derives from.
/// </summary>
/// <remarks>
/// A closed hierarchy: the private constructor means no case can be added outside this
/// file, so a <c>switch</c> over it is exhaustive by construction.
/// </remarks>
public abstract record ArbitrationEvent
{
    private ArbitrationEvent()
    {
    }

    /// <summary>§5.1's first writer: the rising edge of microphone-in-use on this machine.</summary>
    public sealed record MicRisingEdge : ArbitrationEvent;

    /// <summary>
    /// §7.2's safety signal, which is <b>not</b> a writer. §5.2 is explicit that a call
    /// ending does nothing; this only moves §5.5's override.
    /// </summary>
    public sealed record SelfMicChanged(bool Live) : ArbitrationEvent;

    /// <summary>§5.1's second writer.</summary>
    public sealed record ManualClaim(ClaimSource Source) : ArbitrationEvent;

    /// <summary>
    /// An accepted non-<c>bye</c> peer datagram, narrowed to the three values §5.4 and §5.5
    /// actually read.
    /// </summary>
    /// <remarks>
    /// Deliberately not the wire record. Carrying <c>PeerDatagram</c> would give the
    /// reducer read access to <c>SentUtc</c>, <c>Version</c>, <c>PairId</c> and
    /// <c>SenderId</c> - four fields it must never consult, and <c>SentUtc</c> is the
    /// dangerous one, because §5.4 orders "by event count, not wall-clock time" and a
    /// wall-clock field inside the event makes the forbidden ordering a one-line edit that
    /// reads as an improvement. It would also falsify <c>IngressContext</c>'s shipped
    /// claim that "the reducer consumes events rather than bytes, so neither component
    /// depends on the other".
    /// </remarks>
    public sealed record PeerStateReceived(MachineId ActiveOwner, ulong Seq, bool MicLive) : ArbitrationEvent;

    /// <summary>
    /// An accepted <c>bye</c>. Carries <b>no</b> <c>seq</c> and <b>no</b> owner.
    /// </summary>
    /// <remarks>
    /// That emptiness is the point. §5.4 says a departure's <c>seq</c> and
    /// <c>activeOwner</c> "are ignored in both directions of ordering", and review finding
    /// B-1 was a Critical caused by exactly this rule being sender discipline the receiver
    /// did not enforce. An event with nothing to move the latch with cannot move it,
    /// whatever a later edit does - unrepresentable rather than merely tested.
    /// </remarks>
    public sealed record PeerDeparted : ArbitrationEvent;

    /// <summary>
    /// A datagram ingress refused. §7.1 makes a MAC failure silent per datagram and loud in
    /// aggregate, so the aggregate lives here.
    /// </summary>
    public sealed record PeerDatagramRejected(IngressResult Reason) : ArbitrationEvent;

    /// <summary>§7.6: the window opens on first successful socket bind and send.</summary>
    public sealed record QuarantineEntered : ArbitrationEvent;

    /// <summary>
    /// §7.6: a network change restarts the window. Preserves the observation latch - see
    /// <see cref="QuarantineWindow.RestartedAt"/>.
    /// </summary>
    public sealed record QuarantineRestarted : ArbitrationEvent;

    /// <summary>An error raised by a component outside the reducer.</summary>
    public sealed record ErrorRaised(ErrorCause Cause) : ArbitrationEvent;

    /// <summary>
    /// The user acknowledged the tray error. Clears a latched edge cause; a continuous
    /// cause that is still true cannot be acknowledged away.
    /// </summary>
    public sealed record ErrorAcknowledged : ArbitrationEvent;

    /// <summary>
    /// The periodic pulse. Drives the §7.1 cadence, §7.6 expiry, and the re-evaluation
    /// §5.5 means by "evaluated every tick".
    /// </summary>
    public sealed record Tick : ArbitrationEvent;
}

/// <summary>Something the composition root must do as a result of a reduction.</summary>
public abstract record ArbitrationEffect
{
    private ArbitrationEffect()
    {
    }

    /// <summary>
    /// Write <c>(activeOwner, seq)</c> to <c>state.json</c>. §5.1 orders this
    /// <b>before</b> <see cref="Broadcast"/>, and <see cref="ReducerResult.Effects"/>
    /// preserves that order.
    /// </summary>
    public sealed record PersistState(MachineId ActiveOwner, ulong Seq) : ArbitrationEffect;

    /// <summary>
    /// Send a state datagram. Carries its own payload rather than letting the host re-read
    /// state afterwards, so what was decided and what is sent cannot diverge.
    /// </summary>
    public sealed record Broadcast(MachineId ActiveOwner, ulong Seq, bool MicLive) : ArbitrationEffect;
}

/// <summary>
/// The output of one reduction: the new state, what to do about it, and the derived values
/// §5.5 and §7.4 define.
/// </summary>
/// <remarks>
/// <see cref="ShouldMute"/> and <see cref="TrayState"/> are <b>returned</b>, not stored and
/// not left as free functions over <c>(state, roster)</c>. Storing them would give §5.5 two
/// sources; leaving them free would let row 7's reconciler and row 9's tray each evaluate
/// against a roster snapshot different from the one the reducer used, because ADR 0011
/// mutates the roster at runtime. Computing them once, here, from the context the reducer
/// was handed, is the only shape with neither problem.
/// </remarks>
/// <param name="State">The new state.</param>
/// <param name="Effects">Ordered. §5.1 persists before it broadcasts.</param>
/// <param name="ShouldMute">§5.5's predicate, evaluated in positive space.</param>
/// <param name="TrayState">§7.4's state - a total function of state, roster and time.</param>
/// <param name="ErrorCause">
/// The cause behind <see cref="Core.TrayState.Error"/>, for the tooltip §7.4 requires.
/// </param>
public readonly record struct ReducerResult(
    ArbitrationState State,
    ImmutableArray<ArbitrationEffect> Effects,
    bool ShouldMute,
    TrayState TrayState,
    ErrorCause ErrorCause);
