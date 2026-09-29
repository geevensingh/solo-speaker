namespace SoloSpeaker.Core.Identity;

/// <summary>
/// The two-entry roster of <c>docs/design.md</c> §5.3 - the enrolled identity of this machine
/// and, once pairing completes, of its peer.
/// </summary>
/// <remarks>
/// <para>
/// A sealed class with a private constructor rather than a struct, so that there is no
/// <c>default</c> instance holding <see cref="MachineId.None"/> as <see cref="Self"/>. Every
/// roster in existence has been through <see cref="TryCreate"/>.
/// </para>
/// <para>
/// §3 lists "coordinating more than two machines" as a non-goal that revision 2 actively
/// prevents, and the shape here is that prevention: <see cref="Self"/> plus an optional
/// <see cref="Peer"/> makes a third entry unrepresentable and makes "incomplete during the
/// §7.7 pairing window" a state rather than a convention.
/// </para>
/// </remarks>
public sealed class Roster
{
    private Roster(MachineId self, MachineId? peer)
    {
        Self = self;
        Peer = peer;
    }

    /// <summary>This machine's enrolled identity.</summary>
    public MachineId Self { get; }

    /// <summary>
    /// The peer's enrolled identity, or <see langword="null"/> while the §7.7 pairing window
    /// is still open and the roster holds one entry.
    /// </summary>
    public MachineId? Peer { get; }

    /// <summary>
    /// Whether both entries are enrolled. A machine with an incomplete roster can never mute
    /// - §5.5's positive predicate has no right-hand side - and raises
    /// <see cref="TrayState.Error"/> with a "pairing incomplete" cause once the window
    /// expires.
    /// </summary>
    public bool IsComplete => Peer.HasValue;

    /// <summary>
    /// Builds a roster, rejecting every shape §5.3 forbids.
    /// </summary>
    /// <remarks>
    /// Three rejections, each covering a way a machine could otherwise mute itself:
    /// <list type="bullet">
    /// <item>
    /// <see cref="MachineId.None"/> as either entry. §5 reserves it as "nobody has claimed",
    /// so a roster containing it would make <c>activeOwner == peerRosterId</c> true for the
    /// pre-claim value and mute a machine for a peer that does not exist.
    /// </item>
    /// <item>
    /// <see cref="Self"/> equal to <see cref="Peer"/>. Reachable by cloning a disk image -
    /// plausible for a desktop and laptop under one owner, and §7.7 names machine
    /// replacement as a supported path. It makes §5.5's <c>activeOwner == peerRosterId</c>
    /// and §7.4's <c>activeOwner == self</c> simultaneously true and leaves §5.4's tiebreak
    /// with no winner.
    /// </item>
    /// <item>
    /// A peer without a self. There is no such machine.
    /// </item>
    /// </list>
    /// </remarks>
    public static bool TryCreate(MachineId self, MachineId? peer, out Roster? roster)
    {
        roster = null;

        if (self.IsNone)
        {
            return false;
        }

        if (peer.HasValue && (peer.Value.IsNone || peer.Value == self))
        {
            return false;
        }

        roster = new Roster(self, peer);
        return true;
    }

    /// <summary>
    /// Whether the candidate is an enrolled roster entry. Ordinal by construction: the
    /// comparison is integer equality on <see cref="MachineId"/>, with no string in the
    /// path.
    /// </summary>
    public bool Contains(MachineId candidate) =>
        candidate == Self || (Peer.HasValue && candidate == Peer.Value);

    /// <summary>
    /// Whether the candidate is <em>this</em> machine. Ingress drops these before anything
    /// else in §7.1 step 3: a machine hears its own broadcasts, and counting one as a peer
    /// datagram keeps the machine permanently present to itself.
    /// </summary>
    public bool IsSelf(MachineId candidate) => candidate == Self;

    /// <summary>
    /// The winner of §5.4's concurrent-edge tiebreak - the lexicographically smaller of the
    /// two roster IDs, which both machines compute identically.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the roster is incomplete, because there is nothing to
    /// break a tie between. The reducer resolves that case; this type does not throw.
    /// </returns>
    public bool TryGetTiebreakWinner(out MachineId winner)
    {
        if (!Peer.HasValue)
        {
            winner = default;
            return false;
        }

        winner = Self < Peer.Value ? Self : Peer.Value;
        return true;
    }
}
