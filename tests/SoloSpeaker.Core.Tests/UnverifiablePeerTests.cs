using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §7.1's unverifiable-peer producer - the rate signal that replaced the per-datagram
/// version check revisions 3 and 4 specified and that could never fire.
/// </summary>
/// <remarks>
/// Row 2 shipped <see cref="IngressResult.BadMac"/> as a reason distinguishable from
/// <see cref="IngressResult.ForeignPairId"/> precisely so this could exist: foreign traffic
/// dies at step 1, while a version-mismatched peer dies at step 2 <em>having passed step
/// 1</em>.
/// </remarks>
public sealed class UnverifiablePeerTests
{
    private static ReducerHarness Unverifiable(ReducerHarness harness, int count, double secondsApart = 2)
    {
        for (int index = 0; index < count; index++)
        {
            harness.Apply(
                TimeSpan.FromSeconds(secondsApart),
                new ArbitrationEvent.PeerDatagramRejected(IngressResult.BadMac));
        }

        return harness;
    }

    [Fact]
    public void Three_unverifiable_datagrams_with_nothing_valid_accepted_raise_error()
    {
        ReducerHarness harness = Unverifiable(ReducerHarness.For(), 3);

        Assert.Equal(ErrorCause.PeerUnverifiable, harness.Error);
        Assert.Equal(TrayState.Error, harness.Tray);
    }

    [Fact]
    public void Two_are_not_sustained()
    {
        ReducerHarness harness = Unverifiable(ReducerHarness.For(), 2);

        Assert.Equal(ErrorCause.None, harness.Error);
        Assert.NotEqual(TrayState.Error, harness.Tray);
    }

    /// <summary>
    /// §7.1's second clause is load-bearing: <c>pairId</c> is public, so without it any
    /// stranger could pin the tray into a sticky error and destroy the only visible
    /// explanation for why a machine is silent.
    /// </summary>
    [Fact]
    public void The_same_traffic_raises_nothing_while_valid_datagrams_are_still_arriving()
    {
        ReducerHarness harness = ReducerHarness.For();

        for (int round = 0; round < 10; round++)
        {
            harness.PeerState(harness.Peer, (ulong)round + 1);
            Unverifiable(harness, 4, secondsApart: 0.2);

            Assert.NotEqual(ErrorCause.PeerUnverifiable, harness.Error);
        }
    }

    /// <summary>
    /// Sliding, not tumbling. A tumbling counter resets on roll, so four-then-four across a
    /// boundary would never trip a three-in-ten rule.
    /// </summary>
    [Fact]
    public void The_window_slides_rather_than_tumbling()
    {
        ReducerHarness harness = ReducerHarness.For();

        // Two, then a long gap that ages them out, then two more: never three in any window.
        Unverifiable(harness, 2, secondsApart: 1);
        harness.Advance(TimeSpan.FromSeconds(30));
        Unverifiable(harness, 2, secondsApart: 1);

        Assert.Equal(ErrorCause.None, harness.Error);

        // A third inside the same window does trip it.
        Unverifiable(harness, 1, secondsApart: 1);

        Assert.Equal(ErrorCause.PeerUnverifiable, harness.Error);
    }

    /// <summary>An acceptance resets the count rather than merely ageing it out.</summary>
    [Fact]
    public void Accepting_a_datagram_resets_the_count()
    {
        ReducerHarness harness = ReducerHarness.For();

        Unverifiable(harness, 2, secondsApart: 0.5);
        harness.PeerState(harness.Peer, 1);
        Unverifiable(harness, 2, secondsApart: 0.5);

        Assert.Equal(ErrorCause.None, harness.Error);
    }

    /// <summary>
    /// Only <c>pairId</c>-matching, <c>mac</c>-failing traffic is evidence. Foreign traffic
    /// dies at step 1 and means nothing.
    /// </summary>
    [Theory]
    [InlineData(IngressResult.ForeignPairId)]
    [InlineData(IngressResult.Unreadable)]
    [InlineData(IngressResult.SelfOrigin)]
    [InlineData(IngressResult.NotInRoster)]
    public void Other_rejection_reasons_do_not_feed_the_rate_signal(IngressResult reason)
    {
        ReducerHarness harness = ReducerHarness.For();

        for (int index = 0; index < 20; index++)
        {
            harness.Apply(TimeSpan.FromSeconds(1), new ArbitrationEvent.PeerDatagramRejected(reason));
        }

        Assert.Equal(ErrorCause.None, harness.Error);
    }

    /// <summary>§7.1 steps 4 and 5 are loud per datagram, not in aggregate.</summary>
    [Theory]
    [InlineData(IngressResult.SeqOutOfBounds, ErrorCause.SeqBoundExceeded)]
    [InlineData(IngressResult.UnknownVersion, ErrorCause.UnknownWireVersion)]
    public void The_loud_ingress_steps_raise_on_a_single_datagram(IngressResult reason, ErrorCause expected)
    {
        ReducerHarness harness = ReducerHarness.For()
            .Apply(new ArbitrationEvent.PeerDatagramRejected(reason));

        Assert.Equal(expected, harness.Error);
        Assert.Equal(TrayState.Error, harness.Tray);
    }

    /// <summary>§7.4: sticky until acknowledged.</summary>
    [Fact]
    public void An_edge_error_is_sticky_until_acknowledged()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Apply(new ArbitrationEvent.PeerDatagramRejected(IngressResult.SeqOutOfBounds));

        harness.Apply(TimeSpan.FromMinutes(5), new ArbitrationEvent.Tick());
        Assert.Equal(ErrorCause.SeqBoundExceeded, harness.Error);

        harness.Apply(new ArbitrationEvent.ErrorAcknowledged());
        Assert.Equal(ErrorCause.None, harness.Error);
    }

    /// <summary>§7.4: the first cause raised is the one the tooltip names.</summary>
    [Fact]
    public void The_first_cause_raised_is_the_one_reported()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Apply(new ArbitrationEvent.ErrorRaised(ErrorCause.HotkeyRegistrationFailed))
            .Apply(new ArbitrationEvent.PeerDatagramRejected(IngressResult.SeqOutOfBounds));

        Assert.Equal(ErrorCause.HotkeyRegistrationFailed, harness.Error);
    }
}
