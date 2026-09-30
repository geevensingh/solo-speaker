using System.Collections.Immutable;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.MuteActuator;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.Composition;

/// <summary>
/// Executes the ordered effects of one reduction against the seams.
/// </summary>
/// <remarks>
/// <para>
/// A seam of its own so that work item 7 fills a named slot rather than choosing an order.
/// §5.1 orders the write before the send and §7.3 orders the ledger write before the
/// mutation; those are two different orderings in two different components, and the one
/// thing that must not happen is a later row inventing a third by accident.
/// </para>
/// <para>
/// The per-cycle contract is: <b>reduce, drain effects in order (persist, then broadcast),
/// reconcile actuation, publish derived values.</b> Steps three and four are no-ops here -
/// work item 7 brings <c>IMuteActuator</c> and <c>ILedger</c>, work item 9 brings the tray -
/// and they are named now so that the order is a decision rather than an emergent property.
/// </para>
/// </remarks>
public sealed class EffectExecutor
{
    private readonly IClock _clock;
    private readonly IConfigStore _config;
    private readonly IStateStore _stateStore;
    private readonly IPeerTransport _transport;
    private readonly MuteReconciler? _reconciler;

    /// <summary>Creates an executor over the seams an effect can reach.</summary>
    /// <param name="clock">The monotonic clock. §5.4 orders by event count, never by time.</param>
    /// <param name="config">The roster and the pairing secret.</param>
    /// <param name="stateStore">§7.5's persisted latch.</param>
    /// <param name="transport">§7.1's datagram transport.</param>
    /// <param name="reconciler">
    /// Step three of the per-cycle contract, filled by work item 7. Optional because the
    /// two-node harness exercises arbitration without an audio device; when it is absent,
    /// actuation is simply not reconciled.
    /// </param>
    public EffectExecutor(
        IClock clock,
        IConfigStore config,
        IStateStore stateStore,
        IPeerTransport transport,
        MuteReconciler? reconciler = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(transport);

        _clock = clock;
        _config = config;
        _stateStore = stateStore;
        _transport = transport;
        _reconciler = reconciler;
    }

    /// <summary>
    /// Raised when a reconcile concluded that the user unmuted an endpoint this app had
    /// muted, which §9.1's decision D-1 treats as a manual claim.
    /// </summary>
    /// <remarks>
    /// An event rather than a direct post because the reducer is mid-reduction when this
    /// fires. The host queues the claim through its dispatch, so the follow-up is a
    /// subsequent cycle rather than a re-entrant one.
    /// </remarks>
    public event Action? ExternalUnmuteObserved;

    /// <summary>
    /// Runs the effects in the order the reducer produced them.
    /// </summary>
    /// <returns>
    /// <see cref="ErrorCause.StatePersistFailed"/> if a write failed with the process still
    /// alive, otherwise <see cref="ErrorCause.None"/>. The caller posts it back as an event;
    /// a peer holding a claim our disk never recorded is not something to swallow.
    /// </returns>
    public ErrorCause Execute(ImmutableArray<ArbitrationEffect> effects)
    {
        ErrorCause cause = ErrorCause.None;

        foreach (ArbitrationEffect effect in effects)
        {
            switch (effect)
            {
                case ArbitrationEffect.PersistState persist:
                    if (!TryPersist(persist))
                    {
                        cause = ErrorCause.StatePersistFailed;
                    }

                    break;

                case ArbitrationEffect.Broadcast broadcast:
                    Send(broadcast.ActiveOwner, broadcast.Seq, broadcast.MicLive, bye: false);
                    break;

                default:
                    throw new NotSupportedException($"Unhandled effect '{effect.GetType().Name}'.");
            }
        }

        return cause;
    }

    /// <summary>
    /// Step three of the per-cycle contract: make the audio endpoint match what §5.5 just
    /// derived.
    /// </summary>
    /// <remarks>
    /// Here rather than in the host because this type exists to keep the orderings in one
    /// place - §5.1 orders the persist before the send, §7.3 orders the ledger write before
    /// the mutation - and because <c>SoloSpeaker.Core.Tests</c> is what mutation-tests the
    /// reconcile table. "Composed alongside the cycle, never into it" is satisfied by
    /// injection: the reconciler arrives through the constructor exactly as
    /// <see cref="IPeerTransport"/> does, while the COM that touches the device stays in
    /// <c>SoloSpeaker.App</c>.
    /// </remarks>
    /// <returns>A cause to raise, or <see cref="ErrorCause.None"/>.</returns>
    public ErrorCause Reconcile(bool shouldMute)
    {
        if (_reconciler is null)
        {
            return ErrorCause.None;
        }

        ReconcileOutcome outcome = _reconciler.Reconcile(shouldMute);

        if (outcome.Claim)
        {
            ExternalUnmuteObserved?.Invoke();
        }

        return outcome.Cause;
    }

    /// <summary>
    /// Builds, signs and sends one datagram. The single construction path into the frozen v1
    /// format - work item 6's departure datagrams call this with <c>bye: true</c>.
    /// </summary>
    /// <remarks>
    /// <c>pairKey</c> is fetched per use and never held. <c>IngressContext</c> and
    /// <c>IConfigStore</c> both go to documented lengths to keep the secret out of any type
    /// whose generated <c>ToString</c> could render it, and a long-lived field here would be
    /// the same disclosure in a new shape - <c>AGENTS.md</c> §6 is explicit that debug
    /// logging counts.
    /// </remarks>
    public void Send(Identity.MachineId activeOwner, ulong seq, bool micLive, bool bye)
    {
        if (!_config.TryGetPairKey(out byte[] pairKey))
        {
            // §7.4: an undecryptable key raises error with a re-pair cause rather than
            // crashing or falling back. Nothing can be signed, so nothing is sent.
            return;
        }

        PeerDatagram datagram = DatagramRouter.DatagramFor(
            _config, activeOwner, seq, micLive, bye, _clock.UtcNow);

        byte[] buffer = new byte[WireProtocol.MaxDatagramBytes];
        int length = DatagramCodec.Encode(datagram, pairKey, buffer);

        _transport.Send(buffer.AsMemory(0, length));
    }

    private bool TryPersist(ArbitrationEffect.PersistState persist)
    {
        try
        {
            _stateStore.SaveState(persist.ActiveOwner, persist.Seq);
            return true;
        }
        catch (IOException)
        {
            // Surfaced as a cause the caller raises, never swallowed. A full disk or a file
            // locked by a backup agent is exactly the case §7.5 had no channel to report.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
