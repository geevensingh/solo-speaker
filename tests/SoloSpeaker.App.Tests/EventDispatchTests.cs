using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.App.Tests;

public sealed class EventDispatchTests
{
    [Fact]
    public void Posted_events_reach_the_loop_only_after_the_dispatch_drains()
    {
        AppArbitrationFixture fixture = AppArbitrationFixture.Create();

        fixture.Dispatch.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.True(fixture.Loop.State.ActiveOwner.IsNone);
        Assert.Empty(fixture.Transport.Sent);

        fixture.Dispatch.Drain(TimeSpan.FromSeconds(1));

        Assert.Equal(fixture.Config.Roster.Self, fixture.Loop.State.ActiveOwner);
        Assert.Single(fixture.Transport.Sent);
    }

    /// <summary>
    /// DepartureAnnouncer depends on this FIFO guarantee: byes queued after a claim must see
    /// and carry the claim, not the state that existed when shutdown began.
    /// </summary>
    [Fact]
    public void A_post_followed_by_postwork_runs_in_fifo_order()
    {
        AppArbitrationFixture fixture = AppArbitrationFixture.Create();
        Core.Identity.MachineId observedOwner = Core.Identity.MachineId.None;

        fixture.Dispatch.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));
        fixture.Dispatch.PostWork(() => observedOwner = fixture.Dispatch.Loop.State.ActiveOwner);

        fixture.Dispatch.Drain(TimeSpan.FromSeconds(1));

        Assert.Equal(fixture.Config.Roster.Self, observedOwner);
    }

    /// <summary>
    /// The transport reuses receive buffers. EventDispatch must own a private byte copy, or
    /// a queued datagram can change under the ingress parser before the dispatch drains it.
    /// </summary>
    [Fact]
    public void Receive_copies_the_datagram_before_queuing_it()
    {
        AppArbitrationFixture fixture = AppArbitrationFixture.Create();
        byte[] datagram = AppTestIds.DatagramFromPeer(AppTestIds.Peer, seq: 1);

        fixture.Dispatch.Receive(datagram);
        Array.Clear(datagram);

        fixture.Dispatch.Drain(TimeSpan.FromSeconds(1));

        Assert.True(fixture.Loop.State.HasAcceptedPeerState);
        Assert.Equal(1UL, fixture.Loop.State.LastAcceptedSeq);
        Assert.Equal(AppTestIds.Peer, fixture.Loop.State.LastAcceptedOwner);
    }
}
