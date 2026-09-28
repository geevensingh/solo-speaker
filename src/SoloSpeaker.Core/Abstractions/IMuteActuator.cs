namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Applies the desired mute state to the default render endpoint. See
/// <c>docs/design.md</c> §7.3.
/// </summary>
/// <remarks>
/// <para>
/// Actuation only. The mutation ledger was split out into <see cref="ILedger"/> per
/// <c>docs/review-2026-09-28.md</c> finding B-4: §6 of the design and <c>AGENTS.md</c> §3
/// name <c>MuteActuator</c> and <c>Ledger</c> as separate components, and fusing them put
/// a disk concern behind an audio-device interface where no test could reach it.
/// </para>
/// <para>
/// Implementations must be idempotent and must never use <c>VK_VOLUME_MUTE</c>, which is a
/// toggle and drifts permanently out of sync the first time anything else touches it.
/// </para>
/// <para>
/// Callers record intent to <see cref="ILedger"/> <em>before</em> calling
/// <see cref="SetMute"/>. That ordering is the whole of the crash-recovery guarantee, and
/// it is stated here because this interface is where it would be forgotten.
/// </para>
/// </remarks>
public interface IMuteActuator
{
    /// <summary>
    /// Identifier of the endpoint currently being controlled, or <see langword="null"/> if
    /// enumeration failed. A null value raises <see cref="TrayState.Error"/>.
    /// </summary>
    string? CurrentEndpointId { get; }

    /// <summary>
    /// Reads the actual mute state, for the per-tick reconcile of §7.3, or
    /// <see langword="null"/> if the endpoint cannot be read.
    /// </summary>
    bool? ReadActualMute();

    /// <summary>Applies a mute state to the current endpoint. Idempotent.</summary>
    void SetMute(bool muted);

    /// <summary>
    /// Applies a mute state to a specific endpoint, used by ledger replay to restore an
    /// endpoint that may no longer be the default.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if that endpoint no longer exists, which replay treats as a
    /// cleared entry rather than an error.
    /// </returns>
    bool TrySetMute(string endpointId, bool muted);
}
