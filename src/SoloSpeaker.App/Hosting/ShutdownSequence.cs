using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// The ordered shutdown steps, mirroring <see cref="StartupSequence"/>.
/// </summary>
/// <remarks>
/// <para>
/// Work item 6 is the first work item that <em>has</em> a shutdown order, and the order is
/// already spread across three documents and four work items: §7.6's unmute table orders
/// restore-endpoints, clear-ledger, send-<c>bye</c>; §7.1 and <c>wire-format.md</c> order the
/// final state datagram before the <c>bye</c>s; the cadence has to stop first so a heartbeat
/// cannot race the departure; work item 7 adds the endpoint restore and the ledger clear;
/// work item 12 adds the mutex release.
/// </para>
/// <para>
/// <c>EffectExecutor</c> names the hazard this exists to prevent: "the one thing that must
/// not happen is a later work item inventing a third ordering by accident". Work item 5
/// solved exactly this for startup, and the steps are members rather than prose for the same
/// reason - a later work item fills a slot instead of re-deriving the order.
/// </para>
/// <para>
/// Note the apparent inversion against §7.6's table: the departure is announced <em>before</em>
/// the endpoint is restored. It is not an inversion. §7.6 orders the steps of an
/// <em>unmute</em>, which is about this machine's own audio; the <c>bye</c> is about the
/// peer's. Sending it first costs nothing and buys the peer the earliest possible unmute,
/// which is the difference §10 measures between a graceful exit and a hard kill.
/// </para>
/// </remarks>
public sealed class ShutdownSequence
{
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(750);

    private readonly HeartbeatPump _pump;
    private readonly DepartureAnnouncer _departure;
    private readonly EventDispatch _dispatch;
    private readonly Action _stopEndpointWatch;
    private readonly ILedger _ledger;
    private readonly IMuteActuator _actuator;
    private readonly Logging.DiagnosticLog? _diagnostics;

    private bool _ran;

    /// <summary>Creates a sequence over the things work item 7 shuts down.</summary>
    public ShutdownSequence(
        HeartbeatPump pump,
        DepartureAnnouncer departure,
        EventDispatch dispatch,
        Action stopEndpointWatch,
        ILedger ledger,
        IMuteActuator actuator,
        Logging.DiagnosticLog? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(departure);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(stopEndpointWatch);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(actuator);

        _pump = pump;
        _departure = departure;
        _dispatch = dispatch;
        _stopEndpointWatch = stopEndpointWatch;
        _ledger = ledger;
        _actuator = actuator;
        _diagnostics = diagnostics;
    }

    /// <summary>Runs the steps in order. Idempotent.</summary>
    /// <remarks>
    /// The guard is not defensive programming. This is invoked twice on an ordinary logoff -
    /// once from the window's <c>WM_ENDSESSION</c> handler and again from the host's
    /// disposal after the message loop returns. Before work item 7 that double-sent a
    /// <c>bye</c>, which was untidy; now it would be a double audio mutation and a double
    /// ledger operation on the one path §7.6 names as the graceful unmute.
    /// </remarks>
    public void Run()
    {
        if (_ran)
        {
            return;
        }

        _ran = true;

        StopCadence();
        AnnounceDeparture();
        StopEndpointWatch();
        RestoreEndpoints();
        ReleaseSingleInstance();
        FlushDiagnostics();
    }

    /// <summary>
    /// Stops the 2s cadence first, so an ordinary heartbeat cannot be queued behind the
    /// departure and arrive after the <c>bye</c>s - which would look to the peer exactly
    /// like the machine coming back.
    /// </summary>
    private void StopCadence() => _pump.Dispose();

    /// <summary>
    /// §7.1's final state datagram and three <c>bye</c>s, then drains so they actually
    /// leave before the process does.
    /// </summary>
    private void AnnounceDeparture()
    {
        _departure.Announce();
        _dispatch.Drain(DrainBudget);
    }

    /// <summary>
    /// Unsubscribes from device notifications <b>before</b> restoring, so an endpoint change
    /// racing shutdown cannot re-target and re-mute an endpoint whose ledger entry the next
    /// step is about to clear.
    /// </summary>
    private void StopEndpointWatch() => _stopEndpointWatch();

    /// <summary>
    /// §7.3: undo every mute this process made, using the ledger as the record.
    /// </summary>
    /// <remarks>
    /// It reads the <em>ledger</em> rather than any in-memory reconciler state, which is
    /// what makes a mutation applied during <c>AnnounceDeparture</c>'s drain still covered.
    /// Replay also clears what it repairs, so there is no separate clear step: an entry that
    /// could not be restored is deliberately kept, and a second void method could not have
    /// expressed that constraint.
    /// </remarks>
    private void RestoreEndpoints() => _ledger.Replay(_actuator);

    /// <summary>
    /// ADR 0012's mutex, released last so no second instance can start while this one is
    /// still restoring audio. Owned by <c>Program</c>, which disposes it after this returns.
    /// </summary>
    private static void ReleaseSingleInstance()
    {
    }

    /// <summary>
    /// Writes whatever the log still holds. Last, because every step above emits.
    /// </summary>
    /// <remarks>
    /// Forced, so the partial minute is not lost - the most interesting minute of a log is
    /// usually the last one before something stopped. The flush also discloses any entries
    /// lost to write failures even if the sink has given up, so a graceful exit always says
    /// the log has a hole in it rather than leaving a permanent failure permanently silent.
    /// </remarks>
    private void FlushDiagnostics()
    {
        _diagnostics?.Note("host stopped");
        _diagnostics?.Flush(force: true);
    }
}
