namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// The single source of time for the whole system.
/// </summary>
/// <remarks>
/// <para>
/// This seam exists so that every interval in <c>docs/design.md</c> is testable without
/// sleeping: the 2 s heartbeat cadence (§7.1), the 10 s presence window (§7.1), the 12 s
/// quarantine window (§7.6), the 5 s claim debounce (§7.2), and the 250 ms self-change
/// suppression window (§7.3).
/// </para>
/// <para>
/// Nothing outside the composition root may call <see cref="System.DateTime.UtcNow"/> or
/// <see cref="System.Environment.TickCount64"/> directly. A single real-clock call inside
/// the reducer makes the timing tests either slow or flaky, and those tests cover the
/// paths where the design's highest-priority goal is at risk.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Current wall-clock time, used only for logging and <c>sentUtc</c>.</summary>
    /// <remarks>
    /// Per §7.1, <c>sentUtc</c> is informational. Ordering relies on <c>seq</c>, never on
    /// this value, and clock skew is never a drop condition.
    /// </remarks>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Monotonic elapsed time, unaffected by wall-clock adjustment or resume from sleep.
    /// All window and timeout arithmetic uses this, not <see cref="UtcNow"/>.
    /// </summary>
    TimeSpan Elapsed { get; }
}
