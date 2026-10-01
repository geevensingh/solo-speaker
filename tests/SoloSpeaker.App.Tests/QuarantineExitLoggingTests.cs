using SoloSpeaker.App.Logging;
using SoloSpeaker.Core;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Composition;
using SoloSpeaker.Core.Diagnostics;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.App.Tests;

/// <summary>
/// §7.6's three quarantine exits, as the diagnostic log reports them.
/// </summary>
/// <remarks>
/// <para>
/// The exits are not interchangeable, and the log is the only account of a lid-open that
/// survives the event. An adoption means this machine took the peer's recorded pair because
/// the window observed one; a manual claim means the user ended the window deliberately under
/// §7.6's carve-out; an expiry with nothing observed means it resumed from what it already
/// held.
/// </para>
/// <para>
/// <b>The defect these guard against:</b> the reason was originally inferred from whether
/// ownership had moved during the reduction. An adoption moves ownership exactly as a claim
/// does, so inference could not separate the two, and every adoption was reported as a manual
/// claim - the opposite of the truth. The reason is now read from the
/// <see cref="OwnershipSource"/> carried on the write, which is the mechanism work item 8
/// added so that byte-identical state changes stay distinguishable.
/// </para>
/// <para>
/// Each case drives the real reducer rather than a hand-built <see cref="ReducerResult"/>.
/// A hand-built result would assert only that this class maps a source to a string, and would
/// still pass if the reducer stopped emitting that source - which is the half of the contract
/// most likely to drift.
/// </para>
/// </remarks>
public sealed class QuarantineExitLoggingTests
{
    private const string SelfHex = "7f3a9c1e2d4b6a8035179246ab13cd5e";
    private const string PeerHex = "2d81e407fa63b95c18204e7dc6395fa1";

    /// <summary>The case the old inference got backwards.</summary>
    [Fact]
    public void An_adoption_at_expiry_is_reported_as_an_adoption_not_a_claim()
    {
        using var cycle = new Cycle();

        cycle.Apply(new ArbitrationEvent.QuarantineEntered());
        cycle.Apply(new ArbitrationEvent.PeerStateReceived(Id(PeerHex), 7, MicLive: false));
        cycle.Apply(new ArbitrationEvent.Tick(), after: TimeSpan.FromSeconds(12.001));

        string reason = cycle.ExitReason();

        Assert.Equal("expired, adopted observed pair", reason);
        Assert.DoesNotContain("claim", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_claim_that_ends_the_window_is_reported_as_a_claim()
    {
        using var cycle = new Cycle();

        cycle.Apply(new ArbitrationEvent.QuarantineEntered());
        cycle.Apply(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

        Assert.Equal("manual claim", cycle.ExitReason());
    }

    /// <summary>
    /// Expiry with the latch unset emits no write at all, and the absence of a source - rather
    /// than a distinct one - is what separates this exit from the other two.
    /// </summary>
    [Fact]
    public void An_expiry_with_nothing_observed_is_reported_as_such()
    {
        using var cycle = new Cycle();

        cycle.Apply(new ArbitrationEvent.QuarantineEntered());
        cycle.Apply(new ArbitrationEvent.Tick(), after: TimeSpan.FromSeconds(12.001));

        Assert.Equal("expired, nothing observed", cycle.ExitReason());
    }

    private static MachineId Id(string hex) =>
        MachineId.TryParseOwner(hex, out MachineId id)
            ? id
            : throw new InvalidOperationException($"Test identifier '{hex}' is not canonical.");

    /// <summary>The real reducer, observed by a real <see cref="DiagnosticLog"/>.</summary>
    private sealed class Cycle : IDisposable
    {
        private readonly ArbitrationContext _context;
        private readonly RecordingLogSink _sink = new();
        private readonly StubClock _clock = new();
        private readonly DiagnosticLog _log;

        private ArbitrationState _state = ArbitrationState.FromPersisted(Id(SelfHex), 500);

        internal Cycle()
        {
            if (!Roster.TryCreate(Id(SelfHex), Id(PeerHex), out Roster? roster))
            {
                throw new InvalidOperationException("Test roster is invalid.");
            }

            _context = ArbitrationContext.ForEvent(roster!);
            _log = new DiagnosticLog(_sink, _clock);
        }

        public void Dispose() => _sink.Dispose();

        internal void Apply(ArbitrationEvent arbitrationEvent, TimeSpan? after = null)
        {
            _clock.Advance(after ?? TimeSpan.Zero);

            ArbitrationState previous = _state;
            ReducerResult result = Reducer.Reduce(_context, previous, arbitrationEvent, _clock.Elapsed);
            _state = result.State;

            _log.Observe(new CycleObservation(arbitrationEvent, result, previous, null));
        }

        internal string ExitReason() => _sink.Entries
            .OfType<LogEntry.QuarantineChanged>()
            .Single(entry => entry.Transition == "exited")
            .Reason;
    }

    private sealed class RecordingLogSink : ILogSink
    {
        internal List<LogEntry> Entries { get; } = [];

        public void Write(LogEntry entry) => Entries.Add(entry);

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubClock : IClock
    {
        public TimeSpan Elapsed { get; private set; }

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Elapsed;

        internal void Advance(TimeSpan by) => Elapsed += by;
    }
}
