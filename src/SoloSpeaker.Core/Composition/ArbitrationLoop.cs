using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.Composition;

/// <summary>
/// The deterministic cycle: hold the arbitration state, dispatch one event into the reducer,
/// drain its effects, and publish what the rest of the app reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not thread-safe. All entry points are serialized by the caller.</b> In production
/// events arrive from a socket callback, a cadence timer, a hotkey message pump and a device
/// notification - different threads - while the two-node harness is single-threaded by
/// design and could therefore never observe a torn state. The contract is stated rather than
/// discovered: the host serializes, and work item 6's host is where that happens.
/// </para>
/// <para>
/// <b>The loop never schedules itself.</b> A host posts <see cref="ArbitrationEvent.Tick"/>
/// at <see cref="ArbitrationTunables.HeartbeatCadence"/>. Putting a timer here would give the
/// cadence two homes - one in the tunables and one in whatever interval the host chose.
/// </para>
/// <para>
/// It owns exactly bytes to event to reduce to effects, and the state it reduces. Actuation,
/// ledger replay, hotkey registration, tray rendering and pairing are composed
/// <em>alongside</em> it by the host, never <em>into</em> it - see <c>AGENTS.md</c> §3.
/// </para>
/// </remarks>
public sealed class ArbitrationLoop : IProximitySource
{
    private readonly IClock _clock;
    private readonly IConfigStore _config;
    private readonly EffectExecutor _executor;
    private readonly ArbitrationTunables _tunables;

    /// <summary>Creates a loop over its two seams and an effect executor.</summary>
    /// <remarks>
    /// The constructor does not reduce. It used to prime itself with a <see
    /// cref="ArbitrationEvent.Tick"/> whose effects it then discarded - which stamped
    /// <c>LastBroadcastAt</c> for a broadcast that never reached the executor, so the state
    /// recorded a beat that never went out and the first real one was a full cadence late.
    /// Invisible while <see cref="IPeerTransport"/> had no implementation; wire-visible from
    /// work item 6. The host posts the first tick like every other one, which makes "the
    /// executor is the only place effects are drained" an invariant rather than a convention.
    /// </remarks>
    public ArbitrationLoop(
        IClock clock,
        IConfigStore config,
        EffectExecutor executor,
        ArbitrationState? initialState = null,
        ArbitrationTunables? tunables = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(executor);

        _clock = clock;
        _config = config;
        _executor = executor;
        _tunables = tunables ?? ArbitrationTunables.Default;

        State = initialState ?? ArbitrationState.Fresh();
        LastResult = Reducer.Initial(Context(), State, clock.Elapsed);
    }

    /// <summary>The current arbitration state.</summary>
    public ArbitrationState State { get; private set; }

    /// <summary>The most recent reduction, including the derived values §5.5 and §7.4 define.</summary>
    public ReducerResult LastResult { get; private set; }

