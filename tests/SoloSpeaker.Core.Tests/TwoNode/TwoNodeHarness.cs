using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.Fakes;
using SoloSpeaker.Core.Tests.StateMachine;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests.TwoNode;

/// <summary>
/// Two real composition cycles on one simulated subnet, driven by one controlled clock.
/// </summary>
/// <remarks>
/// <para>
/// Datagrams travel as <b>real canonical bytes</b> through the real codec and the real
/// ingress pipeline. Synthesising events instead would let the harness prove convergence over
/// a pipeline that was never run - the "passes for the wrong reason" failure ADR 0006 names
/// by hand - and it is the composition, where <c>IngressContext.LocalSeq</c> is bound to live
/// reducer state, that unit tests cannot reach.
/// </para>
/// <para>
/// Identities come from <see cref="ReducerHarness"/> so the fact that A is the
/// lexicographically <em>larger</em> ID - and therefore that B wins §5.4's tiebreak - has one
/// home. Two copies of that fact is how a "B wins" assertion goes green against identities
/// defined the other way round.
/// </para>
/// </remarks>
internal sealed class TwoNodeHarness
{
    private readonly FakeClock _clock = new();

    private TwoNodeHarness(ArbitrationTunables tunables)
    {
        Subnet = new SubnetSimulator(() => _clock.Elapsed);
        Tunables = tunables;

        MachineId a = ReducerHarness.Id(ReducerHarness.AHex);
        MachineId b = ReducerHarness.Id(ReducerHarness.BHex);

        NodeA = BuildNode("A", a, b);
        NodeB = BuildNode("B", b, a);
    }

    internal SubnetSimulator Subnet { get; }

    internal ArbitrationTunables Tunables { get; }

    internal Node NodeA { get; }

    internal Node NodeB { get; }

    internal TimeSpan Now => _clock.Elapsed;

    internal static TwoNodeHarness Create(ArbitrationTunables? tunables = null) =>
        new(tunables ?? ArbitrationTunables.Default);

    /// <summary>
    /// Advances one cadence, ticks both nodes, and delivers. Stepping in cadence-sized
    /// increments matters: a heartbeat is emitted only on a tick, so advancing ten seconds
    /// and ticking once would produce one beat rather than the five §4.3's loss scenario is
    /// about.
    /// </summary>
    internal TwoNodeHarness Beat(int beats = 1)
    {
        for (int beat = 0; beat < beats; beat++)
        {
            _clock.Advance(Tunables.HeartbeatCadence);
            NodeA.Tick();
            NodeB.Tick();
            Subnet.Deliver();
        }

        return this;
    }

    /// <summary>Advances the clock without ticking - time passing for a machine nobody is driving.</summary>
    internal TwoNodeHarness Advance(TimeSpan by)
    {
        _clock.Advance(by);
        return this;
    }

    /// <summary>Delivers whatever is due right now.</summary>
    internal TwoNodeHarness Deliver()
    {
        Subnet.Deliver();
        return this;
    }

    private Node BuildNode(string name, MachineId self, MachineId peer)
    {
        if (!Roster.TryCreate(self, peer, out Roster? roster))
        {
            throw new InvalidOperationException("Test roster is invalid.");
        }

        var config = new FakeConfigStore(
            WireVectorConstants.PairId, roster!, WireVectorConstants.TestPairKey);
        var stateStore = new FakeStateStore();
        IPeerTransport transport = Subnet.ConnectEndpoint(name);

        return new Node(name, _clock, config, stateStore, transport, Tunables);
    }

    /// <summary>One machine: its cycle, its seams, and the identity it was built with.</summary>
    internal sealed class Node
    {
        internal Node(
            string name,
            IClock clock,
            FakeConfigStore config,
            FakeStateStore stateStore,
            IPeerTransport transport,
            ArbitrationTunables tunables)
        {
            Name = name;
            Config = config;
            StateStore = stateStore;
            Transport = transport;

            var executor = new EffectExecutor(clock, config, stateStore, transport);
            Loop = new ArbitrationLoop(clock, config, executor, tunables: tunables);

            transport.DatagramReceived += datagram => Received.Add(Loop.Receive(datagram.Span));
        }

        /// <summary>
        /// The ingress verdict on every datagram this node was handed, in order.
        /// </summary>
        /// <remarks>
        /// Recorded so that a property like "a node drops its own broadcast" is asserted on
        /// what the node actually received off the subnet, rather than by handing it a
        /// datagram directly - which would pass whatever topology the simulator has.
        /// </remarks>
        internal List<IngressResult> Received { get; } = [];

        internal string Name { get; }

        internal ArbitrationLoop Loop { get; }

        internal FakeConfigStore Config { get; }

        internal FakeStateStore StateStore { get; }

        internal IPeerTransport Transport { get; }

        internal MachineId Self => Config.Roster.Self;

        internal MachineId Peer => Config.Roster.Peer!.Value;

        internal ArbitrationState State => Loop.State;

        internal bool ShouldMute => Loop.LastResult.ShouldMute;

        internal TrayState Tray => Loop.LastResult.TrayState;

        internal bool PeerPresent => Loop.PeerPresent;

        internal void Claim() => Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        internal void Tick() => Loop.Post(new ArbitrationEvent.Tick());

        internal void EnterQuarantine() => Loop.Post(new ArbitrationEvent.QuarantineEntered());
    }
}
