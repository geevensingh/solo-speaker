using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.PeerLink;

/// <summary>
/// Everything the ordered ingress pipeline of <c>docs/design.md</c> §7.1 needs in order to
/// judge <b>one</b> datagram.
/// </summary>
/// <remarks>
/// <para>
/// <b>Valid for exactly one datagram. Never cache one.</b> <see cref="LocalSeq"/> is §5.4
/// reducer state and <see cref="PairingWindowOpen"/> is the §7.7 window's; both are
/// snapshots. A context held across datagrams evaluates step 4's bound against a <c>seq</c>
/// the reducer has already moved past, and nothing in the type would notice. Build a fresh
/// one per datagram through <see cref="ForDatagram"/>.
/// </para>
/// <para>
/// Note what is <b>not</b> here: <c>pairKey</c>. It is passed to
/// <see cref="IngressPipeline.Evaluate"/> as a <see cref="ReadOnlySpan{T}"/> instead,
/// because a record's generated <c>ToString</c> prints every member and would put the
/// keying secret into any assertion-failure message or log line. <c>AGENTS.md</c> §6 is
/// explicit that debug logging counts, and a span cannot be captured into a record, boxed,
/// or rendered.
/// </para>
/// <para>
/// The dependency direction is deliberate and one-way. This type lets the pipeline read a
/// snapshot of reducer state without referencing the reducer, and the reducer consumes
/// events rather than bytes, so neither component depends on the other.
/// </para>
/// </remarks>
public readonly record struct IngressContext
{
    private IngressContext(PairId pairId, Roster roster, ulong localSeq, bool pairingWindowOpen)
    {
        PairId = pairId;
        Roster = roster;
        LocalSeq = localSeq;
        PairingWindowOpen = pairingWindowOpen;
    }

    /// <summary>Our pairing GUID, compared at step 1.</summary>
    public PairId PairId { get; }

    /// <summary>Our roster, read at step 3 for both the self-origin and membership tests.</summary>
    public Roster Roster { get; init; }

    /// <summary>The reducer's current <c>seq</c> at the moment this datagram arrived.</summary>
    public ulong LocalSeq { get; }

    /// <summary>
    /// Whether the §7.7 pairing window is open. Step 3's enrollment exception needs this
    /// <em>and</em> an incomplete roster; either alone is not enough.
    /// </summary>
    public bool PairingWindowOpen { get; }

    /// <summary>Builds a context for a single datagram.</summary>
    public static IngressContext ForDatagram(PairId pairId, Roster roster, ulong localSeq, bool pairingWindowOpen)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return new IngressContext(pairId, roster, localSeq, pairingWindowOpen);
    }
}
