using SoloSpeaker.Core;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Diagnostics;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.App.Logging;

/// <summary>
/// Turns what the cycle publishes into what ADR 0015 says to record.
/// </summary>
/// <remarks>
/// A participant in <c>AGENTS.md</c> §3's sense: constructed beside the cycle, handed the
/// seams it needs, never reached into. It subscribes to one publication point rather than
/// each component logging for itself, so the order of lines for a single reduction is a
/// decision rather than an emergent property of call order.
/// </remarks>
public sealed class DiagnosticLog
{
    private readonly ILogSink _sink;
    private readonly IClock _clock;
    private readonly IngressDropAggregator _drops = new();

    private TrayState _lastTray = TrayState.Alone;
    private bool _lastPresent;
    private MachineId _lastOwner = MachineId.None;
    private bool _seeded;

    /// <summary>Creates a log over the sink and the clock.</summary>
    public DiagnosticLog(ILogSink sink, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);

        _sink = sink;
        _clock = clock;
    }

    /// <summary>Records a free-form note - startup, shutdown, replay summaries.</summary>
    public void Note(string text) => _sink.Write(new LogEntry.Note(text) { At = _clock.UtcNow });

    /// <summary>Records one ledger action, for ADR 0015's "ledger writes, replays, and clears".</summary>
    public void LedgerActivity(string action, string endpointId, string detail) =>
        _sink.Write(new LogEntry.LedgerActivity(action, endpointId, detail) { At = _clock.UtcNow });

    /// <summary>Records a default-endpoint change.</summary>
    public void EndpointChanged(string? from, string? to) =>
        _sink.Write(new LogEntry.EndpointChanged(from, to) { At = _clock.UtcNow });

    /// <summary>Observes an ingress verdict that produced no reduction.</summary>
    public void ObserveIngress(IngressResult result) => _drops.Observe(result, _clock.Elapsed);

    /// <summary>Observes one turn of the cycle.</summary>
    public void Observe(CycleObservation observation)
    {
        if (observation.Ingress is { } ingress)
        {
            _drops.Observe(ingress, _clock.Elapsed);
        }

        ArbitrationState previous = observation.PreviousState;
        ReducerResult result = observation.Result;

        LogOwnership(result);
        LogPresence(previous, result, observation.Event);
        LogTray(result);
        LogQuarantine(previous, result);
    }

    /// <summary>
    /// Emits the elapsed minute's drop rollup and writes everything buffered.
    /// </summary>
    /// <remarks>
    /// Driven by the 2 s tick rather than by a timer of its own. A second timer would put the
    /// cadence in two places, which is the trap <c>ArbitrationLoop</c>'s remarks warn about,
    /// and the tick is already guaranteed to arrive.
    /// </remarks>
    public void Flush(bool force = false)
    {
        foreach (LogEntry entry in _drops.Flush(_clock.Elapsed, _clock.UtcNow, force))
        {
            _sink.Write(entry);
        }

        _sink.Flush();
    }

    private void LogOwnership(ReducerResult result)
    {
        // The source rides on the effect rather than being inferred, because four writers
        // produce identical state changes - see OwnershipSource.
        foreach (ArbitrationEffect effect in result.Effects)
        {
            if (effect is ArbitrationEffect.PersistState persist)
            {
                _sink.Write(new LogEntry.OwnershipChanged(persist.ActiveOwner, persist.Seq, persist.Source)
                {
                    At = _clock.UtcNow,
                });

                _lastOwner = persist.ActiveOwner;
            }
        }
    }

    private void LogPresence(ArbitrationState previous, ReducerResult result, ArbitrationEvent reduced)
    {
        bool present = result.PeerPresent;

        if (_seeded && present == _lastPresent)
        {
            return;
        }

        _lastPresent = present;

        // §10 wants a graceful exit distinguishable from a hard kill in the log: a bye is a
        // deliberate departure, a timeout is a crash, a walk out of range, or a peer on a
        // wire version we reject. That distinction is what makes manual row A9 answerable.
        bool viaBye = !present && reduced is ArbitrationEvent.PeerDeparted;

        if (_seeded)
        {
            _sink.Write(new LogEntry.PresenceChanged(present, viaBye) { At = _clock.UtcNow });
        }
    }

    private void LogTray(ReducerResult result)
    {
        if (_seeded && result.TrayState == _lastTray)
        {
            return;
        }

        if (_seeded)
        {
            _sink.Write(new LogEntry.TrayStateChanged(_lastTray, result.TrayState, result.ErrorCause)
            {
                At = _clock.UtcNow,
            });
        }

        _lastTray = result.TrayState;
        _seeded = true;
    }

    private void LogQuarantine(ArbitrationState previous, ReducerResult result)
    {
        bool was = previous.Quarantine is not null;
        bool now = result.State.Quarantine is not null;

        if (was == now)
        {
            return;
        }

        if (now)
        {
            _sink.Write(new LogEntry.QuarantineChanged("entered", "rejoin window opened")
            {
                At = _clock.UtcNow,
            });

            return;
        }

        // Exit has three distinct causes and they are not interchangeable: a manual claim
        // ends the window deliberately (§7.6's carve-out), while expiry either adopts what
        // the window observed or keeps what this machine already held.
        string reason = previous.Quarantine is { PeerObserved: true }
            ? "expired, adopted observed pair"
            : "expired, nothing observed";

        if (result.State.ActiveOwner == _lastOwner && previous.ActiveOwner != result.State.ActiveOwner)
        {
            reason = "manual claim";
        }

        _sink.Write(new LogEntry.QuarantineChanged("exited", reason) { At = _clock.UtcNow });
    }
}
