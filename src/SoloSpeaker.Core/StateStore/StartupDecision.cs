using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.StateStore;

/// <summary>
/// What a machine should start from, decided from the two persisted files.
/// </summary>
/// <param name="State">The arbitration state to begin with.</param>
/// <param name="Cause">
/// <see cref="ErrorCause.None"/>, or the cause the host raises before the first tick.
/// </param>
public readonly record struct StartupOutcome(ArbitrationState State, ErrorCause Cause);

/// <summary>
/// §7.5's cross-file reasoning, as a pure function of what the two stores reported.
/// </summary>
/// <remarks>
/// <para>
/// A decision, not a sequence. The full startup ordering - single-instance guard (ADR 0012),
/// ledger replay (§7.3, "before anything else"), then this - spans work items 12, 7 and 5,
/// and two of its three steps are the host's by <c>AGENTS.md</c> §3's boundary rule. The
/// ordering therefore lives in <c>SoloSpeaker.App</c>'s <c>StartupSequence</c>, which calls
/// this for its own step.
/// </para>
/// <para>
/// Every outcome leaves the machine audible. There is no combination of these two files that
/// produces a mute, which is Goal 1 holding by construction rather than by failsafe.
/// </para>
/// <para>
/// <b>Every outcome is also quarantined.</b> Design revision 10: §7.6 used to say the rejoin
/// window "starts on first successful socket bind and send", which cannot be implemented.
/// A quarantined machine broadcasts nothing, so a window whose trigger is a send either
/// never opens - leaving the machine permanently silent - or is preceded by exactly one
/// datagram carrying the persisted <c>(activeOwner, seq)</c>, which lets a running peer
/// adopt the stale higher <c>seq</c> and mute. That second branch is the lid-open defect
/// §7.6 exists to prevent.
/// </para>
/// <para>
/// Opening the window here makes the silence a property of the state the host is handed
/// rather than of the order in which the host does things, so no host bug can reintroduce
/// that datagram. The first successful bind then raises <c>QuarantineRestarted</c>, which
/// preserves the latch and re-measures the 12s from the point the network actually came up -
/// which is what §7.6's "not from process start" rationale was reaching for.
/// </para>
/// </remarks>
public static class StartupDecision
{
    /// <summary>
    /// Decides what to start from. The result is always quarantined - see the type remarks.
    /// </summary>
    /// <param name="configPairId">
    /// The pairing the configuration names. <see langword="null"/> when there is no usable
    /// configuration at all - the machine is unpaired and the host does not build a loop.
    /// </param>
    /// <param name="stateRead">What <see cref="JsonStateStore.TryLoadState"/> reported.</param>
    /// <param name="statePairId">The pairing the state file names, if it was readable.</param>
    /// <param name="activeOwner">The persisted owner, if it was readable.</param>
    /// <param name="seq">The persisted clock, if it was readable.</param>
    /// <param name="now">
    /// Monotonic time the window opens from. The bind restarts it, so this only bounds how
    /// long a machine that never binds stays silent.
    /// </param>
    public static StartupOutcome Decide(
        PairId? configPairId,
        PersistedReadResult stateRead,
        PairId? statePairId,
        MachineId activeOwner,
        ulong seq,
        TimeSpan now)
    {
        StartupOutcome outcome = Classify(configPairId, stateRead, statePairId, activeOwner, seq);

        return outcome with
        {
            State = outcome.State with { Quarantine = QuarantineWindow.Started(now) },
        };
    }

    private static StartupOutcome Classify(
        PairId? configPairId,
        PersistedReadResult stateRead,
        PairId? statePairId,
        MachineId activeOwner,
        ulong seq)
    {
        // A refused state file names its own cause and the machine starts fresh. It stays
        // audible either way, and the error is what stops the refusal being silent.
        if (stateRead.Found && stateRead.Cause != ErrorCause.None)
        {
            return new StartupOutcome(ArbitrationState.Fresh(), stateRead.Cause);
        }

        if (!stateRead.Found)
        {
            // Design revision 9: the pairing ceremony writes state.json, so on a configured
            // machine an absent one means somebody deleted it - which §10 requires be
            // reported rather than silently tolerated. With no configuration either, this
            // is an ordinary first run.
            return configPairId is null
                ? new StartupOutcome(ArbitrationState.Fresh(), ErrorCause.None)
                : new StartupOutcome(ArbitrationState.Fresh(), ErrorCause.StateUnreadable);
        }

        if (configPairId is not { } config)
        {
            // State without configuration. The machine cannot arbitrate at all, so the host
            // never reaches a loop; the stale file is reported rather than acted on.
            return new StartupOutcome(ArbitrationState.Fresh(), ErrorCause.StateUnreadable);
        }

        // §7.5's cross-file check. A half-restored pair breaks §5.5's invariant, which spans
        // both files, so it is raised rather than tolerated.
        if (statePairId != config)
        {
            return new StartupOutcome(ArbitrationState.Fresh(), ErrorCause.StatePairIdMismatch);
        }

        return new StartupOutcome(ArbitrationState.FromPersisted(activeOwner, seq), ErrorCause.None);
    }
}
