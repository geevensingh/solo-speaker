using SoloSpeaker.App.Hosting;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.App.Tests;

/// <summary>
/// §7.1's departure ordering, exercised without a socket.
/// </summary>
/// <remarks>
/// The ordering is the only part of a departure that can be got wrong silently. A peer that
/// receives the <c>bye</c>s before the final state datagram clears presence and then has it
/// re-established by the late state datagram under §7.1's re-acquisition rule - so it mutes
/// for a machine that has already gone, and stays muted for the full presence window.
/// </remarks>
public sealed class DepartureOrderTests
{
    /// <summary>
    /// Manual row A12: a claim queued just before shutdown must reach the peer before the
    /// departure byes. The assertion is deliberately about <em>order</em> and not about how
    /// many datagrams were sent - a redundant state datagram is harmless, because §5.4 only
    /// acts on news, whereas an inverted order is the A12 defect.
    /// </summary>
    [Fact]
    public void A_queued_claim_is_broadcast_before_the_three_departure_byes()
    {
        AppArbitrationFixture fixture = AppArbitrationFixture.Create();
        var announcer = new DepartureAnnouncer(fixture.Dispatch, fixture.Executor);

        fixture.Dispatch.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));
        announcer.Announce();
        fixture.Dispatch.Drain(TimeSpan.FromSeconds(2));

        PeerDatagram[] datagrams = [.. fixture.Transport.Sent.Select(sent => AppTestIds.Decode(sent))];

        // The tail is exactly three byes...
        PeerDatagram[] byes = [.. datagrams.TakeLast(3)];
        Assert.All(byes, bye => Assert.True(bye.Bye));

        // ...and nothing before them is one, so no bye can have preceded a state datagram.
        Assert.All(datagrams.SkipLast(3), datagram => Assert.False(datagram.Bye));

        // The state datagram immediately before the byes carries the claim, not the state
        // as it was when shutdown began.
        PeerDatagram final = datagrams[^4];
        Assert.False(final.Bye);
        Assert.Equal(fixture.Config.Roster.Self, final.ActiveOwner);
        Assert.Equal(1UL, final.Seq);

        Assert.All(byes, bye =>
        {
            Assert.Equal(final.ActiveOwner, bye.ActiveOwner);
            Assert.Equal(final.Seq, bye.Seq);
        });
    }

    /// <summary>
    /// The departure still leads with a state datagram when nothing was queued ahead of it.
    /// </summary>
    /// <remarks>
    /// This is the path that a suppress-the-duplicate optimisation breaks. §7.1's guarantee
    /// is that a state datagram precedes the byes on <em>every</em> departure, not only on
    /// the ones where a claim happened to share the queue.
    /// </remarks>
    [Fact]
    public void A_departure_with_nothing_queued_still_sends_its_state_before_the_byes()
    {
        AppArbitrationFixture fixture = AppArbitrationFixture.Create();
        var announcer = new DepartureAnnouncer(fixture.Dispatch, fixture.Executor);

        announcer.Announce();
        fixture.Dispatch.Drain(TimeSpan.FromSeconds(2));

        PeerDatagram[] datagrams = [.. fixture.Transport.Sent.Select(sent => AppTestIds.Decode(sent))];

        Assert.Equal(4, datagrams.Length);
        Assert.False(datagrams[0].Bye);
        Assert.All(datagrams.Skip(1), bye => Assert.True(bye.Bye));
    }
}
