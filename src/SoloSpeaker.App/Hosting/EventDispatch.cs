using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// Owns the arbitration loop and is the only thing that can reach it.
/// </summary>
/// <remarks>
/// <para>
/// Work item 4's review asked for a dispatch seam and named work item 6 as its implementer.
/// The remedy as written put an <c>IEventDispatch</c> inside the loop - but it was written
/// for a <c>SoloSpeakerNode</c> that work item 4's own review then dissolved, and it does not
/// transplant. Two reasons, both fatal:
/// </para>
/// <para>
/// The receive path could not go through it. A socket produces bytes, and the loop's
/// mutation entry point takes a <c>ReadOnlySpan&lt;byte&gt;</c> - a <c>ref struct</c>, which
/// cannot be captured in a queued closure at all. A dispatch typed over
/// <see cref="ArbitrationEvent"/> would have serialized the cadence and left the one
/// genuinely cross-thread path unserialized: precisely the race it existed to fix.
/// </para>
/// <para>
/// And it would have broken a synchronous contract. <c>Post</c> returns a
/// <c>ReducerResult</c>, <c>Receive</c> returns an <c>IngressResult</c>, and <c>Receive</c>'s
/// own documentation promises the reduction is in <c>LastResult</c> when it returns.
/// Deferring the reduction makes all three false, along with the tests that read them.
/// </para>
/// <para>
/// So the relationship is inverted. The loop ships unchanged and knows nothing about
/// threads; this owns the queue <em>and</em> the loop, and holds the only reference to it.
/// "All entry points are serialized by the caller" becomes true by construction rather than
/// by discipline. <c>AGENTS.md</c> §3 puts thread marshalling in the host for the same
/// reason it puts the timer there.
/// </para>
/// </remarks>
public sealed class EventDispatch
{
    private readonly ArbitrationLoop _loop;
    private readonly IDispatchTarget _target;

    /// <summary>Creates a dispatch over one loop and the thread it will run on.</summary>
    public EventDispatch(ArbitrationLoop loop, IDispatchTarget target)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(target);

        _loop = loop;
        _target = target;
    }

    /// <summary>
    /// The loop's current state. Only safe to read from the dispatch thread - which is why
    /// everything that reads it does so from inside posted work.
    /// </summary>
    public ArbitrationLoop Loop => _loop;

    /// <summary>Queues one event.</summary>
    public void Post(ArbitrationEvent arbitrationEvent)
    {
        ArgumentNullException.ThrowIfNull(arbitrationEvent);

        _target.Post(() => _loop.Post(arbitrationEvent));
    }

    /// <summary>Queues one received datagram for the full ingress and reduce cycle.</summary>
    /// <remarks>
    /// The bytes are copied because they are about to outlive the call. The transport reads
    /// into a reused buffer, so anything queued by reference would change under its reader
    /// on the next receive - a corruption that would present as a sporadic signature
    /// failure rather than as anything pointing at threading.
    /// </remarks>
    public void Receive(ReadOnlyMemory<byte> datagram)
    {
        byte[] copy = datagram.ToArray();

        _target.Post(() => _loop.Receive(copy));
    }

    /// <summary>Queues arbitrary host work on the same thread, preserving order with events.</summary>
    /// <remarks>
    /// <see cref="DepartureAnnouncer"/> is the reason this exists. §7.1 requires a departing
    /// machine's final state datagram to precede its <c>bye</c>s; if the byes were sent
    /// directly from the message-pump thread while a pending claim was still queued here,
    /// the order would invert and the peer would re-establish presence for a machine that
    /// had already gone.
    /// </remarks>
    public void PostWork(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _target.Post(work);
    }

    /// <summary>Runs everything queued so far. See <see cref="IDispatchTarget.Drain"/>.</summary>
    public void Drain(TimeSpan timeout) => _target.Drain(timeout);
}
