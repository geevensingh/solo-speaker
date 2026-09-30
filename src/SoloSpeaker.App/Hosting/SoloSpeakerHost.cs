using SoloSpeaker.App.MuteActuator;
using SoloSpeaker.App.PeerLink;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.MuteActuator;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.StateStore;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// The running application: the composition root that drives the arbitration cycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>It owns exactly three things</b> - process lifetime and the message pump, the
/// construction order, and the shutdown order. Everything else is a <em>participant</em>:
/// constructed beside the cycle, handed the <see cref="EventDispatch"/> plus whatever it
/// needs, and never reached into.
/// </para>
/// <para>
/// That rule is load-bearing rather than decorative. <c>AGENTS.md</c> §3 explains that
/// without an explicit boundary "every later work item has a default answer, and the default
/// is 'add it to the loop'" - and work item 4's review dissolved a <c>SoloSpeakerNode</c>
/// for being exactly that. A host with no stated shape recreates the same object one layer
/// out: seven work items remain, and the actuator, the ledger, the logger, the tray, the
/// hotkey, the pairing window, the resume handler, the mutex and the shutdown channel all
/// have this constructor as their path of least resistance.
/// </para>
/// <para>
/// Binding lives here rather than in <see cref="StartupSequence"/> because binding is not a
/// persistence-ordering step: it needs the port, which only exists once configuration is
/// loaded, and it produces a socket, which <see cref="StartupResult"/> cannot carry. Work
/// items 7 and 12 produce resources with the same shape and land in the same place.
/// </para>
/// </remarks>
public sealed class SoloSpeakerHost : IDisposable
{
    private readonly HostWindow _window;
    private readonly UdpPeerTransport _transport;
    private readonly EventDispatch _dispatch;
    private readonly HeartbeatPump _pump;
    private readonly ShutdownSequence _shutdown;
    private readonly EndpointWatcher _endpointWatch;
    private readonly IMuteActuator _actuator;
    private readonly MuteReconciler _reconciler;

    private bool _disposed;

    /// <summary>
    /// Builds the cycle and its participants from a completed startup.
    /// </summary>
    /// <remarks>
    /// The construction order is the contract: the window exists before the dispatch, the
    /// dispatch before anything that posts to it, and the transport last, because its first
    /// successful bind raises an event that has to have somewhere to go.
    /// </remarks>
    public SoloSpeakerHost(
        HostWindow window,
        JsonConfigStore config,
        JsonStateStore stateStore,
        IClock clock,
        ArbitrationState initialState,
        ErrorCause startupCause,
        TransportEndpoint endpoint,
        ILedger ledger,
        IMuteActuator actuator,
        ArbitrationTunables? tunables = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(actuator);

        ArbitrationTunables resolved = tunables ?? ArbitrationTunables.Default;

        _window = window;
        _transport = new UdpPeerTransport(endpoint);
        _actuator = actuator;

        _reconciler = new MuteReconciler(actuator, ledger, clock, resolved);
        var executor = new EffectExecutor(clock, config, stateStore, _transport, _reconciler);
        var loop = new ArbitrationLoop(clock, config, executor, initialState, resolved);

        _dispatch = new EventDispatch(loop, window);

        // One tunables instance for both, so the timer and the reducer's own due-check
        // cannot disagree about what the cadence is.
        _pump = new HeartbeatPump(_dispatch, resolved);

        // Constructed after the dispatch because its callback posts to it, and before the
        // shutdown sequence because that sequence must be able to stop it.
        _endpointWatch = new EndpointWatcher(OnDefaultRenderChanged);

        var departure = new DepartureAnnouncer(_dispatch, executor);
        _shutdown = new ShutdownSequence(
            _pump, departure, _dispatch, _endpointWatch.Dispose, ledger, actuator);

        WireParticipants(executor, startupCause);
    }

    /// <summary>The dispatch, for participants later work items add beside the cycle.</summary>
    public EventDispatch Dispatch => _dispatch;

    /// <summary>Binds the socket and starts the cadence.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _transport.Start();
        _pump.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Run();
        _transport.Dispose();
    }

    private void WireParticipants(EffectExecutor executor, ErrorCause startupCause)
    {
        _transport.DatagramReceived += datagram => _dispatch.Receive(datagram);

        // §9.1's decision D-1: the user reached for the volume flyout, which is a claim.
        // Queued rather than reduced inline - the executor raises this from inside a
        // reduction, and posting keeps the follow-up a subsequent cycle rather than a
        // re-entrant one.
        executor.ExternalUnmuteObserved += () =>
            _dispatch.Post(new ArbitrationEvent.ManualClaim(ClaimSource.ExternalUnmute));

        _transport.Bound += () =>
        {
            // §7.6, design revision 10. The window is already open - StartupDecision opened
            // it - so this restarts rather than enters, which preserves the latch and
            // re-measures the 12s from the moment the network actually existed. A re-bind
            // after a network loss gets the same treatment for the same reason: the machine
            // has just been off the air for an unknown period.
            _dispatch.Post(new ArbitrationEvent.QuarantineRestarted());
            _dispatch.Post(new ArbitrationEvent.ErrorResolved(ErrorCause.TransportUnavailable));
        };

        _transport.Unavailable += () =>
            _dispatch.Post(new ArbitrationEvent.ErrorRaised(ErrorCause.TransportUnavailable));

        // Raised by both the OS notification and a self-initiated re-bind, so work item 11
        // consumes one edge rather than two. Nothing consumes it yet.
        _transport.NetworkChanged += () => { };

        _window.SessionEnding += () => _shutdown.Run();

        if (startupCause != ErrorCause.None)
        {
            _dispatch.Post(new ArbitrationEvent.ErrorRaised(startupCause));
        }
    }

    /// <summary>
    /// §7.3's device change. Arrives on a COM thread, so it only ever queues.
    /// </summary>
    /// <remarks>
    /// Release-before-acquire. The failure mode of releasing first is a brief audible window
    /// on a device nobody is listening to; the failure mode of acquiring first is a stranded
    /// mute on the device the user just walked away from, which survives a reboot. Matrix
    /// row D1. A kill between the release and the re-mute leaves nothing muted, which is
    /// Goal 1's direction.
    /// </remarks>
    private void OnDefaultRenderChanged() => _dispatch.PostWork(() =>
    {
        if (_actuator.CurrentEndpointId is { } previous)
        {
            ReconcileOutcome released = _reconciler.Release(previous);

            if (released.Cause != ErrorCause.None)
            {
                _dispatch.Post(new ArbitrationEvent.ErrorRaised(released.Cause));
            }
        }

        _actuator.Retarget();

        // Drives the reconcile that mutes the new endpoint if §5.5 still wants it muted.
        _dispatch.Post(new ArbitrationEvent.Tick());
    });
}
