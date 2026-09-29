using System.Collections.Immutable;
using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.StateMachine;

/// <summary>
/// The §7.6 rejoin window: when it opened, whether the peer was observed during it, and
/// what the peer's state was when last observed.
/// </summary>
/// <remarks>
/// <para>
/// A type rather than three flat fields on <see cref="ArbitrationState"/>, because the three
/// are only meaningful together. Flat, "started at T with no window open" and "the latch is
/// set outside any window" are both representable - and the latch's lifetime is the one
/// thing §7.6 is careful about. Same move <see cref="Roster"/> makes with its private
/// constructor.
/// </para>
/// <para>
/// <see cref="ObservedOwner"/> and <see cref="ObservedSeq"/> exist because §7.6's
/// adopt-at-expiry has nothing to adopt from without them, and the value cannot be
/// recovered by applying §5.4 as observations arrive: §10's row is "adopt peer's owner
/// <em>even when our <c>seq</c> is higher</em>", which is exactly the case §5.4 says to
/// ignore.
/// </para>
/// </remarks>
/// <param name="StartedAt">Monotonic time the window opened or was restarted.</param>
/// <param name="PeerObserved">
/// The latch. Set by any accepted non-<c>bye</c> datagram, and cleared by nothing for the
/// remainder of the window - in particular not by a <c>bye</c>, and not by a restart.
/// </param>
/// <param name="ObservedOwner">The peer's <c>activeOwner</c> when last observed.</param>
/// <param name="ObservedSeq">The peer's <c>seq</c> when last observed.</param>
public sealed record QuarantineWindow(
    TimeSpan StartedAt,
    bool PeerObserved,
    MachineId ObservedOwner,
    ulong ObservedSeq)
{
    /// <summary>Opens a window with the latch unset.</summary>
    public static QuarantineWindow Started(TimeSpan now) =>
        new(now, PeerObserved: false, MachineId.None, 0);

    /// <summary>
    /// Restarts the window, <b>preserving</b> the latch and the observed pair.
    /// </summary>
    /// <remarks>
    /// Design revision 7. A restart begins a new window, so §7.6's "nothing clears it for
    /// the remainder of the window" did not settle this. Both answers have a failure mode:
    /// a surviving latch lets a flapping network carry a stale observation forward, while a
    /// cleared one means a brief network transition during a legitimate rejoin erases a
    /// correct observation, the machine expires "with no peer", and asserts its stale
    /// ownership - the lid-open defect through an ordinary Wi-Fi change. Preserving fails
    /// toward deferring to the continuously-running machine, which is §7.6's rationale.
    /// </remarks>
    public QuarantineWindow RestartedAt(TimeSpan now) => this with { StartedAt = now };

    /// <summary>Records an observation, last-writer-wins.</summary>
    public QuarantineWindow Observing(MachineId owner, ulong seq) =>
        this with { PeerObserved = true, ObservedOwner = owner, ObservedSeq = seq };

    /// <summary>Whether the window has run its course.</summary>
    public bool HasExpired(TimeSpan now, TimeSpan window) => now - StartedAt >= window;
}

/// <summary>
/// The whole arbitration latch: <c>docs/design.md</c> §5's replicated value plus the local
/// facts §5.5, §7.1, §7.4 and §7.6 derive from.
/// </summary>
/// <remarks>
/// <para>
/// Exactly two fields round-trip through <c>state.json</c> - <see cref="ActiveOwner"/> and
/// <see cref="Seq"/> - which is what <c>IStateStore.SaveState</c> already takes. Everything
/// else is deliberately volatile, and the two that could surprise a reader are worth naming:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="LastSeenPeerSeq"/> restarts at zero, so §5.1's
/// <c>max(localSeq, lastSeenPeerSeq) + 1</c> degrades to <c>localSeq + 1</c> after a
/// restart. That is safe only because §5.4 keeps <see cref="Seq"/> at or above
/// <see cref="LastSeenPeerSeq"/> - adoption sets both - so the maximum is already
/// <see cref="Seq"/>. The invariant is load-bearing; it is stated here rather than
/// rediscovered.
/// </item>
/// <item>
/// <see cref="StickyError"/> does not survive a restart. §7.4 makes it sticky until
/// acknowledged, and a restart is not an acknowledgement - but a continuous cause is
/// re-derived immediately on the first evaluation, and an edge cause describes an event
/// the restarted process did not witness.
/// </item>
/// </list>
/// </remarks>
public sealed record ArbitrationState
{
    /// <summary>§5's replicated owner. <c>MachineId.None</c> until the first §5.1 write.</summary>
    public MachineId ActiveOwner { get; init; } = MachineId.None;

    /// <summary>§5's Lamport clock. Orders by event count, never by wall-clock time.</summary>
    public ulong Seq { get; init; }

    /// <summary>The highest <c>seq</c> any accepted peer datagram has carried. §5.1 reads it.</summary>
    public ulong LastSeenPeerSeq { get; init; }

    /// <summary>Monotonic time a peer datagram last established presence, or <see langword="null"/>.</summary>
    public TimeSpan? PeerLastSeenAt { get; init; }

    /// <summary>
    /// §7.1: set by an accepted <c>bye</c>. While set, presence is re-established only by a
    /// datagram carrying information we have not already accepted.
    /// </summary>
    public bool PeerDeparted { get; init; }

    /// <summary>The <c>(activeOwner, seq)</c> of the last accepted peer state datagram.</summary>
    public MachineId LastAcceptedOwner { get; init; } = MachineId.None;

    /// <inheritdoc cref="LastAcceptedOwner"/>
    public ulong LastAcceptedSeq { get; init; }

    /// <summary>Whether any peer state datagram has ever been accepted.</summary>
    public bool HasAcceptedPeerState { get; init; }

    /// <summary>Monotonic time a peer datagram was last accepted, for §7.1's rate producer.</summary>
    public TimeSpan? LastAcceptedAt { get; init; }

    /// <summary>
    /// §5.5's safety override. Phase 1 holds this <see langword="false"/>; §8 ships phase 1
    /// without mic detection.
    /// </summary>
    public bool SelfMicLive { get; init; }

    /// <summary>The §7.6 window, or <see langword="null"/> when not quarantined.</summary>
    public QuarantineWindow? Quarantine { get; init; }

    /// <summary>The latched edge cause of §7.4's <c>error</c>, or <see cref="ErrorCause.None"/>.</summary>
    public ErrorCause StickyError { get; init; }

    /// <summary>Monotonic time of the last broadcast, for §7.1's 2 s cadence.</summary>
    public TimeSpan? LastBroadcastAt { get; init; }

    /// <summary>
    /// Monotonic times of recent unverifiable datagrams, pruned to the presence window - a
    /// sliding window, because a tumbling counter misses four-then-four across a boundary.
    /// </summary>
    public ImmutableArray<TimeSpan> UnverifiableAt { get; init; } = [];

    /// <summary>First run: nothing claimed, no peer heard, not quarantined.</summary>
    public static ArbitrationState Fresh() => new();

    /// <summary>Startup from <c>state.json</c>, per §7.5.</summary>
    public static ArbitrationState FromPersisted(MachineId activeOwner, ulong seq) =>
        new() { ActiveOwner = activeOwner, Seq = seq };

    /// <summary>
    /// §7.1 presence: a datagram from the peer arrived within the window, and the peer has
    /// not departed.
    /// </summary>
    public bool IsPeerPresent(TimeSpan now, TimeSpan presenceWindow) =>
        !PeerDeparted && PeerLastSeenAt is { } seen && now - seen <= presenceWindow;
}
