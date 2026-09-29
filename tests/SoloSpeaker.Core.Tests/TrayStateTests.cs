using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §7.4's tray states and their producers.
/// </summary>
/// <remarks>
/// <c>implementation-plan.md</c> §4.6 asks for two separate things: that every
/// <see cref="TrayState"/> has at least one producer, and that each maps to a distinct icon
/// resource. Only the second needs assets, so only the second waits for row 9. The first is
/// a property of the reducer's tray computation, and it is the test that would have caught
/// revision 6 leaving the table non-total.
/// </remarks>
public sealed class TrayStateTests
{
    [Fact]
    public void Active_when_this_machine_holds_the_latch()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        Assert.Equal(TrayState.Active, harness.Tray);
    }

    [Fact]
    public void Muted_when_the_predicate_is_true()
    {
        ReducerHarness harness = ReducerHarness.For();
        harness.PeerState(harness.Peer, 3);

        Assert.Equal(TrayState.Muted, harness.Tray);
    }

    [Fact]
    public void Alone_when_no_peer_heartbeat_arrived_within_the_window()
    {
        ReducerHarness harness = ReducerHarness.For().Tick();

        Assert.Equal(TrayState.Alone, harness.Tray);
    }

    [Fact]
    public void Quarantine_during_the_rejoin_window()
    {
        ReducerHarness harness = ReducerHarness.For().EnterQuarantine();

        Assert.Equal(TrayState.Quarantine, harness.Tray);
    }

    [Fact]
    public void Error_when_the_owner_is_outside_the_roster()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.StrangerHex), 3)
            .Tick();

        Assert.Equal(TrayState.Error, harness.Tray);
    }

    /// <summary>
    /// The hole design revision 6 left and revision 7 closed: with the reserved pre-claim
    /// owner and a peer present, all five original states were false by their own
    /// definitions.
    /// </summary>
    [Fact]
    public void Unclaimed_when_nobody_has_claimed_and_a_peer_is_present()
    {
        ReducerHarness harness = ReducerHarness.For();

        harness.PeerState(MachineId.None, 1);

        Assert.Equal(TrayState.Unclaimed, harness.Tray);
        Assert.False(harness.ShouldMute);
        Assert.Equal(ErrorCause.None, harness.Error);
    }

    /// <summary>
    /// With no peer, "nobody has claimed" is indistinguishable from "there is nothing to
    /// arbitrate with", and <c>alone</c> is the honest one.
    /// </summary>
    [Fact]
    public void Alone_rather_than_unclaimed_when_nobody_has_claimed_and_no_peer_is_present()
    {
        ReducerHarness harness = ReducerHarness.For().Tick();

        Assert.True(harness.State.ActiveOwner.IsNone);
        Assert.Equal(TrayState.Alone, harness.Tray);
    }

    /// <summary>
    /// §7.4's table must stay a complete enumeration. Revision 1 listed a state nothing
    /// raised; revision 6 left one no producer could reach. This asserts the other
    /// direction - every value is reachable from the reducer.
    /// </summary>
    [Fact]
    public void Every_tray_state_has_a_producer()
    {
        Dictionary<TrayState, Func<ReducerHarness>> producers = new()
        {
            [TrayState.Active] = () => ReducerHarness.For().Claim(),
            [TrayState.Muted] = () =>
            {
                ReducerHarness harness = ReducerHarness.For();
                return harness.PeerState(harness.Peer, 3);
            },
            [TrayState.Unclaimed] = () => ReducerHarness.For().PeerState(MachineId.None, 1),
            [TrayState.Alone] = () => ReducerHarness.For().Tick(),
            [TrayState.Quarantine] = () => ReducerHarness.For().EnterQuarantine(),
            [TrayState.Error] = () => ReducerHarness.For()
                .Apply(new ArbitrationEvent.ErrorRaised(ErrorCause.HotkeyRegistrationFailed)),
        };

        Assert.Equal(Enum.GetValues<TrayState>().Order().ToArray(), producers.Keys.Order().ToArray());

        foreach ((TrayState expected, Func<ReducerHarness> produce) in producers)
        {
            Assert.Equal(expected, produce().Tray);
        }
    }

    /// <summary>
    /// The tray must be a total function: for any reachable combination of owner, presence,
    /// quarantine, microphone and error, exactly one state comes out. Revision 6's defect
    /// was a combination that produced none.
    /// </summary>
    [Fact]
    public void The_tray_state_is_total_over_every_combination_of_inputs()
    {
        MachineId[] owners =
        [
            MachineId.None,
            ReducerHarness.Id(ReducerHarness.AHex),
            ReducerHarness.Id(ReducerHarness.BHex),
            ReducerHarness.Id(ReducerHarness.StrangerHex),
        ];

        foreach (MachineId owner in owners)
        {
            foreach (bool peerPresent in new[] { false, true })
            {
                foreach (bool quarantined in new[] { false, true })
                {
                    foreach (bool micLive in new[] { false, true })
                    {
                        ReducerHarness harness = ReducerHarness.For().Persisted(owner, 3);

                        if (quarantined)
                        {
                            harness.EnterQuarantine();
                        }

                        harness.Apply(new ArbitrationEvent.SelfMicChanged(micLive));

                        if (peerPresent)
                        {
                            harness.Apply(new ArbitrationEvent.PeerStateReceived(owner, 3, false));
                        }
                        else
                        {
                            harness.Tick();
                        }

                        Assert.Contains(harness.Tray, Enum.GetValues<TrayState>());
                    }
                }
            }
        }
    }

    /// <summary>
    /// A continuous cause cannot be acknowledged away. Clearing it would drop the tray back
    /// to an ordinary state while the machine sits in exactly the condition the error
    /// exists to expose.
    /// </summary>
    [Fact]
    public void A_continuous_error_cause_survives_acknowledgement()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.StrangerHex), 3)
            .Tick();

        Assert.Equal(ErrorCause.ActiveOwnerOutsideRoster, harness.Error);

        harness.Apply(new ArbitrationEvent.ErrorAcknowledged());

        Assert.Equal(ErrorCause.ActiveOwnerOutsideRoster, harness.Error);
        Assert.Equal(TrayState.Error, harness.Tray);
    }

    /// <summary>...and it clears by itself once the condition stops being true.</summary>
    [Fact]
    public void A_continuous_error_cause_clears_when_the_condition_resolves()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.StrangerHex), 3)
            .Tick();

        Assert.Equal(TrayState.Error, harness.Tray);

        harness.Claim();

        Assert.Equal(ErrorCause.None, harness.Error);
        Assert.Equal(TrayState.Active, harness.Tray);
    }

    /// <summary>The reserved pre-claim owner is outside the roster, and is never an error.</summary>
    [Fact]
    public void The_pre_claim_owner_is_never_an_error_cause()
    {
        ReducerHarness harness = ReducerHarness.For().Tick();

        Assert.True(harness.State.ActiveOwner.IsNone);
        Assert.Equal(ErrorCause.None, harness.Error);
    }

    /// <summary>§7.1: the cadence is time-driven, and a state change does not wait for it.</summary>
    [Fact]
    public void The_heartbeat_beats_on_the_cadence_and_not_more_often()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        harness.Apply(TimeSpan.FromSeconds(1), new ArbitrationEvent.Tick());
        Assert.Empty(harness.Broadcasts);

        harness.Apply(TimeSpan.FromSeconds(1), new ArbitrationEvent.Tick());
        Assert.Single(harness.Broadcasts);
    }
}
