namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Raw datagram transport for PeerLink (<c>docs/design.md</c> §7.1). Authentication,
/// roster checks, and the <c>seq</c> bound are <em>not</em> implemented here — they belong
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
    /// Whether the socket has successfully bound and sent at least once. The §7.6
    /// quarantine window starts from this point, not from process start, because the
    /// network stack is routinely unavailable for several seconds after resume.
    /// </summary>
    bool IsBound { get; }

    /// <summary>
    /// Raised when Windows reports a network change, which restarts the quarantine window.
    /// </summary>
    event Action? NetworkChanged;

    /// <summary>Broadcasts a datagram to the subnet broadcast address on the configured port.</summary>
    void Send(ReadOnlyMemory<byte> datagram);
}
