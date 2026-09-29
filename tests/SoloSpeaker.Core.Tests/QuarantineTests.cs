using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §7.6's rejoin quarantine - the window, the observation latch, and adopt-or-resume at
/// expiry. The logic lives here; row 11 adds the edges that start the window.
/// </summary>
public sealed class QuarantineTests
{
    private static readonly TimeSpan PastExpiry = TimeSpan.FromSeconds(12.001);

    /// <summary>
    /// §10: "quarantine: peer observed -> adopt peer's owner even when our `seq` is higher."
    /// This is exactly the case §5.4 says to ignore, which is why the window records the
    /// observed pair rather than relying on convergence.
    /// </summary>
    [Fact]
    public void Adopts_the_observed_owner_at_expiry_even_when_our_seq_is_higher()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), 500)
            .EnterQuarantine();

        harness.PeerState(harness.Peer, 7);

        // Still ours during the window: observation is not adoption.
        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.Equal(500UL, harness.State.Seq);

        harness.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
        Assert.True(harness.State.Seq > 500UL);
        Assert.Null(harness.State.Quarantine);
    }

    /// <summary>
    /// Design revision 7: a 12 s window at a 2 s beat admits six observations and the peer
    /// may legitimately claim within them, so the latest observation is the one adopted.
    /// </summary>
    [Fact]
    public void Records_the_observed_pair_last_writer_wins()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine();

        harness.PeerState(MachineId.None, 1);
        harness.Apply(TimeSpan.FromSeconds(2), new ArbitrationEvent.PeerStateReceived(harness.Peer, 2, false));

        harness.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
    }

    [Fact]
    public void Resumes_from_persisted_state_when_the_window_expires_unobserved()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), 42)
            .EnterQuarantine();

        harness.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.Equal(42UL, harness.State.Seq);
        Assert.Null(harness.State.Quarantine);
    }

    /// <summary>
    /// Design revision 7: a quarantined machine sends nothing. Broadcasting
    /// <c>activeOwner = None</c> would let the peer adopt it and erase ownership by opening
    /// a lid; broadcasting the persisted pair would mute the peer outright.
    /// </summary>
    [Fact]
    public void Broadcasts_nothing_at_all_while_quarantined()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), 500)
            .EnterQuarantine();

        for (int beat = 0; beat < 8; beat++)
        {
            harness.Apply(TimeSpan.FromSeconds(1), new ArbitrationEvent.Tick());
            Assert.Empty(harness.Broadcasts);
        }
    }

    [Fact]
    public void Resumes_broadcasting_once_the_window_expires()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine();

        harness.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.NotEmpty(harness.Broadcasts);
    }

    /// <summary>§7.6: "Live events are never suppressed."</summary>
    [Fact]
    public void A_manual_claim_exits_quarantine_immediately_and_writes_normally()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine().Claim();

        Assert.Null(harness.State.Quarantine);
        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.NotEmpty(harness.Persists);
        Assert.NotEmpty(harness.Broadcasts);
    }

    [Fact]
    public void A_mic_edge_exits_quarantine_immediately_and_writes_normally()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine().MicEdge();

        Assert.Null(harness.State.Quarantine);
        Assert.Equal(harness.Self, harness.State.ActiveOwner);
        Assert.NotEmpty(harness.Broadcasts);
    }

    /// <summary>
    /// §7.6: "nothing clears it for the remainder of the window - in particular a `bye`
    /// cannot." This is the lid-open defect revision 4 was written to close.
    /// </summary>
    [Fact]
    public void A_departure_does_not_clear_the_observation_latch()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), 500)
            .EnterQuarantine();

        harness.PeerState(harness.Peer, 7);
        harness.PeerBye();

        Assert.True(harness.State.Quarantine!.PeerObserved);

        harness.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
    }

    /// <summary>
    /// §10's hostile row: "a sustained replayed-`bye` flood during a rejoin window... must
    /// not prevent the quarantine latch from being honoured at expiry."
    /// </summary>
    [Fact]
    public void A_flood_of_departures_does_not_prevent_the_latch_being_honoured()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), 500)
            .EnterQuarantine();

        harness.PeerState(harness.Peer, 7);

        for (int replay = 0; replay < 50; replay++)
        {
            harness.PeerBye();
        }

        harness.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
    }

    /// <summary>
    /// Design revision 7: a restart begins a new window but preserves the latch, so a brief
    /// network transition during a legitimate rejoin cannot erase a correct observation.
    /// </summary>
    [Fact]
    public void A_restart_preserves_the_observation_latch_and_the_observed_pair()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), 500)
            .EnterQuarantine();

        harness.PeerState(harness.Peer, 7);
        harness.Apply(TimeSpan.FromSeconds(3), new ArbitrationEvent.QuarantineRestarted());

        Assert.True(harness.State.Quarantine!.PeerObserved);
        Assert.Equal(harness.Peer, harness.State.Quarantine.ObservedOwner);

        harness.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.Equal(harness.Peer, harness.State.ActiveOwner);
    }

    /// <summary>A restart extends the window rather than expiring on the original start.</summary>
    [Fact]
    public void A_restart_extends_the_window()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine();

        harness.Apply(TimeSpan.FromSeconds(10), new ArbitrationEvent.QuarantineRestarted());
        harness.Apply(TimeSpan.FromSeconds(5), new ArbitrationEvent.Tick());

        Assert.NotNull(harness.State.Quarantine);

        harness.Apply(TimeSpan.FromSeconds(8), new ArbitrationEvent.Tick());

        Assert.Null(harness.State.Quarantine);
    }

    /// <summary>
    /// Simultaneous rejoin: both machines quarantined, both audible, converging when the
    /// windows expire.
    /// </summary>
    [Fact]
    public void Simultaneous_rejoin_leaves_both_machines_audible()
    {
        ReducerHarness fromA = ReducerHarness.For(ReducerHarness.AHex, ReducerHarness.BHex)
            .Persisted(ReducerHarness.Id(ReducerHarness.BHex), 9)
            .EnterQuarantine();
        ReducerHarness fromB = ReducerHarness.For(ReducerHarness.BHex, ReducerHarness.AHex)
            .Persisted(ReducerHarness.Id(ReducerHarness.BHex), 9)
            .EnterQuarantine();

        Assert.False(fromA.ShouldMute);
        Assert.False(fromB.ShouldMute);

        fromA.Apply(PastExpiry, new ArbitrationEvent.Tick());
        fromB.Apply(PastExpiry, new ArbitrationEvent.Tick());

        Assert.Equal(fromA.State.ActiveOwner, fromB.State.ActiveOwner);
    }

    [Fact]
    public void Entering_quarantine_twice_does_not_restart_the_window()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine();
        TimeSpan started = harness.State.Quarantine!.StartedAt;

        harness.Apply(TimeSpan.FromSeconds(4), new ArbitrationEvent.QuarantineEntered());

        Assert.Equal(started, harness.State.Quarantine!.StartedAt);
    }
}
