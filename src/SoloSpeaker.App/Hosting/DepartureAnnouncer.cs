using SoloSpeaker.Core.Composition;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// §7.1's departure: the final state datagram, then three <c>bye</c>s about 50ms apart.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the whole point, and it is a queue property rather than a race.</b> §7.1
/// requires a machine claiming on its way out to broadcast its state datagram <em>first</em>
/// and only then the <c>bye</c>s. The obvious implementation - send the byes straight from
/// the window procedure - inverts that whenever a hotkey claim is still queued: the peer
/// clears presence on the <c>bye</c>, then the late state datagram re-establishes it under
/// §7.1's re-acquisition rule, and the peer mutes for a machine that has already gone, for
/// the full presence window. Manual matrix row A12 is exactly that scenario.
/// </para>
/// <para>
/// So everything goes through the same dispatch, in order, and the caller drains it. Three
/// byes at 50ms is about 150ms, comfortably inside the budget Windows allows after
/// <c>WM_ENDSESSION</c>.
/// </para>
/// <para>
/// One outcome is accepted rather than defended against: a process killed between the state
/// datagram and the first <c>bye</c> leaves the peer to time out the presence window
/// instead. That is Goal 1's safe direction - the peer unmutes - and the alternative would
/// be to send the byes first, which is the A12 defect above.
/// </para>
/// </remarks>
public sealed class DepartureAnnouncer
{
    private static readonly TimeSpan ByeSpacing = TimeSpan.FromMilliseconds(50);
    private const int ByeCount = 3;

    private readonly EventDispatch _dispatch;
    private readonly EffectExecutor _executor;

    /// <summary>Creates an announcer over the dispatch and the executor that owns sending.</summary>
    public DepartureAnnouncer(EventDispatch dispatch, EffectExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(executor);

        _dispatch = dispatch;
        _executor = executor;
    }

    /// <summary>
    /// Queues the departure. The caller drains the dispatch to make it actually leave.
    /// </summary>
    public void Announce()
    {
        // Read the state from inside the queue, not from the calling thread: by the time
        // this runs, any claim queued ahead of it has been reduced, and the datagram must
        // carry that claim rather than the state as it was when shutdown began.
        _dispatch.PostWork(() =>
        {
            Core.StateMachine.ArbitrationState state = _dispatch.Loop.State;

            // Sent unconditionally, even when a claim queued ahead of this has already
            // broadcast the same pair. §7.1's requirement is that a state datagram
            // *precedes* the byes, and sending one here makes that true on every path
            // rather than only on the paths where something else happened to do it. A peer
            // that receives the pair twice adopts nothing the second time - §5.4 only acts
            // on news - so the redundancy costs one datagram and buys an unconditional
            // guarantee. Suppressing it on a "did anyone already send one" check would tie
            // the departure's correctness to whatever else shared the queue.
            _executor.Send(state.ActiveOwner, state.Seq, state.SelfMicLive, bye: false);

            for (int bye = 0; bye < ByeCount; bye++)
            {
                // §7.1 sends three because UDP loses datagrams and a missed departure costs
                // the peer a full presence window of unnecessary silence. Spaced, because
                // three back-to-back sends are lost together by the same transient.
                Thread.Sleep(ByeSpacing);
                _executor.Send(state.ActiveOwner, state.Seq, state.SelfMicLive, bye: true);
            }
        });
    }
}
