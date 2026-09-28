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
    void WriteAtomic(string path, ReadOnlySpan<byte> contents);

    /// <summary>Deletes a file. Succeeds silently if it is already absent.</summary>
    void Delete(string path);

    /// <summary>
    /// Whether a temp file from an interrupted <see cref="WriteAtomic"/> is present. Used
    /// by the §10 recovery case asserting that an interrupted write leaves the previous
    /// state intact.
    /// </summary>
    bool PendingWriteExists(string path);
}
