using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// Drives §7.1's 2s cadence by posting <see cref="ArbitrationEvent.Tick"/>.
/// </summary>
/// <remarks>
/// <para>
/// The loop deliberately never schedules itself: "putting a timer here would give the
/// cadence two homes - one in the tunables and one in whatever interval the host chose". The
/// same trap is available one level up, so the pump does not carry its own interval either -
/// it is handed the <em>same</em> <see cref="ArbitrationTunables"/> instance the loop was
/// built with. If the timer and the reducer's own due-check could disagree, beats would be
/// silently dropped or doubled and the only symptom would be flaky presence.
/// </para>
/// <para>
/// It also posts the very first tick. The loop used to prime itself with one in its
/// constructor and discard the effects, which stamped a broadcast that never went out; now
/// every reduction in the process arrives through the same entry point.
/// </para>
/// </remarks>
public sealed class HeartbeatPump : IDisposable
{
    private readonly EventDispatch _dispatch;
    private readonly ArbitrationTunables _tunables;

    private System.Threading.Timer? _timer;
    private bool _disposed;

    /// <summary>Creates a pump over the dispatch, using the loop's own tunables.</summary>
    public HeartbeatPump(EventDispatch dispatch, ArbitrationTunables tunables)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(tunables);

        _dispatch = dispatch;
        _tunables = tunables;
    }

    /// <summary>Posts the priming tick and starts the cadence.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _timer = new System.Threading.Timer(
            _ => _dispatch.Post(new ArbitrationEvent.Tick()), null, TimeSpan.Zero, _tunables.HeartbeatCadence);
    }

    /// <summary>
    /// Stops the cadence. Called first during shutdown so that a beat cannot race the
    /// departure datagrams - see <see cref="ShutdownSequence"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
