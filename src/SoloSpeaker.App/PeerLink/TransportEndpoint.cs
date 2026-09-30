using System.Net;

namespace SoloSpeaker.App.PeerLink;

/// <summary>
/// How a socket binds and where it broadcasts - <c>docs/design.md</c> §7.1's "fixed port
/// (default 48292, configurable)" plus the address policy around it.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that a <em>test</em> requirement does not become permanent shipped
/// behaviour. Work item 6's done-criterion runs two instances on one host, which needs two
/// sockets on one port, which needs <c>SO_REUSEADDR</c>. Enabling that unconditionally would
/// mean both real machines run a permissive socket forever so that a test can pass.
/// </para>
/// <para>
/// Production takes <see cref="Exclusive"/>; the two-instance test takes
/// <see cref="Shared"/> and says why in one place.
/// </para>
/// </remarks>
/// <param name="Port">The UDP port, bound and sent to. §7.1 uses one port for both.</param>
/// <param name="AllowPortSharing">
/// Whether another socket may bind the same port. <see langword="false"/> asks Windows for
/// exclusive use, which is what a single real machine wants: it turns "something else is
/// already listening" into a bind failure the tray can report rather than a silent split of
/// arriving datagrams.
/// </param>
public readonly record struct TransportEndpoint(int Port, bool AllowPortSharing)
{
    /// <summary>The production endpoint: one instance per machine, exclusive use of the port.</summary>
    public static TransportEndpoint Exclusive(int port) => new(port, AllowPortSharing: false);

    /// <summary>
    /// The two-instance endpoint. Only work item 6's done-criterion test uses this, because
    /// only it puts two SoloSpeakers on one host.
    /// </summary>
    public static TransportEndpoint Shared(int port) => new(port, AllowPortSharing: true);

    /// <summary>Where a heartbeat goes. §7.1 broadcasts; it never unicasts a peer.</summary>
    public IPEndPoint BroadcastTarget => new(IPAddress.Broadcast, Port);

    /// <summary>Where the socket listens. Any interface, because the peer may be on any of them.</summary>
    public IPEndPoint BindTarget => new(IPAddress.Any, Port);
}
