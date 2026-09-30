using System.Net;
using System.Net.Sockets;
using SoloSpeaker.App.PeerLink;
using SoloSpeaker.Core.PeerLink.Wire;

namespace SoloSpeaker.App.Tests;

public sealed class UdpPeerTransportTests
{
    private const string SharedBroadcastSkipReason =
        "This Windows runner does not deliver one IPv4 broadcast to both SO_REUSEADDR sockets bound to the same port.";

    private static readonly Lazy<bool> SharedBroadcastCapability = new(ProbeSharedBroadcastCapability);

    private static int _nextPort = 48920;

    [Fact]
    public void A_transport_binds_reports_bound_and_raises_bound()
    {
        int port = NextPort();
        using var bound = new ManualResetEventSlim();
        using var transport = new UdpPeerTransport(TransportEndpoint.Exclusive(port));
        transport.Bound += bound.Set;

        transport.Start();

        Assert.True(bound.Wait(TimeSpan.FromSeconds(2)), "The transport did not raise Bound.");
        Assert.True(transport.IsBound);
    }

    /// <summary>
    /// The self-origin drop in design section 7.1 exists because Windows can deliver a
    /// broadcast back to the sender's own socket as well as to its peer's socket.
    /// </summary>
    [Fact]
    public void Two_shared_transports_on_one_port_both_receive_a_broadcast_sent_by_one_of_them()
    {
        SkipUnlessSharedBroadcastWorks();

        int port = NextPort();
        byte[] payload = [0x51, 0x52, 0x53, 0x54];
        using var firstBound = new ManualResetEventSlim();
        using var secondBound = new ManualResetEventSlim();
        using var firstReceived = new ManualResetEventSlim();
        using var secondReceived = new ManualResetEventSlim();
        using var first = new UdpPeerTransport(TransportEndpoint.Shared(port));
        using var second = new UdpPeerTransport(TransportEndpoint.Shared(port));

        first.Bound += firstBound.Set;
        second.Bound += secondBound.Set;
        first.DatagramReceived += datagram => SignalWhenPayloadMatches(datagram, payload, firstReceived);
        second.DatagramReceived += datagram => SignalWhenPayloadMatches(datagram, payload, secondReceived);

        first.Start();
        second.Start();
        Assert.True(firstBound.Wait(TimeSpan.FromSeconds(2)), "The first transport did not bind.");
        Assert.True(secondBound.Wait(TimeSpan.FromSeconds(2)), "The second transport did not bind.");

        first.Send(payload);

        Assert.True(firstReceived.Wait(TimeSpan.FromSeconds(2)), "The sender did not receive its own broadcast.");
        Assert.True(secondReceived.Wait(TimeSpan.FromSeconds(2)), "The peer transport did not receive the broadcast.");
    }

    /// <summary>
    /// Windows reports an undersized UDP receive as MessageSize. One oversize datagram must
    /// not kill the receive loop or force a re-bind before the next ordinary heartbeat.
    /// </summary>
    [Fact]
    public void An_oversize_datagram_does_not_prevent_the_next_normal_datagram_from_arriving()
    {
        int port = NextPort();
        byte[] normal = [0x61, 0x62, 0x63];
        byte[] oversize = [.. Enumerable.Repeat((byte)0x7f, 600)];
        using var bound = new ManualResetEventSlim();
        using var normalReceived = new ManualResetEventSlim();
        using var transport = new UdpPeerTransport(TransportEndpoint.Exclusive(port));

        transport.Bound += bound.Set;
        transport.DatagramReceived += datagram => SignalWhenPayloadMatches(datagram, normal, normalReceived);

        transport.Start();
        Assert.True(bound.Wait(TimeSpan.FromSeconds(2)), "The transport did not bind.");

        SendToLoopback(port, oversize);
        SendToLoopback(port, normal);

        Assert.True(normalReceived.Wait(TimeSpan.FromSeconds(2)), "The normal datagram after the oversize one did not arrive.");
        Assert.True(transport.IsBound);
    }

