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
public interface IMuteActuator : IDisposable
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
    MuteApplyOutcome SetMute(bool muted);

    /// <summary>
    /// Re-points this actuator at the current default render endpoint.
    /// </summary>
    /// <remarks>
    /// §7.3 subscribes to <c>IMMNotificationClient</c> so that switching headset to speakers
    /// re-applies the desired state to the new endpoint. The caller releases the previous
    /// endpoint <em>before</em> calling this - see <c>MuteReconciler.Release</c> - because
    /// the endpoint this forgets is the one that would otherwise be left muted with nobody
    /// listening to it.
    /// </remarks>
    void Retarget();

    /// <summary>
    /// Applies a mute state to a specific endpoint, used by ledger replay to restore an
    /// endpoint that may no longer be the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three-valued since design revision 11. It returned a <see langword="bool"/> meaning
    /// only "that endpoint no longer exists", which left a genuine failure - access denied,
    /// an exclusive-mode holder, the disabled endpoint of matrix row E7 - indistinguishable
    /// from a cleared entry. Replay counted it as skipped, <c>--restore</c> exited zero, and
    /// <c>uninstall.ps1</c> deleted the binary and the ledger while the endpoint was still
    /// muted. That is the precise failure the script's Goal 1 branch exists to prevent, and
    /// a failed <em>unmute</em> is the highest-consequence runtime event this product has.
    /// </para>
    /// <para>
    /// <see cref="MuteApplyOutcome.EndpointGone"/> means absent from enumeration entirely,
    /// <b>not</b> merely unplugged. A retained-but-unplugged headset is not gone, and
    /// clearing its entry is what would strand it across matrix rows D1 and D2.
    /// </para>
    /// </remarks>
    MuteApplyOutcome TrySetMute(string endpointId, bool muted);
}

/// <summary>What applying a mute state to an endpoint did.</summary>
/// <remarks>
/// The distinction between <see cref="EndpointGone"/> and <see cref="Failed"/> is what lets
/// §7.3 keep its promise that "an entry whose restore did not succeed is never cleared".
/// </remarks>
public enum MuteApplyOutcome
{
    /// <summary>The endpoint exists and now holds the requested state.</summary>
    Applied,

    /// <summary>
    /// The endpoint is absent from enumeration. Replay treats this as a cleared entry
    /// rather than an error, per §7.3.
    /// </summary>
    EndpointGone,

    /// <summary>
    /// The endpoint exists and the write failed. The entry is retained, and the cause is
    /// <see cref="StateMachine.ErrorCause.MuteWriteFailed"/> or
    /// <see cref="StateMachine.ErrorCause.UnmuteWriteFailed"/> according to the direction
    /// applied - this outcome is returned from both. When it comes from
    /// <see cref="IMuteActuator.TrySetMute"/> during replay, <c>--restore</c> also exits
    /// non-zero.
    /// </summary>
    Failed,
}
