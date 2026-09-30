using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.App.Tests;

internal static class AppTestIds
{
    internal const string PairIdHex = "b1f0a2c3d4e5f60718293a4b5c6d7e8f";
    internal const string SelfHex = "7f3a9c1e2d4b6a8035179246ab13cd5e";
    internal const string PeerHex = "2d81e407fa63b95c18204e7dc6395fa1";

    private static readonly DateTimeOffset Epoch = new(2026, 9, 25, 21, 0, 0, TimeSpan.Zero);

    internal static byte[] PairKey { get; } = [.. Enumerable.Range(0, 32).Select(index => (byte)index)];

    internal static PairId PairId => ParsePairId(PairIdHex);

    internal static MachineId Self => ParseMachine(SelfHex);

    internal static MachineId Peer => ParseMachine(PeerHex);

    internal static Roster CompleteRoster()
    {
        if (!Roster.TryCreate(Self, Peer, out Roster? roster))
        {
            throw new InvalidOperationException("Test roster is invalid.");
        }

        return roster!;
    }

    internal static PairId ParsePairId(string hex)
    {
        if (!PairId.TryParse(hex, out PairId pairId))
        {
            throw new InvalidOperationException($"Test pair ID '{hex}' is not canonical.");
        }

        return pairId;
    }

    internal static MachineId ParseMachine(string hex)
    {
        if (!MachineId.TryParseOwner(hex, out MachineId machineId))
        {
            throw new InvalidOperationException($"Test machine ID '{hex}' is not canonical.");
        }

        return machineId;
    }

    internal static byte[] DatagramFromPeer(MachineId activeOwner, ulong seq, bool bye = false)
    {
        var datagram = new PeerDatagram(
            WireProtocol.Version,
            PairId,
            Peer,
            seq,
            activeOwner,
            MicLive: false,
            bye,
            Epoch + TimeSpan.FromSeconds((double)seq));

        byte[] buffer = new byte[WireProtocol.MaxDatagramBytes];
        int length = DatagramCodec.Encode(datagram, PairKey, buffer);
        return buffer[..length];
    }

    internal static PeerDatagram Decode(ReadOnlyMemory<byte> datagram)
    {
        byte[] mac = new byte[WireProtocol.MacByteLength];
        DatagramParseResult result = DatagramCodec.TryParse(datagram.Span, mac, out PeerDatagram parsed, out PairId pairId);

        Assert.Equal(DatagramParseResult.Ok, result);
        Assert.Equal(PairId, pairId);
        Assert.True(DatagramCodec.VerifyMac(parsed, mac, PairKey));

        return parsed;
    }
}

internal sealed class FakeClock : IClock
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 25, 21, 0, 0, TimeSpan.Zero);

    public TimeSpan Elapsed { get; private set; }

    public DateTimeOffset UtcNow => Epoch + Elapsed;

    internal void Advance(TimeSpan by) => Elapsed += by;
}

internal sealed class FakeConfigStore : IConfigStore
{
    private readonly byte[] _pairKey;

    internal FakeConfigStore(Roster? roster = null, byte[]? pairKey = null)
    {
        Roster = roster ?? AppTestIds.CompleteRoster();
        PairId = AppTestIds.PairId;
        _pairKey = pairKey ?? AppTestIds.PairKey;
    }

    public Roster Roster { get; private set; }

    public PairId PairId { get; }

    public bool IsPaired => Roster.IsComplete;

    public int Port { get; init; } = Core.StateStore.ConfigDefaults.Port;

    public string Hotkey { get; init; } = Core.StateStore.ConfigDefaults.Hotkey;

    public bool TryGetPairKey(out byte[] pairKey)
    {
        pairKey = _pairKey.ToArray();
        return true;
    }

    public bool MatchesState(PairId statePairId) => statePairId == PairId;

