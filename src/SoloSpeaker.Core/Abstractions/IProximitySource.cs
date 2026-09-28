namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Presence only - "is the peer near enough to conflict with". See <c>docs/design.md</c>
/// §8.
/// </summary>
/// <remarks>
/// <para>
/// This seam is narrower than it looks, and the design says so explicitly. PeerLink fuses
/// presence, state replication, and authentication, while a phase-3 Bluetooth RSSI source
/// would supply presence alone. Phase 3 is therefore a decomposition of PeerLink, not a
/// drop-in swap of this interface, and this boundary buys the presence question only.
/// </para>
/// <para>
/// It exists from phase 1 anyway so that presence is never read from the transport
/// directly, which is what would make the later decomposition expensive.
/// </para>
/// </remarks>
public interface IProximitySource
{
    /// <summary>
    /// Whether a valid peer signal has arrived within the presence window (10 s by
    /// default, i.e. five missed 2 s beats).
    /// </summary>
    /// <remarks>
    /// Loss of presence unmutes via the §5.5 predicate. It never alters
    /// <c>activeOwner</c> - stored ownership survives the peer's absence and applies again
    /// on return.
    /// </remarks>
    bool PeerPresent { get; }
}
