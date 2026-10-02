using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.MuteActuator;

/// <summary>What a reconcile pass concluded.</summary>
/// <param name="Claim">
/// <see langword="true"/> when the user unmuted an endpoint we had muted, which §9.1's
/// decision D-1 treats as a manual claim.
/// </param>
/// <param name="Cause">A cause to raise, or <see cref="ErrorCause.None"/>.</param>
public readonly record struct ReconcileOutcome(bool Claim, ErrorCause Cause);

/// <summary>
/// §7.3's reconciler: makes the audio endpoint match <c>shouldMute</c>, records every
/// mutation before making it, and recognises the one divergence that is a user's intent
/// rather than drift.
/// </summary>
/// <remarks>
/// <para>
/// In <c>SoloSpeaker.Core</c>, over three seams, because these rules are the most Goal
/// 1-critical policy in the product and <c>SoloSpeaker.Core.Tests</c> - which targets
/// <c>net10.0</c> and cannot reference a Windows assembly - is what mutation-tests them.
/// Putting them in <c>SoloSpeaker.App</c> beside the COM would be review finding B-4
/// arriving through a different door.
/// </para>
/// <para>
/// One type rather than three, deliberately. The ledger ordering, the self-change window
/// and the claim decision read as three jobs but are one decision function: the age of our
/// last write is an input to the claim branch, and whether to record an intent is
/// conditioned on the same table. <see cref="IMuteActuator"/>'s own remarks warn against
/// separating the mutation from the ordering that protects it.
/// </para>
/// <para>
/// <b>The organising principle</b>, which is what makes the table below asymmetric: the only
/// divergence treated as a <em>claim</em> is the one that makes a machine more audible.
/// Every other case runs toward audible, or leaves a state this app did not create alone.
/// </para>
/// </remarks>
public sealed class MuteReconciler
{
    private readonly IMuteActuator _actuator;
    private readonly ILedger _ledger;
    private readonly IClock _clock;
    private readonly ArbitrationTunables _tunables;

    private string? _ownedEndpointId;
    private bool _lastWritten;
    private TimeSpan _lastWrittenAt;

    /// <summary>Creates a reconciler over the actuator, the ledger and the clock.</summary>
    public MuteReconciler(
        IMuteActuator actuator,
        ILedger ledger,
        IClock clock,
        ArbitrationTunables? tunables = null)
    {
        ArgumentNullException.ThrowIfNull(actuator);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(clock);

        _actuator = actuator;
        _ledger = ledger;
        _clock = clock;
        _tunables = tunables ?? ArbitrationTunables.Default;
    }

    /// <summary>
    /// Brings the endpoint into line with <paramref name="shouldMute"/>, or reports the one
    /// divergence that is a claim.
    /// </summary>
    public ReconcileOutcome Reconcile(bool shouldMute)
    {
        string? endpointId = _actuator.CurrentEndpointId;

        if (endpointId is null)
        {
            // §7.4's producer for an endpoint that cannot be enumerated. Matrix row E7.
            return new ReconcileOutcome(Claim: false, ErrorCause.EndpointEnumerationFailed);
        }

        if (!string.Equals(endpointId, _ownedEndpointId, StringComparison.Ordinal))
        {
            // The default device moved out from under us without a notification. Whatever we
            // believed about the old endpoint says nothing about this one.
            _ownedEndpointId = null;
        }

        bool? actual = _actuator.ReadActualMute();

        if (actual is not { } actualMute)
        {
            // Table row 2. An unreadable endpoint is an I/O failure, and a manual claim must
            // be evidence of a user action - never of a failed read. Deriving a claim here
            // would let a yanked headset transfer ownership and mute the peer.
            return new ReconcileOutcome(Claim: false, ErrorCause.EndpointEnumerationFailed);
        }

        if (actualMute == shouldMute)
        {
            // Table row 1, keyed on DESIRED rather than on what we last wrote. Keying it on
            // the last written value instead is a Goal 1 defect: after a peer departs,
            // shouldMute goes false while the endpoint is still muted and still matches what
            // we wrote, so the machine would stay muted permanently with the process running
            // and believing it was correct.
            return new ReconcileOutcome(Claim: false, ErrorCause.None);
        }

        return shouldMute
            ? OnUnexpectedlyAudible(endpointId)
            : Unmute(endpointId);
    }

