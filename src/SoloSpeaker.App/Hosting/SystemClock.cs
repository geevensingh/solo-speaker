using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// The one <see cref="IClock"/>. <c>AGENTS.md</c> §3 forbids every other type from reading
/// the system clock, so this is the only place in the product that does.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Environment.TickCount64"/>, deliberately. <see cref="IClock.Elapsed"/> is
/// specified as monotonic and unaffected by wall-clock adjustment <em>or resume from
/// sleep</em>, and the choice of primitive decides the second half of that sentence:
/// <c>TickCount64</c> counts time the machine spent suspended, and a
/// <see cref="System.Diagnostics.Stopwatch"/> started before a suspend does not reliably
/// do so across every Windows power path.
/// </para>
/// <para>
/// That difference is not academic here. The quantity being measured is §7.6's 12s
/// quarantine window, whose entire reason for existing is resume-from-sleep. Manual matrix
/// row C7 suspends a machine mid-mute for over an hour: with <c>TickCount64</c> the window
/// is correctly long expired on resume, and the machine rejoins normally.
/// </para>
/// </remarks>
public sealed class SystemClock : IClock
{
    private readonly long _originMilliseconds = Environment.TickCount64;

    /// <inheritdoc />
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(Environment.TickCount64 - _originMilliseconds);

    /// <summary>
    /// Wall-clock time, used only for the wire's <c>sentUtc</c> field.
    /// </summary>
    /// <remarks>
    /// Never for an interval. Every duration in the design is measured against
    /// <see cref="Elapsed"/> precisely so that a clock adjustment cannot move a mute, and
    /// §7.1 makes <c>sentUtc</c> non-normative - it is diagnostic, never a drop condition.
    /// </remarks>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