    /// <summary>
    /// Whether the §7.7 pairing window is open. The host owns its lifetime; work item 10
    /// drives it.
    /// </summary>
    public bool PairingWindowOpen { get; set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Design §8 makes this seam "a projection over reducer state, not a second authority" in
    /// phase 1. It reads the value the reducer already computed rather than recomputing,
    /// because presence cannot live wholly outside the reducer: §7.6's observation latch is
    /// set by an accepted non-<c>bye</c> datagram and must survive a <c>bye</c> clearing
    /// presence.
    /// </remarks>
    public bool PeerPresent => LastResult.PeerPresent;

    /// <summary>Dispatches one event, drains its effects, and returns the reduction.</summary>
    public ReducerResult Post(ArbitrationEvent arbitrationEvent)
    {
        ArgumentNullException.ThrowIfNull(arbitrationEvent);

        ArbitrationState previous = State;
        ReducerResult result = Reduce(arbitrationEvent);

        Publish(arbitrationEvent, result, previous, ingress: null, peerVersion: null);

        return result;
    }

    /// <summary>
    /// The reduction itself, without publication.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Post"/> so that <see cref="Receive"/> can publish <b>once</b>,
    /// carrying the ingress verdict that produced the event. Publishing from both would give
    /// a datagram-driven cycle two observations, one of them missing the verdict.
    /// </remarks>
    private ReducerResult Reduce(ArbitrationEvent arbitrationEvent)
    {
        ReducerResult result = Reducer.Reduce(Context(), State, arbitrationEvent, _clock.Elapsed);
        State = result.State;
        LastResult = result;

        ErrorCause executionFault = _executor.Execute(result.Effects);

        // Step three of the per-cycle contract. It follows the effect drain because §5.5's
        // predicate is derived from the state the drain just persisted and broadcast, and it
        // runs on every reduction rather than only on ticks - a peer claim must not leave
        // this machine audible for up to a full cadence after it should have gone quiet.
        ErrorCause actuationFault = _executor.Reconcile(result.ShouldMute);

        if (executionFault == ErrorCause.None)
        {
            executionFault = actuationFault;
        }

        if (executionFault != ErrorCause.None)
        {
            // Re-entered as an ordinary event so the tray reports it through the one path
            // §7.4 defines, rather than by a side channel.
            result = Reducer.Reduce(
                Context(), State, new ArbitrationEvent.ErrorRaised(executionFault), _clock.Elapsed);
            State = result.State;
            LastResult = result;
        }

        return result;
    }

    /// <summary>
    /// Raised once per cycle, after everything that cycle is going to do.
    /// </summary>
    /// <remarks>
    /// The fourth step of the contract this type's remarks describe: publish what the rest of
    /// the app reads. One subscriber is wired by the host, which fans out to work item 8's
    /// log and work item 9's tray - keeping the subscriber list on the host's side of
    /// <c>AGENTS.md</c> §3's boundary rather than turning this into an event bus.
    /// </remarks>
    public event Action<CycleObservation>? CyclePublished;

    private void Publish(
        ArbitrationEvent arbitrationEvent,
        ReducerResult result,
        ArbitrationState previous,
        IngressResult? ingress,
        int? peerVersion)
    {
        if (CyclePublished is not { } subscribers)
        {
            return;
        }

        // A subscriber is a diagnostic. It must not be able to fault the cycle that fed it,
        // and the sink contract already says writes never throw - this is the belt to that
        // brace, because an exception here would surface as a failed reduction.
        try
        {
            subscribers(new CycleObservation(arbitrationEvent, result, previous, ingress, peerVersion));
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
        }
    }

    /// <summary>
    /// Runs one received datagram through ingress and, if it means anything, the reducer.
    /// </summary>
    /// <returns>
    /// The ingress outcome, so the caller can count rejection reasons. The reduction, if any,
    /// is in <see cref="LastResult"/>.
    /// </returns>
    public IngressResult Receive(ReadOnlySpan<byte> datagram)
    {
        if (!_config.TryGetPairKey(out byte[] pairKey))
        {
            Post(new ArbitrationEvent.ErrorRaised(ErrorCause.ConfigUnreadable));
            return IngressResult.Unreadable;
        }

        IngressContext context = DatagramRouter.ContextFor(State, _config, PairingWindowOpen);
        IngressOutcome outcome = IngressPipeline.Evaluate(datagram, context, pairKey);

        if (DatagramRouter.Route(outcome) is { } arbitrationEvent)
        {
            ArbitrationState previous = State;
            ReducerResult result = Reduce(arbitrationEvent);
            Publish(arbitrationEvent, result, previous, outcome.Result, outcome.PeerVersion);
        }
        else
        {
            // A datagram that reduces to nothing is still evidence. Rejections are what the
            // per-minute rollup counts, and a dropped datagram that nobody observes is the
            // whole failure ADR 0015 exists to prevent.
            ObserveIngressOnly(outcome.Result, outcome.PeerVersion);
        }

        return outcome.Result;
    }

    /// <summary>
    /// Raised for an ingress verdict that produced no reduction, so drops are still counted.
    /// </summary>
    public event Action<IngressResult, int?>? IngressObserved;

    private void ObserveIngressOnly(IngressResult result, int? peerVersion)
    {
        try
        {
            IngressObserved?.Invoke(result, peerVersion);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
        }
    }

    private ArbitrationContext Context() => ArbitrationContext.ForEvent(_config.Roster, _tunables);
}
