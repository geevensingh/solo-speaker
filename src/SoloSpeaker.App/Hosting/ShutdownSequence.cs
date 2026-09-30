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

    /// <summary>Creates a sequence over the three things work item 6 shuts down.</summary>
    public ShutdownSequence(HeartbeatPump pump, DepartureAnnouncer departure, EventDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(departure);
        ArgumentNullException.ThrowIfNull(dispatch);

        _pump = pump;
        _departure = departure;
        _dispatch = dispatch;
    }

    /// <summary>Runs the steps in order.</summary>
    public void Run()
    {
        StopCadence();
        AnnounceDeparture();
        RestoreEndpoints();
        ClearLedger();
        ReleaseSingleInstance();
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
    /// §7.3: undo every mute this process made, using the ledger as the record. Work item 7.
    /// </summary>
    private static void RestoreEndpoints()
    {
    }

    /// <summary>
    /// §7.3: the ledger is only cleared once the endpoints it describes are actually
    /// restored, so this follows rather than precedes. Work item 7.
    /// </summary>
    private static void ClearLedger()
    {
    }

    /// <summary>
    /// ADR 0012's mutex, released last so no second instance can start while this one is
    /// still restoring audio. Work item 12.
    /// </summary>
    private static void ReleaseSingleInstance()
    {
    }
}
