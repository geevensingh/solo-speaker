namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Supplies the two microphone signals of <c>docs/design.md</c> §7.2. They are deliberately
/// separate because their two consumers want opposite biases, and revision 1's single
/// debounced signal was wrong in both directions.
/// </summary>
/// <remarks>
/// Not implemented in phase 1. Phase 1 supplies a stub whose
/// <see cref="SelfMicLive"/> and <see cref="ClaimEdgeFired"/> are always
/// <see langword="false"/>, which is why the wire format still carries an explicit
/// <c>micLive: false</c> — see §7.1 and §8.
/// </remarks>
public interface IMicWatcher
{
    /// <summary>
    /// The safety signal of §5.5. Denylist-filtered but <em>not</em> debounced, so it
    /// engages the instant the mic opens. Biased broad and fast: a false positive costs
    /// only a brief both-audible window, which Goal 3 permits and Goal 1 prefers.
    /// </summary>
    bool SelfMicLive { get; }

    /// <summary>
    /// The ownership signal of §5.1. Denylist-filtered <em>and</em> debounced (default
    /// 5 s). Biased narrow and slow: a false negative costs nothing, because the latch
    /// simply does not move.
    /// </summary>
    bool ClaimEdgeFired { get; }

    /// <summary>
    /// Process names currently holding a capture session, for the denylist discovery
    /// affordance of §7.4. Without this, diagnosing an always-on mic consumer requires
    /// knowing the problem exists, knowing the process name, and hand-editing JSON.
    /// </summary>
    IReadOnlyList<string> LiveCaptureProcesses { get; }
}
