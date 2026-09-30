using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.PeerLink.Wire;

namespace SoloSpeaker.App.PeerLink;

/// <summary>
/// <see cref="IPeerTransport"/> over a real UDP socket - <c>docs/design.md</c> §7.1's
/// transport, and nothing above it.
/// </summary>
/// <remarks>
/// <para>
/// No parsing, no scheduling, no presence, no authentication.
/// <c>implementation-plan.md</c> §3 puts validation deliberately <em>above</em> this seam so
/// that §10's hostile-input tests run on real bytes without a socket, and that only stays
/// true if this type never develops an opinion about what a datagram means.
/// </para>
/// <para>
/// <b>Failures are reported, never swallowed, and never fatal.</b> Goal 1's direction is to
/// keep running: a transport that gives up silently stops presence forever, and §5.5 only
/// unmutes on a presence lapse, so a dead receive loop on one machine strands the other
/// muted. Every socket error is therefore classified explicitly - <c>AGENTS.md</c> §4
/// forbids the blanket catch, and <c>DatagramRouter</c> throws on purpose for unimplemented
/// paths, so swallowing everything here would hide that too.
/// </para>
/// <para>
/// One classification is worth stating because it is counter-intuitive and was wrong in the
/// first draft of this work item: an oversize datagram does <b>not</b> arrive truncated.
/// Windows fails the receive with <see cref="SocketError.MessageSize"/>. Sizing the buffer
/// at <see cref="WireProtocol.MaxDatagramBytes"/> would therefore have turned one chatty
/// device on the subnet - which manual matrix row F12 asks a human to simulate - into one
/// re-bind per second. The buffer is one byte larger than the wire maximum so that an
/// oversize datagram is <em>received</em> and then rejected by the pipeline above, exactly
/// as golden vector <c>v1-oversize</c> asserts.
/// </para>
/// </remarks>
public sealed class UdpPeerTransport : IPeerTransport, IDisposable
{
    // One byte past the wire bound, so oversize is a readable datagram the pipeline refuses
    // rather than a socket error the transport has to interpret. The bound itself stays in
    // Core, where the golden vectors test it.
    private static readonly int ReceiveBufferBytes = WireProtocol.MaxDatagramBytes + 1;

    private static readonly TimeSpan MinimumRebindBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRebindBackoff = TimeSpan.FromSeconds(5);

    private readonly TransportEndpoint _endpoint;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    private Socket? _socket;
    private Thread? _receiver;
    private TimeSpan _backoff = MinimumRebindBackoff;
    private bool _disposed;

    /// <summary>Creates a transport over one endpoint. Nothing binds until <see cref="Start"/>.</summary>
    public UdpPeerTransport(TransportEndpoint endpoint) => _endpoint = endpoint;

    /// <inheritdoc />
    public event Action<ReadOnlyMemory<byte>>? DatagramReceived;

    /// <inheritdoc />
    public event Action? NetworkChanged;

    /// <summary>
    /// Raised on every successful bind, including a re-bind after the network returned.
    /// </summary>
    /// <remarks>
    /// The host turns this into §7.6's <c>QuarantineRestarted</c>. It is an edge rather than
    /// a latch because a re-bind is exactly as good a reason to re-measure the window as the
    /// first bind was - the machine has just been off the air for an unknown period, which
    /// is the situation the window exists for.
    /// </remarks>
    public event Action? Bound;

    /// <summary>Raised when the socket cannot be bound, or has lost the network.</summary>
    public event Action? Unavailable;

    /// <inheritdoc />
    public bool IsBound
    {
        get
        {
            lock (_gate)
            {
                return _socket is not null;
            }
        }
    }

