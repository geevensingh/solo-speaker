namespace SoloSpeaker.App.Hosting;

/// <summary>
/// The single thread every arbitration mutation runs on.
/// </summary>
/// <remarks>
/// <para>
/// <c>ArbitrationLoop</c> is documented as not thread-safe, with all entry points serialized
/// by the caller. In production events arrive from a socket receive thread, a cadence timer,
/// a hotkey message pump and - from work item 7 - a COM device-notification thread. This is
/// the seam that makes "the caller serializes" one identifiable thread rather than a hope.
/// </para>
/// <para>
/// It is an interface only so that the ordering guarantees built on it -
/// <see cref="DepartureAnnouncer"/>'s state-datagram-before-<c>bye</c> rule in particular -
/// are testable without standing up a real Win32 message pump. Production has exactly one
/// implementation, <see cref="HostWindow"/>.
/// </para>
/// </remarks>
public interface IDispatchTarget
{
    /// <summary>Queues work and returns immediately.</summary>
    void Post(Action work);

    /// <summary>
    /// Runs everything queued so far, on the calling thread, and returns.
    /// </summary>
    /// <remarks>
    /// Used once, during <c>WM_ENDSESSION</c>: Windows is about to terminate the process, so
    /// the departure datagrams have to actually leave before the handler returns rather than
    /// sitting in a queue nobody will pump again.
    /// </remarks>
    void Drain(TimeSpan timeout);
}
