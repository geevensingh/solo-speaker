using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §5.1's two writers and §5.2's non-writers - "the central property of the design".
/// </summary>
public sealed class LatchTests
{
    [Fact]
    public void A_call_starting_takes_ownership()
    {
        ReducerHarness harness = ReducerHarness.For().MicEdge();

        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.Equal(1UL, harness.State.Seq);
    }

    [Fact]
    public void A_call_ending_does_not_release_ownership()
    {
        ReducerHarness harness = ReducerHarness.For()
            .MicEdge()
            .Apply(new ArbitrationEvent.SelfMicChanged(true))
            .Apply(new ArbitrationEvent.SelfMicChanged(false));

        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.Equal(1UL, harness.State.Seq);
    }

    [Fact]
    public void A_manual_claim_takes_ownership()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.Equal(1UL, harness.State.Seq);
    }

    [Theory]
    [InlineData(ClaimSource.Hotkey)]
    [InlineData(ClaimSource.TrayClick)]
    [InlineData(ClaimSource.ExternalUnmute)]
    public void All_three_claim_surfaces_are_the_same_claim(ClaimSource source)
    {
        ReducerHarness harness = ReducerHarness.For().Apply(new ArbitrationEvent.ManualClaim(source));

        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.Equal(1UL, harness.State.Seq);
    }

    /// <summary>§5.2: "A call starting on the machine that is already active. No-op."</summary>
    [Fact]
    public void A_claim_on_the_machine_that_already_owns_the_latch_does_not_advance_seq()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();
        ulong afterFirst = harness.State.Seq;

        harness.Claim().MicEdge();

        Assert.Equal(afterFirst, harness.State.Seq);
        Assert.Empty(harness.Persists);
    }

    /// <summary>§5.1: <c>seq = max(localSeq, lastSeenPeerSeq) + 1</c>.</summary>
    [Fact]
    public void A_claim_advances_past_the_highest_seq_the_peer_has_shown_us()
    {
        ReducerHarness harness = ReducerHarness.For();

        harness.PeerState(harness.Peer, 40).Claim();

        Assert.Equal(41UL, harness.State.Seq);
        Assert.Equal(harness.Self, harness.State.ActiveOwner);
    }

    /// <summary>
    /// §10: "claim on peer while peer absent, then peer returns -> peer is owner, we mute."
    /// </summary>
    [Fact]
    public void When_the_peer_claimed_while_absent_and_then_returns_it_owns_the_latch_and_we_mute()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.False(harness.ShouldMute);

        // The peer was away, claimed on its own machine, and comes back with a higher seq.
        harness.Apply(TimeSpan.FromSeconds(30), new ArbitrationEvent.PeerStateReceived(harness.Peer, 9, false));

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
        Assert.True(harness.ShouldMute);
        Assert.Equal(TrayState.Muted, harness.Tray);
    }

    /// <summary>§5.2: "Peer appearing or disappearing... never `activeOwner`."</summary>
    [Fact]
    public void Losing_the_peer_unmutes_without_moving_the_latch()
    {
        ReducerHarness harness = ReducerHarness.For().PeerState(ReducerHarness.Id(ReducerHarness.BHex), 5);

        Assert.True(harness.ShouldMute);

        harness.Apply(TimeSpan.FromSeconds(11), new ArbitrationEvent.Tick());

        Assert.False(harness.ShouldMute);
        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
        Assert.Equal(TrayState.Alone, harness.Tray);
    }

    /// <summary>§5.1 orders the write before the send, and §7.1 wants the send immediate.</summary>
    [Fact]
    public void A_claim_persists_before_it_broadcasts_and_does_not_wait_for_the_next_beat()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        Assert.Collection(
            harness.Effects,
            effect => Assert.Equal(new ArbitrationEffect.PersistState(harness.Self, 1), effect),
            effect => Assert.Equal(new ArbitrationEffect.Broadcast(harness.Self, 1, false), effect));
    }

    [Fact]
    public void An_unremarkable_event_emits_nothing()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Claim()
            .Apply(new ArbitrationEvent.SelfMicChanged(true));

        Assert.Empty(harness.Effects);
    }

    /// <summary>
    /// The peer's <c>micLive</c> is carried for §5.5's evaluation on the <em>peer's</em>
    /// machine; it never relaxes ours, because §5.5's override reads <c>selfMicLive</c>.
    /// </summary>
    [Fact]
    public void A_peers_live_microphone_does_not_stop_us_muting()
    {
        ReducerHarness harness = ReducerHarness.For();

        harness.PeerState(harness.Peer, 3, micLive: true);

        Assert.True(harness.ShouldMute);
    }

    [Fact]
    public void Seq_saturates_rather_than_wrapping_at_the_top_of_the_range()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), ulong.MaxValue)
            .Apply(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.Equal(ulong.MaxValue, harness.State.Seq);
    }
}
