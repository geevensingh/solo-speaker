namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Raw datagram transport for PeerLink (<c>docs/design.md</c> §7.1). Authentication,
/// roster checks, and the <c>seq</c> bound are <em>not</em> implemented here - they belong
/// to the ingress pipeline above this seam, so that the hostile-input tests of §10 can run
/// against real bytes without a socket.
/// </summary>
public interface IPeerTransport
{
    /// <summary>
    /// Raised for every datagram received, before any validation. The fake implementation
    /// used by the two-node test harness can drop, duplicate, reorder, and delay these.
    /// </summary>
    event Action<ReadOnlyMemory<byte>>? DatagramReceived;

    /// <summary>
    /// Whether the socket is currently bound. Design revision 10: this is a state, not a
    /// latch, because a transport that re-binds after losing the network would otherwise
    /// report health it no longer has.
    /// </summary>
    /// <remarks>
    /// §7.6's quarantine window no longer starts from this point. It is open from process
    /// start, decided in <c>StartupDecision</c>, because a window whose trigger was a send
    /// could not be implemented - a quarantined machine broadcasts nothing, so the trigger
    /// either never fired or was preceded by exactly the stale datagram §7.6 exists to
    /// prevent. A successful bind <em>restarts</em> the already-open window instead, which
    /// is what re-measures the 12s from the point the network actually came up.
    /// </remarks>
    bool IsBound { get; }

    /// <summary>
    /// Raised when Windows reports a network change, which restarts the quarantine window.
    /// </summary>
    event Action? NetworkChanged;

    /// <summary>Broadcasts a datagram to the subnet broadcast address on the configured port.</summary>
    void Send(ReadOnlyMemory<byte> datagram);
}
