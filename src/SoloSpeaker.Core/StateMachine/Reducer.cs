using System.Collections.Immutable;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;

namespace SoloSpeaker.Core.StateMachine;

/// <summary>
/// The pure reducer of <c>docs/design.md</c> §6:
/// <c>(currentState, event, now) -&gt; (newState, effects)</c>.
/// </summary>
/// <remarks>
/// <para>
/// No socket, no clock, no disk, no device. <c>now</c> is <b>monotonic</b> - every window in
/// the design is measured against it, so a wall-clock adjustment or a resume from sleep
/// cannot move a mute. Wall-clock time enters the system only as <c>sentUtc</c>, which the
/// codec already takes as a caller-supplied parameter.
/// </para>
/// <para>
/// The whole arbitration model is therefore unit-testable in process, which is the claim
/// <c>implementation-plan.md</c> §4.1 makes about what CI can prove.
/// </para>
/// </remarks>
public static class Reducer
{
    /// <summary>
    /// Publishes the derived values for a state without reducing anything.
    /// </summary>
    /// <remarks>
    /// What a loop needs at construction: §5.5's mute predicate and §7.4's tray state are
    /// functions of the state, not of any event, so a freshly-built loop can expose them
    /// without first inventing an event to reduce. It emits no effects, which is what makes
    /// it safe to call before an executor is wired up.
    /// </remarks>
    public static ReducerResult Initial(in ArbitrationContext context, ArbitrationState state, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context.Roster);

