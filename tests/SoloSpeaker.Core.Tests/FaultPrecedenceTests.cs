using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.Ledger;
using SoloSpeaker.Core.MuteActuator;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.Fakes;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// A cycle has two fault channels - the effect drain and the actuation reconcile - and can
/// latch only one cause. These cover which one wins.
/// </summary>
/// <remarks>
/// <para>
/// The precedence was previously "whichever the effect drain produced", which discarded the
/// actuation fault whenever a persist also failed. Neither cause is continuous
/// (<see cref="ErrorCauseExtensions.IsContinuous"/>), so nothing re-raises the loser while
/// the winner keeps recurring, and the discarded one is exactly the one that can leave this
/// machine silent. No test asserted that ordering, which is why it survived as an accident
/// rather than as a decision.
/// </para>
/// <para>
/// These cannot live in the two-node harness: <see cref="EffectExecutor"/> takes its
/// reconciler optionally precisely so that harness can "exercise arbitration without an
/// audio device", so <c>Reconcile</c> there always returns
/// <see cref="ErrorCause.None"/>.
/// </para>
/// </remarks>
public sealed class FaultPrecedenceTests
{
    private const string LedgerPath = @"C:\root\ledger.json";
    private const string EndpointId = "endpoint-a";

    /// <summary>
    /// Goal 1's direction. A failed unmute leaves the machine silent; a failed
    /// <c>state.json</c> write leaves it audible with a stale record. §7.4 calls the tray
    /// "the only visible explanation for why a machine is silent", so the silent one wins.
    /// </summary>
    [Fact]
    public void A_failed_unmute_outranks_a_failed_persist_in_the_same_cycle()
    {
        LoopFixture fixture = LoopFixture.Create(actualMute: true);
        fixture.Actuator.SetSetMuteOutcome(EndpointId, MuteApplyOutcome.Failed);
        fixture.StateStore.FailNextWrite = true;

        // A claim both writes state - so the persist fault fires - and leaves this machine
        // the owner, so ShouldMute is false and the reconciler attempts the failing unmute.
        // A Tick would not do: it persists nothing, so only one channel would fault and the
        // test would pass under either precedence.
        fixture.Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.Equal(ErrorCause.UnmuteWriteFailed, fixture.Loop.LastResult.ErrorCause);
        Assert.Equal(TrayState.Error, fixture.Loop.LastResult.TrayState);
    }

    /// <summary>
    /// The pair the rank does <b>not</b> decide. A failed mute leaves the machine audible -
    /// §7.4's safe direction - and a stale `state.json` leaves it audible too, so both are
    /// `Impaired` and the audibility axis is silent. The outcome is the precedence this site
    /// shipped with, preferred by operand order rather than decided by the table, and step
    /// 3's set is where it gets settled. Without this case the two available mutations -
    /// inverting `Louder`'s comparison, or swapping its operands here - both leave the suite
    /// green.
    /// </summary>
    [Fact]
    public void A_failed_mute_outranks_a_failed_persist_in_the_same_cycle()
    {
        LoopFixture fixture = LoopFixture.Create(actualMute: false);
        fixture.Actuator.SetSetMuteOutcome(EndpointId, MuteApplyOutcome.Failed);
        fixture.StateStore.FailNextWrite = true;

        // The peer claiming both writes state - so the persist fault fires - and makes this
        // machine the non-owner, so ShouldMute is true and the reconciler attempts the
        // failing mute.
        fixture.Loop.Post(new ArbitrationEvent.PeerStateReceived(WireVectorConstants.MachineB, 9, false));

        Assert.Equal(ErrorCause.MuteWriteFailed, fixture.Loop.LastResult.ErrorCause);
    }

    /// <summary>
    /// The other actuation cause. §7.1's reconciler returns it before any write and without
    /// consulting <c>shouldMute</c>, so on a departed peer it means "should unmute, could
    /// not read the endpoint, took no action" - the machine stays silent.
    /// </summary>
    [Fact]
    public void An_unreadable_endpoint_outranks_a_failed_persist_in_the_same_cycle()
    {
        LoopFixture fixture = LoopFixture.Create(actualMute: true);
        fixture.Actuator.CurrentEndpointId = null;
        fixture.StateStore.FailNextWrite = true;

        fixture.Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.Equal(ErrorCause.EndpointEnumerationFailed, fixture.Loop.LastResult.ErrorCause);
        Assert.Equal(TrayState.Error, fixture.Loop.LastResult.TrayState);
    }

    /// <summary>
    /// The persist fault is not suppressed - it is only outranked. With actuation healthy it
    /// still reaches the tray, which is what §7.5 added it for.
    /// </summary>
    [Fact]
    public void A_failed_persist_still_wins_when_actuation_is_healthy()
    {
        LoopFixture fixture = LoopFixture.Create(actualMute: false);
        fixture.StateStore.FailNextWrite = true;

        fixture.Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.Equal(ErrorCause.StatePersistFailed, fixture.Loop.LastResult.ErrorCause);
        Assert.Equal(TrayState.Error, fixture.Loop.LastResult.TrayState);
    }

    private sealed class LoopFixture
    {
        private LoopFixture(FakeStateStore stateStore, FakeMuteActuator actuator, ArbitrationLoop loop)
        {
            StateStore = stateStore;
            Actuator = actuator;
            Loop = loop;
        }

        internal FakeStateStore StateStore { get; }

        internal FakeMuteActuator Actuator { get; }

        internal ArbitrationLoop Loop { get; }

        internal static LoopFixture Create(bool? actualMute)
        {
            var clock = new FakeClock();
            var config = new FakeConfigStore(
                WireVectorConstants.PairId,
                CompleteRoster(),
                WireVectorConstants.TestPairKey);
            var stateStore = new FakeStateStore();
            var transport = new SilentTransport();
            var files = new FakeFileStore();
            var ledger = new JsonLedger(files, LedgerPath, clock);
            var actuator = new FakeMuteActuator
            {
                CurrentEndpointId = EndpointId,
                ActualMute = actualMute,
            };
            var reconciler = new MuteReconciler(actuator, ledger, clock, ArbitrationTunables.Default);
            var executor = new EffectExecutor(clock, config, stateStore, transport, reconciler);
            var loop = new ArbitrationLoop(clock, config, executor);

            return new LoopFixture(stateStore, actuator, loop);
        }

        private static Roster CompleteRoster()
        {
            if (!Roster.TryCreate(WireVectorConstants.MachineA, WireVectorConstants.MachineB, out Roster? roster))
            {
                throw new InvalidOperationException("Test roster is invalid.");
            }

            return roster!;
        }
    }

    private sealed class SilentTransport : IPeerTransport
    {
        public event Action<ReadOnlyMemory<byte>>? DatagramReceived;

        public event Action? NetworkChanged;

        public bool IsBound => true;

        public void Send(ReadOnlyMemory<byte> datagram)
        {
            _ = DatagramReceived;
            _ = NetworkChanged;
        }
    }
}