    /// <summary>Binds the socket and starts receiving. Safe to call once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        _receiver = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "SoloSpeaker.PeerLink",
        };

        _receiver.Start();
    }

    /// <inheritdoc />
    public void Send(ReadOnlyMemory<byte> datagram)
    {
        Socket? socket;

        lock (_gate)
        {
            socket = _socket;
        }

        if (socket is null)
        {
            return;
        }

        try
        {
            socket.SendTo(datagram.Span, SocketFlags.None, _endpoint.BroadcastTarget);
        }
        catch (SocketException failure) when (IsTransient(failure.SocketErrorCode))
        {
            // A send that fails because the interface went away is the same condition the
            // receive loop is about to notice. Dropping it keeps the cadence going; the
            // re-bind is what actually repairs anything.
            Fault();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose. Shutdown is already underway.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _stopping.Cancel();

        CloseSocket();

        // The receive thread is a background thread blocked in ReceiveFrom; closing the
        // socket is what releases it. A bounded join keeps shutdown from hanging if it does
        // not, which matters because §7.1's departure sends run during WM_ENDSESSION.
        _receiver?.Join(TimeSpan.FromSeconds(1));

        _stopping.Dispose();
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[ReceiveBufferBytes];

        while (!_stopping.IsCancellationRequested)
        {
            Socket? socket = EnsureBound();

            if (socket is null)
            {
                continue;
            }

            try
            {
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                int received = socket.ReceiveFrom(buffer, ref sender);

                if (received > 0)
                {
                    // A copy, because the buffer is reused on the next iteration and the
                    // host queues this for another thread. Handing out a reused buffer is
                    // the classic way to make a queued datagram change under its reader.
                    DatagramReceived?.Invoke(buffer.AsSpan(0, received).ToArray());
                }
            }
            catch (SocketException failure)
            {
                if (!HandleReceiveFailure(failure.SocketErrorCode))
                {
                    throw;
                }
            }
            catch (ObjectDisposedException)
            {
                // Dispose closed the socket underneath us. The cancellation check ends it.
            }
        }
    }

    /// <summary>
    /// Classifies a receive failure. Returns whether the loop may continue.
    /// </summary>
    private bool HandleReceiveFailure(SocketError error)
    {
        switch (error)
        {
            case SocketError.MessageSize:
                // Larger than any legal v1 datagram. Dropped here because the bytes cannot
                // be recovered, not because the transport judged the content - the size rule
                // itself still lives in Core.
                return true;

            case SocketError.ConnectionReset:
            case SocketError.ConnectionRefused:
                // ICMP port-unreachable from a host with nothing listening. Routine on
                // Windows UDP and not a fault of ours; SIO_UDP_CONNRESET suppresses most of
                // it at bind, and this catches the rest.
                return true;

            case SocketError.Interrupted:
            case SocketError.OperationAborted:
                return true;

            case SocketError.NetworkDown:
            case SocketError.NetworkReset:
            case SocketError.NetworkUnreachable:
            case SocketError.HostDown:
            case SocketError.HostUnreachable:
            case SocketError.AddressNotAvailable:
                // The interface is gone. Drop the socket so the next iteration re-binds,
                // and tell the host - a re-bind is a network transition, and work item 11
                // consumes exactly one edge for both this and the OS notification.
                CloseSocket();
                Fault();
                RaiseNetworkChanged();
                return true;

            default:
                // Unknown. AGENTS.md §4: a silent catch here means a machine stops hearing
                // its peer and nobody can say why.
                return false;
        }
    }

    private Socket? EnsureBound()
    {
        lock (_gate)
        {
            if (_socket is not null)
            {
                return _socket;
            }
        }

        Socket? bound = TryBind();

        if (bound is null)
        {
            Fault();

            // Bounded backoff. Without it a machine with no network spins a core, and the
            // transport becomes its own denial of service on the way to reporting a fault
            // it has already reported.
            _stopping.Token.WaitHandle.WaitOne(_backoff);
            _backoff = _backoff < MaximumRebindBackoff
                ? TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaximumRebindBackoff.Ticks))
                : MaximumRebindBackoff;

            return null;
        }

        lock (_gate)
        {
            _socket = bound;
        }

        _backoff = MinimumRebindBackoff;
        Bound?.Invoke();

        return bound;
    }

    private Socket? TryBind()
    {
        Socket? socket = null;

        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Order matters: ExclusiveAddressUse and ReuseAddress are the same underlying
            // decision in .NET, and setting ReuseAddress while exclusive use is still on
            // throws.
            if (_endpoint.AllowPortSharing)
            {
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
            }

            socket.EnableBroadcast = true;
            SuppressConnectionReset(socket);
            socket.Bind(_endpoint.BindTarget);

            return socket;
        }
        catch (SocketException)
        {
            // Port held by something else, or no interface up yet at logon. Both are
            // ordinary and both are retried; the fault is what makes them visible.
            socket?.Dispose();
            return null;
        }
        catch (ObjectDisposedException)
        {
            socket?.Dispose();
            return null;
        }
    }

    private static void SuppressConnectionReset(Socket socket)
    {
        const int SioUdpConnreset = unchecked((int)0x9800000C);

        try
        {
            socket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // Not supported on every stack. The receive loop classifies ConnectionReset
            // anyway, so this is an optimisation rather than a requirement.
        }
    }

    private void CloseSocket()
    {
        Socket? closing;

        lock (_gate)
        {
            closing = _socket;
            _socket = null;
        }

        closing?.Dispose();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args)
    {
        // The socket may well have survived - a second adapter appearing does not break the
        // first. Re-binding regardless would drop datagrams for no reason, so this only
        // forwards the edge and lets work item 11 decide.
        RaiseNetworkChanged();
    }

    private void RaiseNetworkChanged() => NetworkChanged?.Invoke();

    private void Fault() => Unavailable?.Invoke();

    private static bool IsTransient(SocketError error) => error
        is SocketError.NetworkDown
        or SocketError.NetworkReset
        or SocketError.NetworkUnreachable
        or SocketError.HostDown
        or SocketError.HostUnreachable
        or SocketError.AddressNotAvailable;
}
