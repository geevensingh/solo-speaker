using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §7.4's `error` precedence where a retained cause meets the one the reducer derives.
/// </summary>
/// <remarks>
/// <para>
/// `EffectiveError` used to return `ActiveOwnerOutsideRoster` unconditionally ahead of the
/// latch. §5.5 says a stranded owner leaves <b>both</b> machines audible, so it can never be
/// the reason this machine is silent - and §7.4 calls the tray "the only visible explanation
/// for why a machine is silent". A machine that is silent because its unmute failed therefore
/// spent that one slot on the one cause that was definitionally not the explanation.
/// </para>
/// <para>
/// No test asserted the old precedence, which is why it survived: every stranger-owner test
/// in this suite runs with an empty latch, and every raised-cause test runs with
/// `ActiveOwner` unset. The two inputs had never been combined.
/// </para>
/// </remarks>
public sealed class ErrorPrecedenceTests
{
    /// <summary>
    /// The defect. This is the only cell the ranking changes.
    /// </summary>
    [Fact]
    public void A_retained_unmute_failure_outranks_a_stranded_owner()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.StrangerHex), 3)
            .Apply(new ArbitrationEvent.ErrorRaised(ErrorCause.UnmuteWriteFailed));

        Assert.Equal(ErrorCause.UnmuteWriteFailed, harness.Error);
        Assert.Equal(TrayState.Error, harness.Tray);
    }

    /// <summary>
    /// The tie, and the highest-stakes cell in the ranking: both operands are `Impaired`, so
    /// by that level's own definition neither can be why this machine is silent, and the Goal
    /// 1 axis is silent on the contest. Recoverability decides instead. The derived cause
    /// self-clears the moment `ActiveOwner` re-enters the roster, so whatever it masks is one
    /// claim away; `TransportUnavailable` is cleared only by a successful re-bind, and six of
    /// the seven continuous causes have no retraction producer at all. Preferring the retained
    /// cause here would mask the derived one for the whole process lifetime.
    /// </summary>
    [Fact]
    public void A_stranded_owner_outranks_a_retained_cause_of_equal_impact()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.StrangerHex), 3)
            .Apply(new ArbitrationEvent.ErrorRaised(ErrorCause.TransportUnavailable));

        Assert.Equal(ErrorCause.ActiveOwnerOutsideRoster, harness.Error);
    }

    /// <summary>
    /// `Limited` exists for this: a nuisance must not take the slot from a corrupt owner.
    /// Collapsing `HotkeyRegistrationFailed` into the `None` cell would invert it, because
    /// `Louder` prefers its first operand and the derived cause would then win a `None`
    /// tie - which is what `Only_the_absent_cause_has_no_audibility_impact` forbids.
    /// </summary>
    [Fact]
    public void A_stranded_owner_outranks_a_retained_hotkey_failure()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.StrangerHex), 3)
            .Apply(new ArbitrationEvent.ErrorRaised(ErrorCause.HotkeyRegistrationFailed));

        Assert.Equal(ErrorCause.ActiveOwnerOutsideRoster, harness.Error);
    }

    /// <summary>
    /// With no derived cause the latch is reported unchanged, including a `Limited` one.
    /// This is the cell that makes the `None` singleton load-bearing.
    /// </summary>
    [Fact]
    public void A_retained_cause_is_reported_when_no_cause_is_derived()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Apply(new ArbitrationEvent.ErrorRaised(ErrorCause.HotkeyRegistrationFailed));

        Assert.Equal(ErrorCause.HotkeyRegistrationFailed, harness.Error);
    }
}