    [Fact]
    public void A_second_exclusive_bind_raises_unavailable_instead_of_throwing_from_start()
    {
        int port = NextPort();
        using var firstBound = new ManualResetEventSlim();
        using var unavailable = new ManualResetEventSlim();
        using var first = new UdpPeerTransport(TransportEndpoint.Exclusive(port));
        using var second = new UdpPeerTransport(TransportEndpoint.Exclusive(port));

        first.Bound += firstBound.Set;
        second.Unavailable += unavailable.Set;

        first.Start();
        Assert.True(firstBound.Wait(TimeSpan.FromSeconds(2)), "The first exclusive transport did not bind.");

        Exception? thrown = Record.Exception(second.Start);

        Assert.Null(thrown);
        Assert.True(unavailable.Wait(TimeSpan.FromSeconds(2)), "The second exclusive transport did not raise Unavailable.");
        Assert.False(second.IsBound);
    }

    private static void SkipUnlessSharedBroadcastWorks()
    {
        if (!SharedBroadcastCapability.Value)
        {
            throw Xunit.Sdk.SkipException.ForSkip(SharedBroadcastSkipReason);
        }
    }

    /// <summary>
    /// Whether this machine delivers one IPv4 broadcast to both <c>SO_REUSEADDR</c> sockets
    /// bound to one port - the property work item 6's two-instance technique rests on.
    /// </summary>
    /// <remarks>
    /// Probed rather than assumed. Microsoft documents multi-socket behaviour on a shared
    /// port as indeterminate, with multicast as the only stated exception, so the guarantee
    /// this needs is not written down anywhere even though Windows does in fact provide it.
    /// The probe performs the exact operation rather than checking a proxy such as "is there
    /// an operational non-loopback interface" - a proxy that is true on every CI runner, and
    /// so would skip in precisely the cases that were never the danger.
    /// </remarks>
    internal static bool SharedBroadcastWorks => SharedBroadcastCapability.Value;

    /// <summary>Hands out a port from the test range, so parallel tests cannot collide.</summary>
    internal static int ReserveTestPort() => NextPort();

    private static int NextPort()
    {
        int port = Interlocked.Increment(ref _nextPort);
        if (port > 48999)
        {
            throw new InvalidOperationException("The UDP test port range was exhausted.");
        }

        return port;
    }

    private static bool ProbeSharedBroadcastCapability()
    {
        int port = NextPort();
        byte[] payload = [0x31, 0x32, 0x33, 0x34];

        using Socket first = CreateSharedProbeSocket(port);
        using Socket second = CreateSharedProbeSocket(port);

        Task<byte[]> firstReceive = Task.Run(() => ReceiveOne(first));
        Task<byte[]> secondReceive = Task.Run(() => ReceiveOne(second));

        first.SendTo(payload, new IPEndPoint(IPAddress.Broadcast, port));

        bool completed = Task.WaitAll([firstReceive, secondReceive], TimeSpan.FromSeconds(3));
        return completed && firstReceive.Result.SequenceEqual(payload) && secondReceive.Result.SequenceEqual(payload);
    }

    private static Socket CreateSharedProbeSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false,
            EnableBroadcast = true,
            ReceiveTimeout = 2000,
        };

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return socket;
    }

    private static byte[] ReceiveOne(Socket socket)
    {
        byte[] buffer = new byte[WireProtocol.MaxDatagramBytes];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            int received = socket.ReceiveFrom(buffer, ref sender);
            return buffer[..received];
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    private static void SendToLoopback(int port, byte[] payload)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        socket.SendTo(payload, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static void SignalWhenPayloadMatches(
        ReadOnlyMemory<byte> datagram,
        ReadOnlySpan<byte> expected,
        ManualResetEventSlim signal)
    {
        if (datagram.Span.SequenceEqual(expected))
        {
            signal.Set();
        }
    }
}
