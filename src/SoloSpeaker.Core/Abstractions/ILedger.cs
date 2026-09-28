namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// The mutation ledger of <c>docs/design.md</c> §7.3 - the entire crash-recovery story for
/// an app that mutates sticky, OS-global, reboot-surviving state.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="IMuteActuator"/> per <c>docs/review-2026-09-28.md</c> finding
/// B-4. §6 of the design and <c>AGENTS.md</c> §3 both name <c>MuteActuator</c> and
/// <c>Ledger</c> as separate components; fusing them put a disk concern behind an audio
/// interface and made every ledger failure mode untestable.
/// </para>
/// <para>
/// The implementation lives in <c>SoloSpeaker.Core</c> over <see cref="IFileStore"/>, not
/// in <c>SoloSpeaker.App</c>, so that replay and its failure modes are reachable by the
/// test suite.
/// </para>
/// </remarks>
public interface ILedger
{
    /// <summary>
    /// Records an intended mutation and flushes it to disk. Callers must invoke this
    /// <em>before</em> applying the mutation. An entry may therefore describe a mute that
    /// never happened; that is the safe direction, because the restore it triggers is a
    /// no-op.
    /// </summary>
    void RecordIntent(string endpointId, bool priorMute);

    /// <summary>Clears the entry for an endpoint this app has restored.</summary>
    void Clear(string endpointId);

    /// <summary>
    /// Replays every entry still listed, restoring each endpoint to its recorded
    /// <c>priorMute</c> and clearing it. Runs on startup before anything else this
    /// instance is entitled to do, and is the whole of what <c>--restore</c> performs.
    /// </summary>
    /// <returns>
    /// The outcome. A corrupt ledger raises <see cref="TrayState.Error"/> and the app still
    /// starts with nothing muted; an entry naming an endpoint that no longer exists is
    /// cleared without an error.
    /// </returns>
    LedgerReplayResult Replay(IMuteActuator actuator);
}

/// <summary>Outcome of <see cref="ILedger.Replay"/>.</summary>
/// <param name="Restored">Endpoints returned to their recorded prior state.</param>
/// <param name="Skipped">Entries naming an endpoint that no longer exists.</param>
/// <param name="Failed">
/// <see langword="true"/> if the ledger could not be read or parsed, which raises
/// <see cref="TrayState.Error"/>. The app still starts, and nothing is muted.
/// </param>
public readonly record struct LedgerReplayResult(int Restored, int Skipped, bool Failed);
