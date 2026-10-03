using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// The gate `ErrorCause` never had.
/// </summary>
/// <remarks>
/// <para>
/// `ErrorCause`'s own summary calls it "the machine-readable copy" of §7.4's producer table,
/// but nothing enforced the correspondence: <see cref="ErrorCauseExtensions.IsContinuous"/>
/// ends in a <c>_ => false</c> discard, so a new member was silently edge and no test
/// anywhere enumerated the enum. Contrast `DatagramRouter`, which documents that its switch
/// exists "so a new value cannot be added without this switch failing to compile".
/// </para>
/// <para>
/// Deleting that discard is not available: a switch expression over an enum warns
/// <b>CS8524</b> even when every declared member is handled, because undeclared values are
/// representable, and `Directory.Build.props` sets `TreatWarningsAsErrors`. Nor should
/// <c>_ =&gt; throw</c> replace it - <see cref="ErrorCauseExtensions.IsContinuous"/> is called
/// from the reducer's acknowledge path, and a throw there risks taking down the loop that
/// keeps the machine audible. Goal 1 forbids it.
/// </para>
/// <para>
/// So the gate is a test, mirroring the roster this repository already keeps for `TrayState`.
/// Adding a cause without deciding its continuity now fails here.
/// </para>
/// </remarks>
public sealed class ErrorCauseTests
{
    /// <summary>
    /// Every member, with the continuity §7.4 assigns it. Edge causes latch until
    /// acknowledged because nothing re-raises them; continuous causes cannot be acknowledged
    /// while they remain true.
    /// </summary>
    private static readonly Dictionary<ErrorCause, bool> Continuity = new()
    {
        [ErrorCause.None] = false,
        [ErrorCause.ActiveOwnerOutsideRoster] = true,
        [ErrorCause.RosterIncompleteAfterPairing] = true,
        [ErrorCause.StatePairIdMismatch] = true,
        [ErrorCause.HotkeyRegistrationFailed] = false,
        [ErrorCause.SeqBoundExceeded] = false,
        [ErrorCause.UnknownWireVersion] = false,
        [ErrorCause.PeerUnverifiable] = false,
        [ErrorCause.LedgerReplayFailed] = false,
        [ErrorCause.EndpointEnumerationFailed] = false,
        [ErrorCause.ConfigUnreadable] = true,
        [ErrorCause.StateUnreadable] = true,
        [ErrorCause.PersistedSchemaUnknown] = true,
        [ErrorCause.PairKeyRefused] = true,
        [ErrorCause.TunableFellBackToDefault] = false,
        [ErrorCause.StatePersistFailed] = false,
        [ErrorCause.TransportUnavailable] = true,
        [ErrorCause.MuteWriteFailed] = false,
        [ErrorCause.UnmuteWriteFailed] = false,
    };

    [Fact]
    public void Every_cause_has_a_decided_continuity()
    {
        ErrorCause[] declared = Enum.GetValues<ErrorCause>();

        ErrorCause[] unclassified = declared.Where(cause => !Continuity.ContainsKey(cause)).ToArray();

        Assert.True(
            unclassified.Length == 0,
            $"Added to ErrorCause without deciding continuity: {string.Join(", ", unclassified)}. "
            + "Classify it here and in design.md §7.4, then in ErrorCauseExtensions.IsContinuous.");
    }

    [Fact]
    public void The_continuity_roster_names_no_cause_that_no_longer_exists()
    {
        ErrorCause[] declared = Enum.GetValues<ErrorCause>();

        ErrorCause[] stale = Continuity.Keys.Where(cause => !declared.Contains(cause)).ToArray();

        Assert.True(stale.Length == 0, $"Retired from ErrorCause but still rostered: {string.Join(", ", stale)}.");
    }

    [Theory]
    [MemberData(nameof(AllCauses))]
    public void Continuity_matches_the_roster(ErrorCause cause)
    {
        Assert.Equal(Continuity[cause], cause.IsContinuous());
    }

    /// <summary>
    /// Every member, with its position on §7.4's audibility axis. Classification is
    /// <b>ordered</b>: MaySilence first, then Limited; Impaired is the remainder.
    /// </summary>
    private static readonly Dictionary<ErrorCause, AudibilityImpact> Impact = new()
    {
        [ErrorCause.None] = AudibilityImpact.None,

        [ErrorCause.HotkeyRegistrationFailed] = AudibilityImpact.Limited,

        [ErrorCause.EndpointEnumerationFailed] = AudibilityImpact.MaySilence,
        [ErrorCause.LedgerReplayFailed] = AudibilityImpact.MaySilence,
        [ErrorCause.UnmuteWriteFailed] = AudibilityImpact.MaySilence,

        [ErrorCause.ActiveOwnerOutsideRoster] = AudibilityImpact.Impaired,
        [ErrorCause.RosterIncompleteAfterPairing] = AudibilityImpact.Impaired,
        [ErrorCause.StatePairIdMismatch] = AudibilityImpact.Impaired,
        [ErrorCause.SeqBoundExceeded] = AudibilityImpact.Impaired,
        [ErrorCause.UnknownWireVersion] = AudibilityImpact.Impaired,
        [ErrorCause.PeerUnverifiable] = AudibilityImpact.Impaired,
        [ErrorCause.ConfigUnreadable] = AudibilityImpact.Impaired,
        [ErrorCause.StateUnreadable] = AudibilityImpact.Impaired,
        [ErrorCause.PersistedSchemaUnknown] = AudibilityImpact.Impaired,
        [ErrorCause.PairKeyRefused] = AudibilityImpact.Impaired,
        [ErrorCause.TunableFellBackToDefault] = AudibilityImpact.Impaired,
        [ErrorCause.StatePersistFailed] = AudibilityImpact.Impaired,
        [ErrorCause.TransportUnavailable] = AudibilityImpact.Impaired,
        [ErrorCause.MuteWriteFailed] = AudibilityImpact.Impaired,
    };

