namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Persistence for the replicated latch of <c>docs/design.md</c> §7.5 - and only that.
/// </summary>
/// <remarks>
/// <para>
/// Configuration moved to <see cref="IConfigStore"/> per <c>docs/review-2026-09-28.md</c>
/// finding B-4. This interface previously documented a cross-file <c>pairId</c> check it
/// had no surface to express, because the roster it described lived in a file it did not
/// expose.
/// </para>
/// <para>
/// The split between the two files remains a known hazard: the roster lives in
/// <c>config.json</c> while <c>activeOwner</c> lives in <c>state.json</c>, and §5.5's
/// invariant spans both. Both carry the same <c>pairId</c> and a mismatch raises
/// <see cref="TrayState.Error"/> - see <see cref="IConfigStore.MatchesState"/>.
/// </para>
/// </remarks>
public interface IStateStore
{
    /// <summary>
    /// Writes <c>(activeOwner, seq)</c> atomically via
    /// <see cref="IFileStore.WriteAtomic"/>, so a crash mid-write cannot produce a
    /// truncated latch.
    /// </summary>
    void SaveState(ReadOnlySpan<byte> activeOwner, ulong seq);

    /// <summary>
    /// Loads persisted state at startup so a reboot does not reset arbitration.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if state is absent or unreadable. An absent state file is
    /// ordinary on first run; an unreadable one raises <see cref="TrayState.Error"/>.
    /// </returns>
    bool TryLoadState(out byte[] activeOwner, out ulong seq);

    /// <summary>
    /// The <c>pairId</c> recorded alongside the state, for the cross-file check of §7.5.
    /// </summary>
    byte[]? StatePairId { get; }
}
