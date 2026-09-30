namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Raw file I/O, and nothing else. See <c>docs/review-2026-09-28.md</c> finding B-4.
/// </summary>
/// <remarks>
/// <para>
/// This seam exists so that the *policy* around the three persisted files - the JSON
/// shapes of <c>docs/design.md</c> §7.3 and §7.5, the write-before-mutate ordering, the
/// cross-file <c>pairId</c> check, and ledger replay - can live in
/// <c>SoloSpeaker.Core</c> and therefore be tested. Before the seam existed, all of it sat
/// in <c>SoloSpeaker.App</c>, which targets <c>net10.0-windows</c> and so cannot be
/// referenced from the platform-neutral test project. The plan claimed CI could prove
/// ledger replay; it could not.
/// </para>
/// <para>
/// Implementations are a thin wrapper over <c>System.IO</c>. Anything that reasons about
/// what the bytes <em>mean</em> belongs above this line.
/// </para>
/// </remarks>
public interface IFileStore
{
    /// <summary>Reads a file, or <see langword="null"/> if it does not exist.</summary>
    byte[]? Read(string path);

    /// <summary>
    /// Writes atomically - temp file plus replace, never an in-place rewrite - so that a
    /// crash mid-write cannot leave a truncated latch or a half-written ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Also durable</b>, as of design revision 11. The contents must be on stable storage
    /// before the replace publishes them, and the replace itself must be flushed before this
    /// returns. The guarantee lives on the seam rather than inside one implementation so a
    /// fake has to model it, and because §7.3 states it as a requirement: the ledger entry is
    /// "written before <c>SetMute</c>, flushed to disk, then the mutation is applied".
    /// </para>
    /// <para>
    /// Atomicity alone is not enough for the ledger. Ordering is what makes §7.3's "an entry
    /// may describe a mute that never happened" the safe direction; without the flush a
    /// power cut inside the write-back window - matrix row D11 pulls the power - loses the
    /// entry while the mute itself survives in the audio stack, which is a stranded mute
    /// with no record and nothing able to repair it.
    /// </para>
    /// </remarks>
    void WriteAtomic(string path, ReadOnlySpan<byte> contents);

    /// <summary>Deletes a file. Succeeds silently if it is already absent.</summary>
    void Delete(string path);

    /// <summary>
    /// Whether a temp file from an interrupted <see cref="WriteAtomic"/> is present. Used
    /// by the §10 recovery case asserting that an interrupted write leaves the previous
    /// state intact.
    /// </summary>
    bool PendingWriteExists(string path);

    /// <summary>
    /// Deletes an orphaned temp file from an interrupted <see cref="WriteAtomic"/>.
    /// </summary>
    /// <remarks>
    /// The matched pair to <see cref="PendingWriteExists"/>. The temp file's name is this
    /// implementation's private business, so a caller above the seam has no path to hand to
    /// <see cref="Delete"/> without duplicating the naming convention on the wrong side of
    /// the line. Making <see cref="WriteAtomic"/> self-healing would be tidier but removes
    /// the observation point §4.5's "temp file present but replace never happened" row
    /// asserts against.
    /// </remarks>
    void DiscardPendingWrite(string path);
}
