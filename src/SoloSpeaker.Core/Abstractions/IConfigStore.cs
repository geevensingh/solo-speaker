namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// The paired configuration of <c>docs/design.md</c> §7.5 - `pairId`, `pairKey`, the
/// roster, and the tunables.
/// </summary>
/// <remarks>
/// Split out of <see cref="IStateStore"/> per <c>docs/review-2026-09-28.md</c> finding
/// B-4. The roster previously had no seam at all, despite being the right-hand side of
/// §5.5's predicate - so the reducer could not evaluate its central invariant through a
/// declared abstraction, and configuration would have arrived by some undeclared path.
/// </remarks>
public interface IConfigStore
{
    /// <summary>
    /// The two enrolled roster IDs of §5.3, or an incomplete set during the §7.7 pairing
    /// window. Bytes, never strings: ordinal byte equality is the requirement, and a byte
    /// representation is what actually removes the casing hazard - not any build flag.
    /// </summary>
    IReadOnlyList<byte[]> Roster { get; }

    /// <summary>The pairing GUID, transmitted on every datagram to scope the namespace.</summary>
    byte[] PairId { get; }

    /// <summary>
    /// The 256-bit HMAC key. Never transmitted, never logged, never rendered. Held
    /// protected at rest per <c>adr/0013</c> and decrypted on demand.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the protected value cannot be decrypted on this profile,
    /// which raises <see cref="TrayState.Error"/> with a re-pair cause rather than
    /// crashing or falling back.
    /// </returns>
    bool TryGetPairKey(out byte[] pairKey);

    /// <summary>This machine's own roster ID.</summary>
    byte[] SelfId { get; }

    /// <summary>
    /// Whether the roster holds both entries. A machine with an incomplete roster can
    /// never mute - §5.5's positive predicate is false - and raises
    /// <see cref="TrayState.Error"/> with a "pairing incomplete" cause.
    /// </summary>
    bool IsPaired { get; }

    /// <summary>
    /// Cross-checks that this configuration and the persisted state carry the same
    /// <c>pairId</c>. §7.5's invariant spans both files, so restoring one from backup
    /// without the other must raise <see cref="TrayState.Error"/> rather than being
    /// silently tolerated.
    /// </summary>
    bool MatchesState(ReadOnlySpan<byte> statePairId);
}
