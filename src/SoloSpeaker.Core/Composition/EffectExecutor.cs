using System.Collections.Immutable;
using SoloSpeaker.Core.Abstractions;
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

    /// <summary>Creates an executor over the four seams an effect can reach.</summary>
    public EffectExecutor(IClock clock, IConfigStore config, IStateStore stateStore, IPeerTransport transport)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(transport);

        _clock = clock;
        _config = config;
        _stateStore = stateStore;
        _transport = transport;
    }

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
