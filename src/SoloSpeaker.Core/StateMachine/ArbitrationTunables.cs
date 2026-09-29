using SoloSpeaker.Core.PeerLink.Wire;

namespace SoloSpeaker.Core.StateMachine;

/// <summary>
/// The intervals the reducer measures, all of them from <c>docs/design.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec constants, not user configuration. §7.5's <c>config.json</c> holds the port, the
/// hotkey, the denylist and the mic debounce - none of which is a reducer window - so
/// <c>IConfigStore</c> deliberately does not supply these. The override path exists so a
/// test can shrink a 12 s window rather than wait one out.
/// </para>
/// <para>
/// Two of the four are re-exported from <see cref="WireProtocol"/> rather than restated, so
/// that <c>docs/wire-format.md</c>'s Bounds table keeps a single code home.
/// </para>
/// </remarks>
public sealed record ArbitrationTunables
{
    /// <summary>The spec defaults.</summary>
    public static ArbitrationTunables Default { get; } = new();

    /// <summary>
    /// §7.1: a peer is present only if a datagram from the peer passed every ingress check
    /// within this window - five missed beats.
    /// </summary>
    public TimeSpan PresenceWindow { get; init; } = WireProtocol.PresenceWindow;

    /// <summary>
    /// §7.1: the heartbeat interval, plus an immediate extra send on any state change so
    /// claims feel instant rather than up to one beat late.
    /// </summary>
    public TimeSpan HeartbeatCadence { get; init; } = WireProtocol.HeartbeatCadence;

    /// <summary>
    /// §7.6: the rejoin window. It starts on first successful socket bind and send, not on
    /// process start, because the network stack is routinely unavailable for several
    /// seconds after resume.
    /// </summary>
    public TimeSpan QuarantineWindow { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// §7.1 (design revision 7): how many unverifiable datagrams within
    /// <see cref="PresenceWindow"/> count as "sustained".
    /// </summary>
    /// <remarks>
    /// Three, set deliberately below the peer's own beat rate rather than at it. A real
    /// version-mismatched peer broadcasts every 2 s, which is exactly five per ten seconds,
    /// so a threshold of five would sit on the rate it exists to detect and one dropped
    /// packet would silence the signal.
    /// </remarks>
    public int UnverifiableThreshold { get; init; } = 3;
}
