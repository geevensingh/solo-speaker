using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.StateStore;
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

        var files = new FakeFileStore();
        var protector = new FakeSecretProtector();
        string configPath = $@"C:\{name}\config.json";
        string statePath = $@"C:\{name}\state.json";

        // The ceremony writes both files - design revision 9 - so a restart has both sides
        // of the cross-file check available, exactly as a paired machine would.
        JsonConfigStore.Create(
            files, configPath, protector, WireVectorConstants.PairId, WireVectorConstants.TestPairKey, roster!);

        if (!JsonConfigStore.TryLoad(files, configPath, protector, out JsonConfigStore? config).IsUsable)
        {
            throw new InvalidOperationException("Test configuration did not load.");
        }

        var stateStore = new JsonStateStore(files, statePath, config!.PairId);
        stateStore.SaveState(MachineId.None, 0);

        IPeerTransport transport = Subnet.ConnectEndpoint(name);

        return new Node(name, _clock, config, stateStore, files, transport, Tunables);
    }

    /// <summary>One machine: its cycle, its seams, and the identity it was built with.</summary>
    internal sealed class Node
    {
        private readonly IClock _clock;
        private readonly ArbitrationTunables _tunables;

        internal Node(
            string name,
            IClock clock,
            JsonConfigStore config,
            JsonStateStore stateStore,
            FakeFileStore files,
            IPeerTransport transport,
            ArbitrationTunables tunables)
        {
            Name = name;
            Config = config;
            StateStore = stateStore;
            Files = files;
            Transport = transport;
            _clock = clock;
            _tunables = tunables;

            Loop = BuildLoop(ArbitrationState.Fresh());
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

        internal ArbitrationLoop Loop { get; private set; }

        internal JsonConfigStore Config { get; }

        internal JsonStateStore StateStore { get; }

        internal FakeFileStore Files { get; }

        internal IPeerTransport Transport { get; }

        internal MachineId Self => Config.Roster.Self;

        internal MachineId Peer => Config.Roster.Peer!.Value;

        internal ArbitrationState State => Loop.State;

        internal bool ShouldMute => Loop.LastResult.ShouldMute;

        internal TrayState Tray => Loop.LastResult.TrayState;

        internal bool PeerPresent => Loop.PeerPresent;

        /// <summary>
        /// Tears the cycle down and rebuilds it from what the real store holds - the restart
        /// scenario work item 4 booked onto work item 5.
        /// </summary>
        internal StartupOutcome Restart()
        {
            StateStore.TryLoadState(out MachineId activeOwner, out ulong seq);

            StartupOutcome outcome = StartupDecision.Decide(
                Config.PairId, StateStore.LastRead, StateStore.StatePairId, activeOwner, seq, _clock.Elapsed);

            Loop = BuildLoop(outcome.State);
            return outcome;
        }

        internal void Claim() => Loop.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        internal void Tick() => Loop.Post(new ArbitrationEvent.Tick());

        internal void EnterQuarantine() => Loop.Post(new ArbitrationEvent.QuarantineEntered());

        private ArbitrationLoop BuildLoop(ArbitrationState initial)
        {
            var executor = new EffectExecutor(_clock, Config, StateStore, Transport);
            return new ArbitrationLoop(_clock, Config, executor, initial, _tunables);
        }
    }
}