        return Derive(context, state, ImmutableArray<ArbitrationEffect>.Empty, now);
    }

    /// <summary>Reduces one event.</summary>
    public static ReducerResult Reduce(
        in ArbitrationContext context,
        ArbitrationState state,
        ArbitrationEvent arbitrationEvent,
        TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(arbitrationEvent);
        ArgumentNullException.ThrowIfNull(context.Roster);

        var effects = ImmutableArray.CreateBuilder<ArbitrationEffect>();

        state = arbitrationEvent switch
        {
            ArbitrationEvent.MicRisingEdge => Claim(context, state, effects, OwnershipSource.MicEdge),
            ArbitrationEvent.ManualClaim => Claim(context, state, effects, OwnershipSource.ManualClaim),
            ArbitrationEvent.SelfMicChanged mic => state with { SelfMicLive = mic.Live },
            ArbitrationEvent.PeerStateReceived peer => PeerState(context, state, peer, effects, now),
            ArbitrationEvent.PeerDeparted => Departed(state),
            ArbitrationEvent.PeerDatagramRejected rejected => Rejected(context, state, rejected, now),
            ArbitrationEvent.QuarantineEntered => Entered(state, now),
            ArbitrationEvent.QuarantineRestarted => Restarted(state, now),
            ArbitrationEvent.ErrorRaised raised => Raise(state, raised.Cause),
            ArbitrationEvent.ErrorResolved resolved => Resolve(state, resolved.Cause),
            ArbitrationEvent.ErrorAcknowledged => Acknowledge(state),
            ArbitrationEvent.Tick => Tick(context, state, effects, now),
            _ => state,
        };

        // §7.1's 2 s cadence is time-driven, so only a tick can raise one. A state change
        // emits its own immediate broadcast from Write, which is the "plus an immediate
        // extra send" half of the same rule.
        if (arbitrationEvent is ArbitrationEvent.Tick)
        {
            MaybeBeat(context, state, effects, now);
        }

        if (effects.Any(effect => effect is ArbitrationEffect.Broadcast))
        {
            state = state with { LastBroadcastAt = now };
        }

        return Derive(context, state, effects.ToImmutable(), now);
    }

    // §5.1. The only two writers, and they share one path so they cannot drift apart.
    private static ArbitrationState Claim(
        in ArbitrationContext context,
        ArbitrationState state,
        ImmutableArray<ArbitrationEffect>.Builder effects,
        OwnershipSource source)
    {
        MachineId self = context.Roster.Self;
        ulong next = Advance(Math.Max(state.Seq, state.LastSeenPeerSeq));

        // §5.2: a claim on the machine that already owns the latch is a no-op. It still
        // exits quarantine, because §7.6's carve-out is about the window, not the write.
        if (state.ActiveOwner == self && state.Quarantine is null)
        {
            return state;
        }

        state = state with { ActiveOwner = self, Seq = next, Quarantine = null };
        Write(state, effects, source);
        return state;
    }

    private static ArbitrationState PeerState(
        in ArbitrationContext context,
        ArbitrationState state,
        ArbitrationEvent.PeerStateReceived peer,
        ImmutableArray<ArbitrationEffect>.Builder effects,
        TimeSpan now)
    {
        // §7.1, design revision 7: after a departure, only information we have not already
        // accepted re-establishes presence. A replayed datagram carries the peer's own id as
        // activeOwner - the one value §5.5 is looking for - so an echo would otherwise undo
        // a bye and hold us muted for a machine that has gone.
        bool isNews =
            !state.HasAcceptedPeerState ||
            peer.Seq != state.LastAcceptedSeq ||
            peer.ActiveOwner != state.LastAcceptedOwner;

        state = state with
        {
            LastAcceptedOwner = peer.ActiveOwner,
            LastAcceptedSeq = peer.Seq,
            HasAcceptedPeerState = true,
            LastAcceptedAt = now,
            LastSeenPeerSeq = Math.Max(state.LastSeenPeerSeq, peer.Seq),

            // §7.1's rate producer counts unverifiable traffic only while nothing valid is
            // being accepted, so an acceptance resets it rather than merely ageing it out.
            UnverifiableAt = [],
        };

        if (!state.PeerDeparted || isNews)
        {
            state = state with { PeerLastSeenAt = now, PeerDeparted = false };
        }

        // §7.6: during the window the peer is observed, never adopted. §5.4 is not applied.
        if (state.Quarantine is { } window)
        {
            return state with { Quarantine = window.Observing(peer.ActiveOwner, peer.Seq) };
        }

        return Converge(context, state, peer, effects);
    }

    // §5.4.
    private static ArbitrationState Converge(
        in ArbitrationContext context,
        ArbitrationState state,
        ArbitrationEvent.PeerStateReceived peer,
        ImmutableArray<ArbitrationEffect>.Builder effects)
    {
        if (peer.Seq > state.Seq)
        {
            state = state with { ActiveOwner = peer.ActiveOwner, Seq = peer.Seq };
            Write(state, effects, OwnershipSource.PeerAdoption);
            return state;
        }

        if (peer.Seq < state.Seq || peer.ActiveOwner == state.ActiveOwner)
        {
            // Lower seq: ignore, our next broadcast corrects the peer. Equal and agreeing:
            // nothing to do.
            return state;
        }

        // Equal seq, disagreeing owners: concurrent edges. Both sides evaluate this
        // identically, then bump seq so the resolution propagates.
        if (!context.Roster.TryGetTiebreakWinner(out MachineId winner))
        {
            // No peer enrolled yet, so there is no tie to break - reachable during the §7.7
            // pairing window, because an enrolling datagram is accepted before the roster is
            // complete. No winner means no write, which leaves both machines audible.
            return state;
        }

        state = state with { ActiveOwner = winner, Seq = Advance(state.Seq) };
        Write(state, effects, OwnershipSource.TiebreakWin);
        return state;
    }

    // §7.1 receipt rule 2. Touches presence and nothing else - not LastSeenPeerSeq, not
    // ActiveOwner, not Seq, and not the §7.6 latch. §5.1 reads LastSeenPeerSeq, so writing
    // it here would let a replayed bye inflate our next claim by up to the seq bound.
    private static ArbitrationState Departed(ArbitrationState state) =>
        state with { PeerDeparted = true };

    private static ArbitrationState Rejected(
        in ArbitrationContext context,
        ArbitrationState state,
        ArbitrationEvent.PeerDatagramRejected rejected,
        TimeSpan now)
    {
        // §7.1 steps 4 and 5 are loud per datagram: they can only be corruption, an attack,
        // or a peer this machine cannot work with.
        if (rejected.Reason == IngressResult.SeqOutOfBounds)
        {
            return Raise(state, ErrorCause.SeqBoundExceeded);
        }

        if (rejected.Reason == IngressResult.UnknownVersion)
        {
            return Raise(state, ErrorCause.UnknownWireVersion);
        }

        // Only pairId-matching, mac-failing traffic feeds the rate producer. Foreign traffic
        // dies at step 1 and is not evidence of anything.
        if (rejected.Reason != IngressResult.BadMac)
        {
            return state;
        }

        TimeSpan window = context.Tunables.PresenceWindow;
        ImmutableArray<TimeSpan> recent =
        [
            .. state.UnverifiableAt.Where(seen => now - seen <= window),
            now,
        ];

        state = state with { UnverifiableAt = recent };

        bool nothingValidAccepted =
            state.LastAcceptedAt is not { } accepted || now - accepted > window;

        return recent.Length >= context.Tunables.UnverifiableThreshold && nothingValidAccepted
            ? Raise(state, ErrorCause.PeerUnverifiable)
            : state;
    }

    private static ArbitrationState Entered(ArbitrationState state, TimeSpan now) =>
        state.Quarantine is null
            ? state with { Quarantine = QuarantineWindow.Started(now) }
            : state;

    private static ArbitrationState Restarted(ArbitrationState state, TimeSpan now) =>
        state with
        {
            Quarantine = state.Quarantine is { } window
                ? window.RestartedAt(now)
                : QuarantineWindow.Started(now),
        };

    private static ArbitrationState Tick(
        in ArbitrationContext context,
        ArbitrationState state,
        ImmutableArray<ArbitrationEffect>.Builder effects,
        TimeSpan now)
    {
        if (state.Quarantine is not { } window || !window.HasExpired(now, context.Tunables.QuarantineWindow))
        {
            return state;
        }

        state = state with { Quarantine = null };

        if (!window.PeerObserved)
        {
            // §7.6: expired with the latch unset, so resume normally from persisted state.
            return state;
        }

        // §7.6: adopt the peer's recorded pair regardless of seq ordering, and advance past
        // it - past ours as well as theirs, so the adoption is the newest fact and neither
        // side can un-adopt it.
        state = state with
        {
            ActiveOwner = window.ObservedOwner,
            Seq = Advance(Math.Max(state.Seq, window.ObservedSeq)),
        };

        Write(state, effects, OwnershipSource.QuarantineAdoption);
        return state;
    }

    private static ArbitrationState Raise(ArbitrationState state, ErrorCause cause) =>
        // §7.4: sticky until acknowledged, and the first cause raised is the one named.
        state.StickyError == ErrorCause.None ? state with { StickyError = cause } : state;

    /// <remarks>
    /// §7.4: a continuous cause "cannot be acknowledged while it remains true". The two the
    /// reducer cannot re-derive are therefore cleared by their owner retracting them, never
    /// by the user dismissing the tray - otherwise acknowledging would clear the tray while
    /// the machine sat in exactly the condition the error exists to expose.
    /// </remarks>
    private static ArbitrationState Acknowledge(ArbitrationState state) =>
        state.StickyError.IsContinuous() ? state : state with { StickyError = ErrorCause.None };

    /// <remarks>
    /// Raised by whichever component owns the condition once it stops being true - row 10
    /// when the roster completes, row 5 when a repaired configuration is loaded.
    /// </remarks>
    private static ArbitrationState Resolve(ArbitrationState state, ErrorCause cause) =>
        state.StickyError == cause ? state with { StickyError = ErrorCause.None } : state;

    // §7.1's 2 s cadence. A quarantined machine sends nothing at all - design revision 7 -
    // because any value it could put in activeOwner either erases the peer's ownership or
    // reasserts its own stale claim.
    private static void MaybeBeat(
        in ArbitrationContext context,
        ArbitrationState state,
        ImmutableArray<ArbitrationEffect>.Builder effects,
        TimeSpan now)
    {
        if (state.Quarantine is not null || effects.Any(effect => effect is ArbitrationEffect.Broadcast))
        {
            return;
        }

        bool due = state.LastBroadcastAt is not { } last || now - last >= context.Tunables.HeartbeatCadence;

        if (due)
        {
            effects.Add(new ArbitrationEffect.Broadcast(state.ActiveOwner, state.Seq, state.SelfMicLive));
        }
    }

    // §5.1's ordering: persist, then broadcast.
    private static void Write(
        ArbitrationState state,
        ImmutableArray<ArbitrationEffect>.Builder effects,
        OwnershipSource source)
    {
        effects.Add(new ArbitrationEffect.PersistState(state.ActiveOwner, state.Seq, source));
        effects.Add(new ArbitrationEffect.Broadcast(state.ActiveOwner, state.Seq, state.SelfMicLive));
    }

    private static ulong Advance(ulong seq) => seq == ulong.MaxValue ? ulong.MaxValue : seq + 1;

    private static ReducerResult Derive(
        in ArbitrationContext context,
        ArbitrationState state,
        ImmutableArray<ArbitrationEffect> effects,
        TimeSpan now)
    {
        Roster roster = context.Roster;
        bool peerPresent = state.IsPeerPresent(now, context.Tunables.PresenceWindow);

        // §5.5, in positive space. A machine mutes only when it can affirmatively name the
        // peer as owner - never merely because it failed to recognise itself.
        bool shouldMute =
            state.Quarantine is null &&
            peerPresent &&
            roster.Peer is { } peerId &&
            state.ActiveOwner == peerId &&
            !state.SelfMicLive;

        ErrorCause cause = EffectiveError(state, roster);

        return new ReducerResult(
            state,
            effects,
            shouldMute,
            peerPresent,
            TrayFor(state, roster, cause, peerPresent, shouldMute),
            cause);
    }

    private static ErrorCause EffectiveError(ArbitrationState state, Roster roster)
    {
        // §5.5's corrupt-owner case is the one continuous cause the reducer can see for
        // itself, so it is re-derived on every evaluation and needs no latch.
        bool ownerOutsideRoster = !state.ActiveOwner.IsNone && !roster.Contains(state.ActiveOwner);

        if (ownerOutsideRoster)
        {
            return ErrorCause.ActiveOwnerOutsideRoster;
        }

        // Everything else - including the two continuous causes raised from outside, which
        // the reducer cannot re-derive - is reported from the latch. An earlier version
        // discarded continuous causes here, which silently swallowed §7.5's cross-file
        // mismatch and §7.7's incomplete roster: the two conditions whose whole point is
        // that they are raised rather than tolerated.
        return state.StickyError;
    }

    private static TrayState TrayFor(
        ArbitrationState state,
        Roster roster,
        ErrorCause cause,
        bool peerPresent,
        bool shouldMute)
    {
        if (cause != ErrorCause.None)
        {
            return TrayState.Error;
        }

        if (state.Quarantine is not null)
        {
            return TrayState.Quarantine;
        }

        if (shouldMute)
        {
            return TrayState.Muted;
        }

        if (state.ActiveOwner == roster.Self)
        {
            return TrayState.Active;
        }

        if (!peerPresent)
        {
            return TrayState.Alone;
        }

        if (state.ActiveOwner.IsNone)
        {
            return TrayState.Unclaimed;
        }

        // Peer present, peer owns the latch, and we are audible anyway because §5.5's
        // safety override is engaged. §7.4 has no state for this and phase 2 must settle it
        // alongside MicWatcher, which is the only thing that can make SelfMicLive true -
        // §8 hardcodes it false in phase 1, so this is unreachable until then. Active is
        // the interim answer because the user-visible fact is that this machine is audible.
        return TrayState.Active;
    }
}
