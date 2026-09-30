using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>§5.4's convergence rules, including the <c>bye</c> carve-out.</summary>
public sealed class ConvergenceTests
{
    [Fact]
    public void A_higher_seq_is_adopted()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        harness.PeerState(harness.Peer, 7);

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
        Assert.Equal(7UL, harness.State.Seq);
    }

    [Fact]
    public void A_stale_lower_seq_datagram_does_not_move_ownership()
    {
        ReducerHarness harness = ReducerHarness.For().Claim().Claim();
        harness.PeerState(harness.Peer, 9);

        MachineId owner = harness.State.ActiveOwner;
        ulong seq = harness.State.Seq;

        harness.PeerState(ReducerHarness.Id(ReducerHarness.StrangerHex), 4);

        Assert.Equal(owner, harness.State.ActiveOwner);
        Assert.Equal(seq, harness.State.Seq);
    }

    [Fact]
    public void An_equal_seq_with_agreeing_owners_is_a_no_op()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 5);
        harness.PeerState(harness.Peer, 5);

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
        Assert.Equal(5UL, harness.State.Seq);
        Assert.Empty(harness.Persists);
    }

    /// <summary>
    /// §5.4: concurrent edges break by the lexicographically smaller roster ID, "evaluated
    /// identically on both sides". B is the smaller of the two test identifiers.
    /// </summary>
    [Fact]
    public void Concurrent_equal_seq_edges_converge_to_the_same_owner_on_both_sides()
    {
        // A claims locally, and hears B's simultaneous claim at the same seq.
        ReducerHarness fromA = ReducerHarness.For(ReducerHarness.AHex, ReducerHarness.BHex).Claim();
        fromA.PeerState(fromA.Self == ReducerHarness.Id(ReducerHarness.AHex) ? fromA.Peer : fromA.Self, 1);

        // B does the mirror image.
        ReducerHarness fromB = ReducerHarness.For(ReducerHarness.BHex, ReducerHarness.AHex).Claim();
        fromB.PeerState(fromB.Peer, 1);

        Assert.Equal(ReducerHarness.Id(ReducerHarness.BHex), fromA.State.ActiveOwner);
        Assert.Equal(fromA.State.ActiveOwner, fromB.State.ActiveOwner);
    }

    /// <summary>§5.4: "then bump `seq` so the resolution propagates."</summary>
    [Fact]
    public void Breaking_a_tie_bumps_seq_and_propagates_the_resolution()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        harness.PeerState(harness.Peer, 1);

        Assert.Equal(2UL, harness.State.Seq);
        Assert.Collection(
            harness.Effects,
            effect => Assert.Equal(new ArbitrationEffect.PersistState(harness.Peer, 2, OwnershipSource.TiebreakWin), effect),
            effect => Assert.Equal(new ArbitrationEffect.Broadcast(harness.Peer, 2, false), effect));
    }

    /// <summary>
    /// Reachable during the §7.7 pairing window, because an enrolling datagram is accepted
    /// before the roster is complete. No winner means no write - Goal 1's direction.
    /// </summary>
    [Fact]
    public void An_incomplete_roster_has_no_tiebreak_winner_and_writes_nothing()
    {
        ReducerHarness harness = ReducerHarness.For(peerHex: null).Claim();
        ulong seq = harness.State.Seq;

        harness.PeerState(ReducerHarness.Id(ReducerHarness.BHex), seq);

        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.Equal(seq, harness.State.Seq);
        Assert.False(harness.ShouldMute);
    }

    /// <summary>
    /// §5.4: "A datagram carrying `bye: true` never reaches these rules." The event carries
    /// no <c>seq</c> and no owner, so this holds by construction rather than by a branch.
    /// </summary>
    [Fact]
    public void A_departure_cannot_move_the_latch_in_either_direction_of_ordering()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();
        MachineId owner = harness.State.ActiveOwner;
        ulong seq = harness.State.Seq;

        harness.PeerBye();

        Assert.Equal(owner, harness.State.ActiveOwner);
        Assert.Equal(seq, harness.State.Seq);
        Assert.Empty(harness.Effects);
    }

    /// <summary>
    /// §5.1 reads <c>lastSeenPeerSeq</c>, so a departure that wrote it would let a replayed
    /// <c>bye</c> inflate our next claim by up to the §5.4 bound.
    /// </summary>
    [Fact]
    public void A_departure_does_not_touch_the_seq_our_next_claim_reads()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 100);

        ulong seen = harness.State.LastSeenPeerSeq;
        harness.PeerBye().PeerBye();

        Assert.Equal(seen, harness.State.LastSeenPeerSeq);

        harness.Claim();
        Assert.Equal(101UL, harness.State.Seq);
    }
}
