using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §7.1's presence rules, including the three things an accepted <c>bye</c> does and the
/// one thing it must not enable.
/// </summary>
public sealed class PresenceTests
{
    [Fact]
    public void An_accepted_datagram_establishes_presence()
    {
        ReducerHarness harness = ReducerHarness.For();

        harness.PeerState(harness.Peer, 1);

        Assert.True(harness.PeerPresent);
    }

    /// <summary>§7.1 receipt rule 2: a departure clears presence at once, not after the window.</summary>
    [Fact]
    public void A_departure_clears_presence_immediately()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 4);

        Assert.True(harness.ShouldMute);

        harness.PeerBye();

        Assert.False(harness.PeerPresent);
        Assert.False(harness.ShouldMute);
        Assert.Equal(TrayState.Alone, harness.Tray);
    }

    /// <summary>
    /// Design revision 7. A replayed state datagram carries <c>activeOwner = peer</c> - the
    /// one value §5.5 is looking for - so an echo would otherwise undo the departure and
    /// hold us muted for a machine that has gone. §7.1's claim that "the blast radius is
    /// your own speaker" does not bound this case.
    /// </summary>
    [Fact]
    public void A_replayed_datagram_after_a_departure_does_not_re_establish_presence()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 4);
        harness.PeerBye();

        for (int replay = 0; replay < 20; replay++)
        {
            harness.Apply(TimeSpan.FromSeconds(2), new ArbitrationEvent.PeerStateReceived(harness.Peer, 4, false));
            Assert.False(harness.PeerPresent);
            Assert.False(harness.ShouldMute);
        }
    }

    /// <summary>A departure is undone by news, not by an echo.</summary>
    [Fact]
    public void A_datagram_carrying_new_information_does_re_establish_presence()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 4);
        harness.PeerBye();

        harness.Apply(TimeSpan.FromSeconds(2), new ArbitrationEvent.PeerStateReceived(harness.Peer, 5, false));

        Assert.True(harness.PeerPresent);
        Assert.True(harness.ShouldMute);
    }

    /// <summary>A different owner at the same <c>seq</c> is news too.</summary>
    [Fact]
    public void A_changed_owner_at_the_same_seq_re_establishes_presence()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(MachineId.None, 4);
        harness.PeerBye();

        harness.Apply(TimeSpan.FromSeconds(2), new ArbitrationEvent.PeerStateReceived(harness.Peer, 4, false));

        Assert.True(harness.PeerPresent);
    }

    /// <summary>
    /// In steady state the peer repeats the same pair every beat, so the "new information"
    /// rule must apply only after a departure - otherwise presence would lapse during
    /// ordinary operation.
    /// </summary>
    [Fact]
    public void Repeated_identical_datagrams_keep_presence_alive_when_no_departure_occurred()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 4);

        for (int beat = 0; beat < 30; beat++)
        {
            harness.Apply(TimeSpan.FromSeconds(2), new ArbitrationEvent.PeerStateReceived(harness.Peer, 4, false));
            Assert.True(harness.PeerPresent);
        }
    }

    [Fact]
    public void Presence_lapses_after_five_missed_beats()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 4);

        harness.Apply(TimeSpan.FromSeconds(10), new ArbitrationEvent.Tick());
        Assert.True(harness.PeerPresent);

        harness.Apply(TimeSpan.FromSeconds(0.002), new ArbitrationEvent.Tick());
        Assert.False(harness.PeerPresent);
    }

    /// <summary>§7.1: a departure "does not clear the §7.6 quarantine observation latch".</summary>
    [Fact]
    public void A_departure_leaves_the_quarantine_latch_alone()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine();
        harness.PeerState(harness.Peer, 4);

        harness.PeerBye();

        Assert.True(harness.State.Quarantine!.PeerObserved);
    }
}
