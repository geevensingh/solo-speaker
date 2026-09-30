using SoloSpeaker.Core.Diagnostics;

namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Where log entries go - ADR 0015's rolling local log.
/// </summary>
/// <remarks>
/// <para>
/// The eleventh seam, and the only one added since the original ten.
/// <see cref="IFileStore"/> cannot serve: it is atomic write-and-replace, which is the exact
/// opposite of what an append-only log needs, and routing a log through it would rewrite the
/// whole file once per line.
/// </para>
/// <para>
/// <b>Writes must not touch the disk.</b> The cycle that calls this runs on the single
/// dispatch thread that also handles <c>WM_ENDSESSION</c>, so a stalled write - a network
/// drive, an antivirus scan, a disk spinning up - would delay the departure <c>bye</c>, the
/// endpoint restore and the ledger clear. Implementations buffer; <see cref="Flush"/> is
/// where I/O is permitted, and the host calls it on the 2 s cadence.
/// </para>
/// <para>
/// <b>Neither may throw.</b> A diagnostic that can kill the message loop is worse than no
/// diagnostic: the ledger, not the log, is the crash-recovery record. Failures are counted
/// and disclosed rather than propagated - see the implementation.
/// </para>
/// </remarks>
public interface ILogSink : IDisposable
{
    /// <summary>Buffers one entry. Never touches the disk, never throws.</summary>
    void Write(LogEntry entry);

    /// <summary>Writes whatever is buffered. Never throws.</summary>
    void Flush();
}