    /// <summary>
    /// Releases an endpoint we are no longer responsible for, for §7.3's device-change path.
    /// </summary>
    /// <remarks>
    /// Release-before-acquire. The entry is cleared <b>only</b> if the restore succeeded:
    /// an unplugged-but-retained headset is not gone, and clearing its entry is precisely
    /// what would strand it across matrix rows D1 and D2.
    /// </remarks>
    public ReconcileOutcome Release(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        MuteApplyOutcome outcome = _actuator.TrySetMute(endpointId, muted: false);

        if (string.Equals(endpointId, _ownedEndpointId, StringComparison.Ordinal))
        {
            _ownedEndpointId = null;
        }

        if (outcome == MuteApplyOutcome.Failed)
        {
            return new ReconcileOutcome(Claim: false, ErrorCause.UnmuteWriteFailed);
        }

        _ledger.Clear(endpointId);
        return new ReconcileOutcome(Claim: false, ErrorCause.None);
    }

    private ReconcileOutcome OnUnexpectedlyAudible(string endpointId)
    {
        bool weOwnIt = string.Equals(endpointId, _ownedEndpointId, StringComparison.Ordinal);

        if (!weOwnIt)
        {
            // We want it muted and nobody has muted it yet. Ordinary acquisition.
            return Mute(endpointId);
        }

        if (_clock.Elapsed - _lastWrittenAt < _tunables.SelfChangeSuppression)
        {
            // Table row 5. Our own write echoing back; §7.3's suppression window is what
            // stops the self-feedback loop this option is prone to.
            return new ReconcileOutcome(Claim: false, ErrorCause.None);
        }

        // Table row 6. §9.1's D-1: reaching for the volume flyout is the natural reflex when
        // a machine is unexpectedly silent, and revision 1 made it fail. We cede the mute,
        // so the entry goes with it - otherwise it survives to the next replay.
        _ledger.Clear(endpointId);
        _ownedEndpointId = null;

        return new ReconcileOutcome(Claim: true, ErrorCause.None);
    }

    private ReconcileOutcome Mute(string endpointId)
    {
        // §7.3's ordering, and the whole of the crash-recovery guarantee: the intent is
        // recorded and flushed BEFORE the mutation. An entry may therefore describe a mute
        // that never happened, which is the safe direction - the restore it triggers is a
        // no-op.
        _ledger.RecordIntent(endpointId, priorMute: false);

        MuteApplyOutcome outcome = _actuator.SetMute(true);

        if (outcome == MuteApplyOutcome.Applied)
        {
            _ownedEndpointId = endpointId;
            _lastWritten = true;
            _lastWrittenAt = _clock.Elapsed;
            return new ReconcileOutcome(Claim: false, ErrorCause.None);
        }

        // The entry stays. The mutation may have partly landed, and the record is the only
        // thing that can repair it.
        //
        // EndpointGone reaches here too, because this tests == Applied rather than == Failed.
        // It is unreachable in practice: Reconcile calls ReadActualMute() first, which proves
        // the endpoint resolves, and EndpointWatcher posts its retarget through the dispatch
        // rather than running on the COM thread. Kept loose deliberately - tightening to
        // == Failed would let an EndpointGone write raise no cause at all.
        return new ReconcileOutcome(Claim: false, ErrorCause.MuteWriteFailed);
    }

    private ReconcileOutcome Unmute(string endpointId)
    {
        // Table row 4, and the one row with no gate of any kind. This is Goal 1's direction,
        // so no suppression window, no ownership test and no age check may stand between a
        // machine and becoming audible again.
        MuteApplyOutcome outcome = _actuator.SetMute(false);

        if (outcome != MuteApplyOutcome.Applied)
        {
            // Goal 1's direction. EndpointGone reaches here as well as Failed - see the note
            // in Mute; the looseness is deliberate, because a silent machine must raise a
            // cause whichever way the write failed.
            return new ReconcileOutcome(Claim: false, ErrorCause.UnmuteWriteFailed);
        }

        _lastWritten = false;
        _lastWrittenAt = _clock.Elapsed;
        _ledger.Clear(endpointId);
        _ownedEndpointId = null;

        return new ReconcileOutcome(Claim: false, ErrorCause.None);
    }

    /// <summary>The last value this reconciler wrote, for tests and diagnostics.</summary>
    internal bool LastWritten => _lastWritten;
}
