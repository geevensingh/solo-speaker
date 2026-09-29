using System.Reflection;
using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Covers the two-entry roster of <c>docs/design.md</c> §5.3 - the right-hand side of §5.5's
/// mute predicate - and §5.4's concurrent-edge tiebreak.
/// </summary>
public sealed class RosterTests
{
    private const string SelfHex = "7f3a9c1e2d4b6a8035179246ab13cd5e";
    private const string PeerHex = "2d81e407fa63b95c18204e7dc6395fa1";
    private const string StrangerHex = "aa00bb11cc22dd33ee44ff5566778899";

    private static MachineId Id(string hex)
    {
        Assert.True(MachineId.TryParseRosterEntry(hex, out MachineId id));
        return id;
    }

    private static Roster Complete()
    {
        Assert.True(Roster.TryCreate(Id(SelfHex), Id(PeerHex), out Roster? roster));
        return roster!;
    }

    private static Roster Incomplete()
    {
        Assert.True(Roster.TryCreate(Id(SelfHex), peer: null, out Roster? roster));
        return roster!;
    }

    [Fact]
    public void Holds_both_entries_once_pairing_completes()
    {
        Roster roster = Complete();

        Assert.True(roster.IsComplete);
        Assert.Equal(Id(SelfHex), roster.Self);
        Assert.Equal(Id(PeerHex), roster.Peer);
    }

    [Fact]
    public void Is_incomplete_while_the_pairing_window_is_still_open()
    {
        Roster roster = Incomplete();

        Assert.False(roster.IsComplete);
        Assert.Null(roster.Peer);
        Assert.True(roster.Contains(Id(SelfHex)));
        Assert.False(roster.Contains(Id(PeerHex)));
    }

    [Fact]
    public void Contains_both_entries_and_nothing_else()
    {
        Roster roster = Complete();

        Assert.True(roster.Contains(Id(SelfHex)));
        Assert.True(roster.Contains(Id(PeerHex)));
        Assert.False(roster.Contains(Id(StrangerHex)));
        Assert.False(roster.Contains(MachineId.None));
    }

    [Fact]
    public void Recognises_this_machine_so_ingress_can_drop_its_own_broadcasts()
    {
        Roster roster = Complete();

        Assert.True(roster.IsSelf(Id(SelfHex)));
        Assert.False(roster.IsSelf(Id(PeerHex)));
        Assert.False(roster.IsSelf(Id(StrangerHex)));
    }

    /// <summary>
    /// <c>design.md</c> §10: "<c>activeOwner</c> differing only by case -> treated as outside
    /// the roster, not as a match."
    /// </summary>
    /// <remarks>
    /// What this asserts, precisely: the uppercase rendering never becomes a
    /// <see cref="MachineId"/>, so it cannot reach <see cref="Roster.Contains"/> at all.
    /// The guarantee is enforced by the strict parse of canonicalization rule 5, not by the
    /// comparison - the comparison has no string in it to get wrong. A stray
    /// case-insensitive comparison elsewhere in a future hex path remains issue #6's
    /// concern, and the companion test below is the structural guard against one appearing
    /// on these two types.
    /// </remarks>
    [Fact]
    public void An_owner_differing_only_by_case_is_outside_the_roster_and_not_a_match()
    {
        Roster roster = Complete();
        string upperCasedPeer = PeerHex.ToUpperInvariant();

        Assert.False(MachineId.TryParseOwner(upperCasedPeer, out _));
        Assert.False(MachineId.TryParseRosterEntry(upperCasedPeer, out _));

        Assert.True(MachineId.TryParseOwner(PeerHex, out MachineId canonicalPeer));
        Assert.True(roster.Contains(canonicalPeer));
    }

    /// <summary>
    /// The mechanism review finding H-9 asked for: roster IDs are values on every comparison
    /// path. If someone later adds a string overload to either type, a case-insensitive
    /// comparison becomes expressible again and this fails.
    /// </summary>
    [Fact]
    public void No_membership_or_comparison_member_accepts_a_string()
    {
        static IEnumerable<string> StringTakingMembers(Type type) =>
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.Name.StartsWith("TryParse", StringComparison.Ordinal))
                .Where(method => method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(string) ||
                    parameter.ParameterType == typeof(ReadOnlySpan<char>)))
                .Select(method => $"{type.Name}.{method.Name}");

        Assert.Empty(StringTakingMembers(typeof(Roster)).Concat(StringTakingMembers(typeof(MachineId))));
    }

    [Fact]
    public void Refuses_a_roster_whose_two_entries_are_the_same_machine()
    {
        Assert.False(Roster.TryCreate(Id(SelfHex), Id(SelfHex), out Roster? roster));
        Assert.Null(roster);
    }

    [Fact]
    public void Refuses_None_as_either_entry_so_the_pre_claim_value_can_never_be_enrolled()
    {
        Assert.False(Roster.TryCreate(MachineId.None, Id(PeerHex), out _));
        Assert.False(Roster.TryCreate(Id(SelfHex), MachineId.None, out _));
    }

    [Fact]
    public void Breaks_a_concurrent_edge_by_the_lexicographically_smaller_roster_id()
    {
        Roster roster = Complete();

        Assert.True(roster.TryGetTiebreakWinner(out MachineId winner));
        Assert.Equal(Id(PeerHex), winner);
        Assert.True(winner < Id(SelfHex));
    }

    /// <summary>
    /// §5.4 requires the tiebreak to be "evaluated identically on both sides". Swapping the
    /// roles must not swap the winner.
    /// </summary>
    [Fact]
    public void Both_machines_compute_the_same_tiebreak_winner()
    {
        Assert.True(Roster.TryCreate(Id(SelfHex), Id(PeerHex), out Roster? fromOneSide));
        Assert.True(Roster.TryCreate(Id(PeerHex), Id(SelfHex), out Roster? fromTheOther));

        Assert.True(fromOneSide!.TryGetTiebreakWinner(out MachineId oneSideWinner));
        Assert.True(fromTheOther!.TryGetTiebreakWinner(out MachineId otherSideWinner));

        Assert.Equal(oneSideWinner, otherSideWinner);
    }

    [Fact]
    public void Has_no_tiebreak_winner_while_the_roster_is_incomplete()
    {
        Assert.False(Incomplete().TryGetTiebreakWinner(out _));
    }
}