    [Fact]
    public void Every_cause_has_a_decided_impact()
    {
        ErrorCause[] declared = Enum.GetValues<ErrorCause>();

        ErrorCause[] unclassified = declared.Where(cause => !Impact.ContainsKey(cause)).ToArray();

        Assert.True(
            unclassified.Length == 0,
            $"Added to ErrorCause without deciding audibility impact: {string.Join(", ", unclassified)}. "
            + "Classify it in order - can it leave THIS machine silent when it should not be "
            + "(MaySilence)? Are arbitration and actuation both correct and only a convenience "
            + "reduced (Limited)? Otherwise Impaired. Record it here, in design.md §7.4, and in "
            + "ErrorCauseExtensions.Impact.");
    }

    [Fact]
    public void The_impact_roster_names_no_cause_that_no_longer_exists()
    {
        ErrorCause[] declared = Enum.GetValues<ErrorCause>();

        ErrorCause[] stale = Impact.Keys.Where(cause => !declared.Contains(cause)).ToArray();

        Assert.True(stale.Length == 0, $"Retired from ErrorCause but still rostered: {string.Join(", ", stale)}.");
    }

    [Theory]
    [MemberData(nameof(AllCauses))]
    public void Impact_matches_the_roster(ErrorCause cause)
    {
        Assert.Equal(Impact[cause], cause.Impact());
    }

    /// <summary>
    /// <see cref="ErrorCauseExtensions.Louder"/> prefers its first operand on a tie, so a
    /// real cause sharing the <see cref="AudibilityImpact.None"/> cell with
    /// <see cref="ErrorCause.None"/> could be discarded by a <c>None &gt;= None</c> tie and
    /// vanish from the tray entirely. The singleton is what makes that unreachable.
    /// </summary>
    [Fact]
    public void Only_the_absent_cause_has_no_audibility_impact()
    {
        foreach (ErrorCause cause in Enum.GetValues<ErrorCause>())
        {
            Assert.Equal(cause == ErrorCause.None, cause.Impact() == AudibilityImpact.None);
        }
    }

    /// <summary>
    /// The order is load-bearing, not just the membership. §7.4 requires `UnmuteWriteFailed`
    /// above `MuteWriteFailed`, and that falls out of MaySilence outranking Impaired.
    /// </summary>
    [Fact]
    public void The_axis_runs_from_absent_to_possibly_silent()
    {
        Assert.True(AudibilityImpact.MaySilence > AudibilityImpact.Impaired);
        Assert.True(AudibilityImpact.Impaired > AudibilityImpact.Limited);
        Assert.True(AudibilityImpact.Limited > AudibilityImpact.None);

        Assert.True(ErrorCause.UnmuteWriteFailed.Impact() > ErrorCause.MuteWriteFailed.Impact());
    }

    [Fact]
    public void Louder_prefers_its_first_operand_on_a_tie()
    {
        Assert.Equal(
            ErrorCause.StateUnreadable,
            ErrorCauseExtensions.Louder(ErrorCause.StateUnreadable, ErrorCause.TransportUnavailable));

        Assert.Equal(
            ErrorCause.UnmuteWriteFailed,
            ErrorCauseExtensions.Louder(ErrorCause.StatePersistFailed, ErrorCause.UnmuteWriteFailed));
    }

    /// <summary>
    /// Design revision 12 split one cause into two because the directions are opposites under
    /// Goal 1: a failed unmute leaves the machine silent, a failed mute leaves it audible.
    /// Anything that later ranks causes must rank them apart, so they must stay distinct.
    /// </summary>
    [Fact]
    public void The_two_write_directions_are_distinct_causes()
    {
        Assert.NotEqual(ErrorCause.MuteWriteFailed, ErrorCause.UnmuteWriteFailed);
    }

    public static TheoryData<ErrorCause> AllCauses()
    {
        var data = new TheoryData<ErrorCause>();

        foreach (ErrorCause cause in Enum.GetValues<ErrorCause>())
        {
            data.Add(cause);
        }

        return data;
    }
}
