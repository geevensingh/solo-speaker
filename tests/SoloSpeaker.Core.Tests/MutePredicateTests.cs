using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §5.5's mute predicate, stated in positive space: a machine mutes only when it can
/// affirmatively name the peer as owner.
/// </summary>
public sealed class MutePredicateTests
{
    [Fact]
    public void Mutes_when_the_peer_is_present_and_owns_the_latch()
    {
        ReducerHarness harness = ReducerHarness.For();

        harness.PeerState(harness.Peer, 4);

        Assert.True(harness.ShouldMute);
        Assert.Equal(TrayState.Muted, harness.Tray);
    }

    /// <summary>§5.5: "No peer -> never muted, regardless of `activeOwner`."</summary>
    [Fact]
    public void Never_mutes_without_a_peer_whatever_activeOwner_says()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.BHex), 12)
            .Tick();

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
        Assert.False(harness.ShouldMute);
        Assert.Equal(TrayState.Alone, harness.Tray);
    }

    /// <summary>
    /// §5.5: an owner that is corrupt, unknown, or names a retired machine leaves the
    /// predicate false on <em>both</em> machines, so Goal 1 holds by construction.
    /// </summary>
    [Fact]
    public void An_owner_outside_the_roster_leaves_both_machines_audible_and_raises_error()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.StrangerHex), 12);

        harness.PeerState(harness.Peer, 3);

        Assert.False(harness.ShouldMute);
        Assert.Equal(TrayState.Error, harness.Tray);
        Assert.Equal(ErrorCause.ActiveOwnerOutsideRoster, harness.Error);
    }

    /// <summary>
    /// §5.5's safety override. Phase 1 cannot reach this - §8 hardcodes <c>micLive</c>
    /// false - but the predicate is written for phase 2 and is tested now, because §8
    /// freezes the field on the wire in phase 1.
    /// </summary>
    [Fact]
    public void Never_mutes_while_this_machine_is_capturing_audio()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 4);

        Assert.True(harness.ShouldMute);

        harness.Apply(new ArbitrationEvent.SelfMicChanged(true));

        Assert.False(harness.ShouldMute);
    }

    /// <summary>§5.5: "The override only ever relaxes muting."</summary>
    [Fact]
    public void The_safety_override_never_causes_a_mute_that_would_not_otherwise_occur()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        harness.Apply(new ArbitrationEvent.SelfMicChanged(false));

        Assert.False(harness.ShouldMute);
    }

    /// <summary>The pre-claim owner of §5 names no machine, so it can never satisfy the predicate.</summary>
    [Fact]
    public void Never_mutes_before_anyone_has_claimed()
    {
        ReducerHarness harness = ReducerHarness.For();

        harness.PeerState(MachineId.None, 1);

        Assert.True(harness.State.ActiveOwner.IsNone);
        Assert.False(harness.ShouldMute);
        Assert.Equal(TrayState.Unclaimed, harness.Tray);
    }

    /// <summary>An incomplete roster has no right-hand side for the predicate.</summary>
    [Fact]
    public void Never_mutes_while_the_roster_is_incomplete()
    {
        ReducerHarness harness = ReducerHarness.For(peerHex: null);

        harness.PeerState(ReducerHarness.Id(ReducerHarness.BHex), 9);

        Assert.False(harness.ShouldMute);
    }

    /// <summary>§7.6: a quarantined machine "does not apply mute".</summary>
    [Fact]
    public void Never_mutes_while_quarantined()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine();

        harness.PeerState(harness.Peer, 9);

        Assert.False(harness.ShouldMute);
        Assert.Equal(TrayState.Quarantine, harness.Tray);
    }

    /// <summary>§7.1's presence window is five missed beats, and the boundary is inclusive.</summary>
    [Theory]
    [InlineData(9.999, true)]
    [InlineData(10.0, true)]
    [InlineData(10.001, false)]
    public void The_presence_window_boundary_is_ten_seconds(double elapsedSeconds, bool expectedMuted)
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 4);

        harness.Apply(TimeSpan.FromSeconds(elapsedSeconds), new ArbitrationEvent.Tick());

        Assert.Equal(expectedMuted, harness.ShouldMute);
    }
}
