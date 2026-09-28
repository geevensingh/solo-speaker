namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Persistence for the replicated latch and the paired configuration
/// (<c>docs/design.md</c> §7.5).
/// </summary>
/// <remarks>
/// The split between the two files is load-bearing and is a known hazard: the roster lives
/// in <c>config.json</c> while <c>activeOwner</c> lives in <c>state.json</c>, and §5.5's
/// invariant spans both. Restoring one from backup without the other breaks the invariant,
/// so both carry the same <c>pairId</c> and a mismatch raises
/// <see cref="TrayState.Error"/> rather than being silently tolerated.
/// </remarks>
public interface IStateStore
{
    /// <summary>
    /// Writes <c>(activeOwner, seq)</c> atomically — temp file plus replace, never an
    /// in-place rewrite, so a crash mid-write cannot produce a truncated latch.
    /// </summary>
    void SaveState(ReadOnlySpan<byte> activeOwner, ulong seq);

    /// <summary>
    /// Loads persisted state at startup so a reboot does not reset arbitration.
    /// </summary>
    /// <returns><see langword="false"/> if state is absent or fails the cross-file
    /// <c>pairId</c> check, which raises <see cref="TrayState.Error"/>.</returns>
    bool TryLoadState(out byte[] activeOwner, out ulong seq);
}
