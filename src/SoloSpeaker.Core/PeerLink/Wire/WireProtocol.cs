namespace SoloSpeaker.Core.PeerLink.Wire;

/// <summary>
/// The frozen v1 constants of <c>docs/wire-format.md</c>.
/// </summary>
/// <remarks>
/// All four rows of that document's Bounds table live here, including the two timing values
/// whose consumers - the heartbeat scheduler and the presence window - do not exist yet.
/// One normative table with two code homes is how the halves drift, and the table is a
/// single unit whether or not every row has a caller today.
/// </remarks>
public static class WireProtocol
{
    /// <summary>
    /// The wire format version. Frozen by <c>docs/design.md</c> §8 so that nothing on the
    /// wire becomes a two-machine migration.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Maximum accepted <c>seq</c> above the local value - Bounds row 1.
    /// </summary>
    /// <remarks>
    /// The bound is <b>one-directional</b>. §5.4 drops a datagram whose <c>seq</c>
    /// <em>exceeds</em> the local value by more than this; a <em>lower</em> <c>seq</c> is a
    /// different case that §5.4 ignores silently. Computing an unsigned difference without
    /// checking the direction first wraps, so an ordinary reordered or replayed datagram
    /// would present as a delta of 2^64-1 and light a sticky <c>error</c>.
    /// </remarks>
    public const ulong MaxSeqDelta = 1000;

    /// <summary>
    /// Maximum datagram size in bytes - Bounds row 2. The canonical payload is around 310
    /// bytes; anything larger is malformed, and the cap keeps the parser off fragmented
    /// paths.
    /// </summary>
    public const int MaxDatagramBytes = 512;

    /// <summary>Length of the canonical hex rendering of the <c>mac</c> field.</summary>
    public const int MacHexLength = 64;

    /// <summary>Length of the HMAC-SHA256 tag in bytes.</summary>
    public const int MacByteLength = 32;

    /// <summary>
    /// Heartbeat cadence - Bounds row 3. Plus an immediate extra send on any state change,
    /// so claims feel instant rather than up to one beat late.
    /// </summary>
    public static TimeSpan HeartbeatCadence => TimeSpan.FromSeconds(2);

    /// <summary>
    /// Presence window - Bounds row 4, five missed beats. A peer is present only if a
    /// datagram <em>from the peer</em> passed every ingress check within this window; a
    /// machine's own broadcasts are dropped at §7.1 step 3 and never refresh it.
    /// </summary>
    public static TimeSpan PresenceWindow => TimeSpan.FromSeconds(10);
}
