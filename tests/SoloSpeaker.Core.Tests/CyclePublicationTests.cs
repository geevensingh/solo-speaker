using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.Fakes;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

public sealed class CyclePublicationTests
{
    /// <summary>
    /// Publishing from inside the effect executor would either log the pre-error reduction
    /// or log twice. A failed state write is one of the highest-consequence facts the
    /// diagnostic stream can report, so the loop must publish once after the re-entry.
    /// </summary>
    [Fact]
    public void One_post_publishes_once_even_when_a_reduction_faults_and_re_enters_error_raised()
    {
        LoopFixture fixture = LoopFixture.Create();
        fixture.StateStore.FailNextWrite = true;
        List<CycleObservation> observations = [];
        fixture.Loop.CyclePublished += observations.Add;

        fixture.Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.Single(observations);
    }

    [Fact]
    public void A_faulting_reduction_publishes_the_post_error_result_values()
    {
        LoopFixture fixture = LoopFixture.Create();
        fixture.StateStore.FailNextWrite = true;
        CycleObservation? observation = null;
        fixture.Loop.CyclePublished += published => observation = published;

        fixture.Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.NotNull(observation);
        Assert.Equal(ErrorCause.StatePersistFailed, observation.Value.Result.ErrorCause);
        Assert.Equal(TrayState.Error, observation.Value.Result.TrayState);
    }

    [Fact]
    public void Previous_state_is_the_state_before_the_reduction_so_transitions_are_detectable()
    {
        LoopFixture fixture = LoopFixture.Create();
        CycleObservation? observation = null;
        fixture.Loop.CyclePublished += published => observation = published;

        fixture.Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.NotNull(observation);
        Assert.True(observation.Value.PreviousState.ActiveOwner.IsNone);
        Assert.Equal(fixture.Config.Roster.Self, observation.Value.Result.State.ActiveOwner);
    }

    /// <summary>
    /// Diagnostics are not allowed to kill the message loop. A throwing subscriber must not
    /// prevent the state write and in-memory transition the cycle has already completed.
    /// </summary>
    [Fact]
    public void A_throwing_subscriber_does_not_fault_the_cycle_or_prevent_the_state_update()
    {
        LoopFixture fixture = LoopFixture.Create();
        fixture.Loop.CyclePublished += _ => throw new InvalidOperationException("Subscriber failed.");

        ReducerResult result = fixture.Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.Equal(fixture.Config.Roster.Self, fixture.Loop.State.ActiveOwner);
        Assert.Equal(fixture.Config.Roster.Self, result.State.ActiveOwner);
        Assert.Equal(1, fixture.StateStore.SaveCount);
    }

    [Fact]
    public void Receive_with_a_datagram_that_reduces_to_nothing_observes_ingress_without_publishing_a_cycle()
    {
        LoopFixture fixture = LoopFixture.Create();
        List<IngressResult> observedIngress = [];
        int publishedCycles = 0;
        fixture.Loop.IngressObserved += observedIngress.Add;
        fixture.Loop.CyclePublished += _ => publishedCycles++;

        byte[] unreadable = [0xff];

        IngressResult result = fixture.Loop.Receive(unreadable);

        Assert.Equal(IngressResult.Unreadable, result);
        Assert.Equal(IngressResult.Unreadable, Assert.Single(observedIngress));
        Assert.Equal(0, publishedCycles);
    }

    [Fact]
    public void Receive_with_an_accepted_datagram_publishes_once_and_carries_the_ingress_verdict()
    {
        LoopFixture fixture = LoopFixture.Create();
        List<CycleObservation> observations = [];
        fixture.Loop.CyclePublished += observations.Add;

        IngressResult result = fixture.Loop.Receive(PeerDatagramBytes(activeOwner: WireVectorConstants.MachineB, seq: 1));

        CycleObservation observation = Assert.Single(observations);
        Assert.Equal(IngressResult.Accepted, result);
        Assert.Equal(IngressResult.Accepted, observation.Ingress);
    }

    private static byte[] PeerDatagramBytes(MachineId activeOwner, ulong seq)
    {
        var datagram = new PeerDatagram(
            WireProtocol.Version,
            WireVectorConstants.PairId,
            WireVectorConstants.MachineB,
            seq,
            activeOwner,
            MicLive: false,
            Bye: false,
            WireVectorConstants.ParseTimestamp(WireVectorConstants.BaselineSentUtc));

        byte[] buffer = new byte[WireProtocol.MaxDatagramBytes];
        int length = DatagramCodec.Encode(datagram, WireVectorConstants.TestPairKey, buffer);

        return buffer[..length];
    }

    private sealed class LoopFixture
    {
        private LoopFixture(
            FakeConfigStore config,
            FakeStateStore stateStore,
            ArbitrationLoop loop)
        {
            Config = config;
            StateStore = stateStore;
            Loop = loop;
        }

        internal FakeConfigStore Config { get; }

        internal FakeStateStore StateStore { get; }

        internal ArbitrationLoop Loop { get; }

        internal static LoopFixture Create()
        {
            var clock = new FakeClock();
            var config = new FakeConfigStore(
                WireVectorConstants.PairId,
                CompleteRoster(),
                WireVectorConstants.TestPairKey);
            var stateStore = new FakeStateStore();
            var transport = new RecordingTransport();
            var executor = new EffectExecutor(clock, config, stateStore, transport);
            var loop = new ArbitrationLoop(clock, config, executor);

            return new LoopFixture(config, stateStore, loop);
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

    private sealed class RecordingTransport : IPeerTransport
    {
        public event Action<ReadOnlyMemory<byte>>? DatagramReceived;

        public event Action? NetworkChanged;

        public bool IsBound => true;

        public void Send(ReadOnlyMemory<byte> datagram)
        {
            Sent.Add(datagram.ToArray());
        }

        internal List<byte[]> Sent { get; } = [];

        internal void RaiseDatagram(ReadOnlyMemory<byte> datagram) => DatagramReceived?.Invoke(datagram);

        internal void RaiseNetworkChanged() => NetworkChanged?.Invoke();
    }
}