    public bool TryCompleteRoster(MachineId peerId)
    {
        if (Roster.IsComplete || !Roster.TryCreate(Roster.Self, peerId, out Roster? completed))
        {
            return false;
        }

        Roster = completed!;
        return true;
    }
}

internal sealed class FakeStateStore : IStateStore
{
    private MachineId _activeOwner = MachineId.None;
    private ulong _seq;
    private bool _hasState;

    public PairId? StatePairId { get; private set; } = AppTestIds.PairId;

    internal List<(MachineId ActiveOwner, ulong Seq)> Saves { get; } = [];

    public void SaveState(MachineId activeOwner, ulong seq)
    {
        _activeOwner = activeOwner;
        _seq = seq;
        _hasState = true;
        Saves.Add((activeOwner, seq));
    }

    public bool TryLoadState(out MachineId activeOwner, out ulong seq)
    {
        activeOwner = _activeOwner;
        seq = _seq;
        return _hasState;
    }
}

internal sealed class RecordingPeerTransport : IPeerTransport
{
    public event Action<ReadOnlyMemory<byte>>? DatagramReceived;

    public event Action? NetworkChanged;

    public bool IsBound { get; init; } = true;

    internal List<byte[]> Sent { get; } = [];

    public void Send(ReadOnlyMemory<byte> datagram) => Sent.Add(datagram.ToArray());

    internal void RaiseDatagram(ReadOnlyMemory<byte> datagram) => DatagramReceived?.Invoke(datagram);

    internal void RaiseNetworkChanged() => NetworkChanged?.Invoke();
}

internal sealed class QueuedDispatchTarget : SoloSpeaker.App.Hosting.IDispatchTarget
{
    // Locked rather than a plain Queue: the two-instance test enqueues from the socket
    // receive thread while the test thread drains, which is the exact concurrency the
    // dispatch exists to tame.
    private readonly Lock _gate = new();
    private readonly Queue<Action> _queued = [];

    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count;
            }
        }
    }

    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            _queued.Enqueue(work);
        }
    }

    public void Drain(TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            Action work;

            lock (_gate)
            {
                if (_queued.Count == 0)
                {
                    return;
                }

                work = _queued.Dequeue();
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The queued dispatch did not drain before the test timeout.");
            }

            work();
        }
    }
}

internal sealed class AppArbitrationFixture
{
    private AppArbitrationFixture(
        FakeClock clock,
        FakeConfigStore config,
        FakeStateStore stateStore,
        RecordingPeerTransport transport,
        EffectExecutor executor,
        ArbitrationLoop loop,
        SoloSpeaker.App.Hosting.EventDispatch dispatch,
        QueuedDispatchTarget target)
    {
        Clock = clock;
        Config = config;
        StateStore = stateStore;
        Transport = transport;
        Executor = executor;
        Loop = loop;
        Dispatch = dispatch;
        Target = target;
    }

    internal FakeClock Clock { get; }

    internal FakeConfigStore Config { get; }

    internal FakeStateStore StateStore { get; }

    internal RecordingPeerTransport Transport { get; }

    internal EffectExecutor Executor { get; }

    internal ArbitrationLoop Loop { get; }

    internal SoloSpeaker.App.Hosting.EventDispatch Dispatch { get; }

    internal QueuedDispatchTarget Target { get; }

    internal static AppArbitrationFixture Create(ArbitrationState? initialState = null)
    {
        var clock = new FakeClock();
        var config = new FakeConfigStore();
        var stateStore = new FakeStateStore();
        var transport = new RecordingPeerTransport();
        var executor = new EffectExecutor(clock, config, stateStore, transport);
        var loop = new ArbitrationLoop(clock, config, executor, initialState);
        var target = new QueuedDispatchTarget();
        var dispatch = new SoloSpeaker.App.Hosting.EventDispatch(loop, target);

        return new AppArbitrationFixture(clock, config, stateStore, transport, executor, loop, dispatch, target);
    }
}
