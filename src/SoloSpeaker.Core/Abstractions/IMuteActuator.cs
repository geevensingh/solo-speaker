namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Applies the desired mute state to the default render endpoint, and owns the mutation
/// ledger that makes those mutations recoverable. See <c>docs/design.md</c> §7.3.
/// </summary>
/// <remarks>
/// Implementations must be idempotent and must never use <c>VK_VOLUME_MUTE</c>, which is a
/// toggle and drifts permanently out of sync the first time anything else touches it.
/// </remarks>
public interface IMuteActuator
{
    /// <summary>
    /// Identifier of the endpoint currently being controlled, or <see langword="null"/> if
    /// enumeration failed. A null value raises <see cref="TrayState.Error"/>.
    /// </summary>
    string? CurrentEndpointId { get; }

    /// <summary>Reads the actual mute state, for the per-tick reconcile of §7.3.</summary>
    bool? ReadActualMute();

    /// <summary>
    /// Records the intended mutation to the ledger, flushes it, and only then applies it.
    /// An entry may therefore describe a mute that never happened; that is the safe
    /// direction, because the recovery it triggers is a no-op.
    /// </summary>
    void ApplyDesiredMute(bool desiredMute);

    /// <summary>
    /// Replays the ledger, restoring every endpoint still listed to its recorded
    /// <c>priorMute</c> and clearing the entry. Must run on startup before anything else,
    /// and is the whole of what <c>--restore</c> does.
    /// </summary>
    /// <returns><see langword="false"/> if replay failed, which raises <see cref="TrayState.Error"/>.</returns>
    bool ReplayLedger();
}
