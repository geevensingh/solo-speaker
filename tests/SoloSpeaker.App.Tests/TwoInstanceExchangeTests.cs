using SoloSpeaker.App.Hosting;
using SoloSpeaker.App.PeerLink;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.StateStore;

namespace SoloSpeaker.App.Tests;

/// <summary>
/// Work item 6's done-criterion: two instances on one host, separate config roots, exchange
/// state; a <c>bye</c> clears presence without moving <c>activeOwner</c>.
/// </summary>
/// <remarks>
/// <para>
/// The criterion originally said "separate config roots <em>and ports</em>". That cannot
/// work: <c>IConfigStore.Port</c> is a single value driving both the bind and the
/// destination, so two instances on different ports would exchange nothing and the
/// criterion's own verb would be unsatisfiable. One port and two <c>SO_REUSEADDR</c> sockets
/// is both achievable and a closer model of two machines on a subnet - it additionally
/// covers broadcast delivery and the self-origin drop.
/// </para>
/// <para>
/// The config roots stay separate because they carry <em>identity</em>, not addressing.
/// Sharing one would give both instances the same <c>Roster.Self</c>, every datagram would
/// be discarded at ingress step 3 as self-origin, and the test would pass vacuously.
/// </para>
/// <para>
/// This composes the cycle directly rather than through <see cref="SoloSpeakerHost"/>,
/// because the host binds the §7.6 quarantine to its first successful bind and a quarantined
/// machine deliberately broadcasts nothing. The quarantine itself is covered in
/// <c>SoloSpeaker.Core.Tests</c>, where it can be tested against a controlled clock instead
/// of a 12s wait.
/// </para>
/// </remarks>
public sealed class TwoInstanceExchangeTests : IDisposable
{
    private const string SkipReason =
        "This Windows runner does not deliver one IPv4 broadcast to both SO_REUSEADDR sockets bound to the same port.";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    private readonly string _rootA = Path.Combine(Path.GetTempPath(), $"solo-speaker-a-{Guid.NewGuid():N}");
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), $"solo-speaker-b-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (string root in new[] { _rootA, _rootB })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Two_instances_exchange_state_and_a_bye_clears_presence_without_moving_the_owner()
    {
        if (!SharedBroadcastIsAvailable())
        {
            throw Xunit.Sdk.SkipException.ForSkip(SkipReason);
        }

        int port = UdpPeerTransportTests.ReserveTestPort();

        Instance instanceA = Build(_rootA, AppTestIds.Self, AppTestIds.Peer, port);
        Instance instanceB = Build(_rootB, AppTestIds.Peer, AppTestIds.Self, port);

        WaitUntil(() => instanceA.Transport.IsBound && instanceB.Transport.IsBound, instanceA, instanceB);

        // A claims. Its Write effect persists and then broadcasts, over a real socket.
        instanceA.Dispatch.Post(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        WaitUntil(() => instanceB.Dispatch.Loop.State.ActiveOwner == AppTestIds.Self, instanceA, instanceB);

        // B heard it off the wire and converged - which is the exchange the criterion asks
        // for, through the real codec, the real ingress pipeline and a real UDP broadcast.
        Assert.Equal(AppTestIds.Self, instanceB.Dispatch.Loop.State.ActiveOwner);
        Assert.True(instanceB.Dispatch.Loop.LastResult.ShouldMute);

        // A's own broadcast came back to its own socket, and was dropped at ingress step 3
        // rather than making A its own peer.
        Assert.Null(instanceA.Dispatch.Loop.State.PeerLastSeenAt);

        ulong ownedSeq = instanceB.Dispatch.Loop.State.Seq;

        // A departs.
        new DepartureAnnouncer(instanceA.Dispatch, instanceA.Executor).Announce();

        WaitUntil(() => !instanceB.Dispatch.Loop.LastResult.ShouldMute, instanceA, instanceB);

        // §7.1: the bye clears presence, so B unmutes - but ownership is untouched, because
        // a departure is not a claim. Getting this wrong in either direction is a defect:
        // leaving B muted strands it, and moving the owner invents a claim nobody made.
        Assert.False(instanceB.Dispatch.Loop.LastResult.ShouldMute);
        Assert.Equal(AppTestIds.Self, instanceB.Dispatch.Loop.State.ActiveOwner);
        Assert.Equal(ownedSeq, instanceB.Dispatch.Loop.State.Seq);
    }

    private static bool SharedBroadcastIsAvailable() => UdpPeerTransportTests.SharedBroadcastWorks;

    private static void WaitUntil(Func<bool> condition, params Instance[] instances)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (Instance instance in instances)
            {
                instance.Dispatch.Drain(Patience);
            }

            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        foreach (Instance instance in instances)
        {
            instance.Dispatch.Drain(Patience);
        }

        Assert.True(condition(), "The two instances did not reach the expected state in time.");
    }

    private Instance Build(string root, MachineId self, MachineId peer, int port)
    {
        var files = new FileStore();
        var protector = new DpapiSecretProtector();
        string configPath = DataRoot.PathFor(root, PersistedFiles.Config);

        if (!Roster.TryCreate(self, peer, out Roster? roster))
        {
            throw new InvalidOperationException("The test roster is invalid.");
        }

        // Both roots share a pairId and pairKey - they are one pair - and differ only in
        // which machine each calls itself.
        JsonConfigStore.Create(files, configPath, protector, AppTestIds.PairId, AppTestIds.PairKey, roster!);
        JsonConfigStore.TryLoad(files, configPath, protector, out JsonConfigStore? config);

        var stateStore = new JsonStateStore(
            files, DataRoot.PathFor(root, PersistedFiles.State), config!.PairId);

        var transport = new UdpPeerTransport(TransportEndpoint.Shared(port));
        var clock = new FakeClock();
        var executor = new EffectExecutor(clock, config, stateStore, transport);
        var loop = new ArbitrationLoop(clock, config, executor);
        var target = new QueuedDispatchTarget();
        var dispatch = new EventDispatch(loop, target);

        transport.DatagramReceived += datagram => dispatch.Receive(datagram);
        transport.Start();

        _disposables.Add(transport);

        return new Instance(transport, dispatch, executor);
    }

    private sealed record Instance(
        UdpPeerTransport Transport,
        EventDispatch Dispatch,
        EffectExecutor Executor);
}
