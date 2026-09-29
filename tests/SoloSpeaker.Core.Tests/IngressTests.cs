using System.Text;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Covers the ordered ingress pipeline of <c>docs/design.md</c> §7.1.
/// </summary>
/// <remarks>
/// The frozen vectors assert each rejection reason independently. These assert the
/// <em>ordering</em> - a datagram failing two checks must be attributed to the first - and
/// the contracts that have no byte representation, such as the context being valid for one
/// datagram only.
/// </remarks>
public sealed class IngressTests
{
    private static byte[] Signed(
        string machineIdHex = WireVectorConstants.MachineBHex,
        ulong seq = 41,
        string activeOwnerHex = WireVectorConstants.MachineBHex,
        bool bye = false,
        int version = 1,
        string pairIdHex = WireVectorConstants.PairIdHex,
        byte[]? pairKey = null)
    {
        PeerDatagram datagram = WireVectorConstants.Datagram(
            version, pairIdHex, machineIdHex, seq, activeOwnerHex,
            micLive: false, bye, WireVectorConstants.BaselineSentUtc);

        byte[] buffer = new byte[WireProtocol.MaxDatagramBytes];
        int length = DatagramCodec.Encode(datagram, pairKey ?? WireVectorConstants.TestPairKey, buffer);
        return buffer[..length];
    }

    private static IngressOutcome Evaluate(
        byte[] datagram, string? rosterPeer = "B", ulong localSeq = 41, bool pairingWindowOpen = false) =>
        IngressPipeline.Evaluate(
            datagram,
            WireVectorConstants.Context(rosterPeer, localSeq, pairingWindowOpen),
            WireVectorConstants.TestPairKey);

    [Fact]
    public void Accepts_a_well_formed_datagram_from_the_peer()
    {
        IngressOutcome outcome = Evaluate(Signed());

        Assert.Equal(IngressResult.Accepted, outcome.Result);
        Assert.True(outcome.IsAccepted);
        Assert.Equal(WireVectorConstants.MachineB, outcome.Datagram.SenderId);
        Assert.False(outcome.Datagram.IsDeparture);
    }

    [Fact]
    public void Surfaces_a_departure_so_the_reducer_can_route_it_to_the_presence_layer()
    {
        IngressOutcome outcome = Evaluate(Signed(bye: true));

        Assert.Equal(IngressResult.Accepted, outcome.Result);
        Assert.True(outcome.Datagram.IsDeparture);
    }

    /// <summary>
    /// §7.1 makes the list ordered and requires a datagram failing two checks to be
    /// attributed to the first. This one fails step 1 and step 4.
    /// </summary>
    [Fact]
    public void Attributes_a_foreign_pairId_to_step_one_even_when_the_seq_bound_also_fails()
    {
        byte[] datagram = Signed(pairIdHex: "00112233445566778899aabbccddeeff", seq: 900_000);

        Assert.Equal(IngressResult.ForeignPairId, Evaluate(datagram).Result);
    }

    /// <summary>Fails step 2 and step 4; must be attributed to step 2.</summary>
    [Fact]
    public void Attributes_a_bad_mac_to_step_two_even_when_the_seq_bound_also_fails()
    {
        byte[] datagram = Signed(seq: 900_000, pairKey: [.. Enumerable.Repeat((byte)0xab, 32)]);

        Assert.Equal(IngressResult.BadMac, Evaluate(datagram).Result);
    }

    /// <summary>Fails step 3 and step 5; must be attributed to step 3.</summary>
    [Fact]
    public void Attributes_a_stranger_to_step_three_even_when_the_version_is_also_unknown()
    {
        byte[] datagram = Signed(machineIdHex: WireVectorConstants.StrangerHex, version: 2);

        Assert.Equal(IngressResult.NotInRoster, Evaluate(datagram).Result);
    }

    /// <summary>Fails step 4 and step 5; must be attributed to step 4.</summary>
    [Fact]
    public void Attributes_an_out_of_bound_seq_to_step_four_even_when_the_version_is_also_unknown()
    {
        byte[] datagram = Signed(seq: 100_000, version: 2);

        Assert.Equal(IngressResult.SeqOutOfBounds, Evaluate(datagram).Result);
    }

    /// <summary>
    /// A machine hears its own broadcasts, and its own <c>machineId</c> is a roster entry,
    /// so every check after step 3 would pass. Counting one as a peer datagram is what would
    /// keep a machine permanently present to itself - and muted for a peer that is gone.
    /// </summary>
    [Fact]
    public void Drops_our_own_broadcast_silently()
    {
        IngressOutcome outcome = Evaluate(Signed(machineIdHex: WireVectorConstants.MachineAHex));

        Assert.Equal(IngressResult.SelfOrigin, outcome.Result);
        Assert.False(outcome.IsAccepted);
    }

    /// <summary>
    /// Self-origin is evaluated before step 3's pairing exception, so a machine cannot
    /// enroll itself as its own peer during the §7.7 window.
    /// </summary>
    [Fact]
    public void Drops_our_own_broadcast_before_the_pairing_exception_can_enroll_it()
    {
        byte[] datagram = Signed(machineIdHex: WireVectorConstants.MachineAHex, activeOwnerHex: WireVectorConstants.NoneHex);

        Assert.Equal(
            IngressResult.SelfOrigin,
            Evaluate(datagram, rosterPeer: null, localSeq: 0, pairingWindowOpen: true).Result);
    }

    [Fact]
    public void Enrolls_an_unknown_machine_only_while_the_roster_is_incomplete_and_the_window_is_open()
    {
        byte[] datagram = Signed(machineIdHex: WireVectorConstants.StrangerHex, seq: 1, activeOwnerHex: WireVectorConstants.NoneHex);

        Assert.Equal(
            IngressResult.PairingEnrollment,
            Evaluate(datagram, rosterPeer: null, localSeq: 0, pairingWindowOpen: true).Result);

        Assert.Equal(
            IngressResult.NotInRoster,
            Evaluate(datagram, rosterPeer: null, localSeq: 0, pairingWindowOpen: false).Result);

        Assert.Equal(
            IngressResult.NotInRoster,
            Evaluate(datagram, rosterPeer: "B", localSeq: 0, pairingWindowOpen: true).Result);
    }

    /// <summary>
    /// §7.1: "Steps 1, 2, 4 and 5 are unchanged by that exception." Enrollment relaxes the
    /// roster test and nothing else.
    /// </summary>
    [Fact]
    public void Holds_an_enrolling_datagram_to_the_seq_bound_and_the_version_check()
    {
        byte[] outOfBounds = Signed(machineIdHex: WireVectorConstants.StrangerHex, seq: 5_000, activeOwnerHex: WireVectorConstants.NoneHex);
        byte[] wrongVersion = Signed(machineIdHex: WireVectorConstants.StrangerHex, seq: 1, activeOwnerHex: WireVectorConstants.NoneHex, version: 2);

        Assert.Equal(
            IngressResult.SeqOutOfBounds,
            Evaluate(outOfBounds, rosterPeer: null, localSeq: 0, pairingWindowOpen: true).Result);

        Assert.Equal(
            IngressResult.UnknownVersion,
            Evaluate(wrongVersion, rosterPeer: null, localSeq: 0, pairingWindowOpen: true).Result);
    }

    /// <summary>
    /// The bound is one-directional. §5.4 ignores a lower <c>seq</c> silently, and an
    /// unsigned subtraction without a direction check would present it as a delta of
    /// 2^64-1 and light a sticky <c>error</c> on ordinary UDP reordering.
    /// </summary>
    [Theory]
    [InlineData(41UL, 1041UL, IngressResult.Accepted)]
    [InlineData(41UL, 1042UL, IngressResult.SeqOutOfBounds)]
    [InlineData(41UL, 40UL, IngressResult.Accepted)]
    [InlineData(41UL, 0UL, IngressResult.Accepted)]
    [InlineData(ulong.MaxValue, 0UL, IngressResult.Accepted)]
    [InlineData(0UL, 1000UL, IngressResult.Accepted)]
    [InlineData(0UL, 1001UL, IngressResult.SeqOutOfBounds)]
    [InlineData(0UL, ulong.MaxValue, IngressResult.SeqOutOfBounds)]
    public void Bounds_the_seq_delta_in_one_direction_only(ulong localSeq, ulong datagramSeq, IngressResult expected)
    {
        Assert.Equal(expected, Evaluate(Signed(seq: datagramSeq), localSeq: localSeq).Result);
    }

    /// <summary>
    /// A <c>bye</c> is exempt from §5.4, never from ingress. Each rejection reason applies to
    /// it exactly as to any other datagram.
    /// </summary>
    [Theory]
    [InlineData("foreign-pairid")]
    [InlineData("bad-mac")]
    [InlineData("self-origin")]
    [InlineData("not-in-roster")]
    [InlineData("seq-out-of-bounds")]
    [InlineData("unknown-version")]
    public void Drops_a_bye_that_fails_any_ingress_check(string failure)
    {
        (byte[] datagram, IngressResult expected) = failure switch
        {
            "foreign-pairid" => (Signed(bye: true, pairIdHex: "00112233445566778899aabbccddeeff"), IngressResult.ForeignPairId),
            "bad-mac" => (Signed(bye: true, pairKey: [.. Enumerable.Repeat((byte)0xab, 32)]), IngressResult.BadMac),
            "self-origin" => (Signed(bye: true, machineIdHex: WireVectorConstants.MachineAHex), IngressResult.SelfOrigin),
            "not-in-roster" => (Signed(bye: true, machineIdHex: WireVectorConstants.StrangerHex), IngressResult.NotInRoster),
            "seq-out-of-bounds" => (Signed(bye: true, seq: 100_000), IngressResult.SeqOutOfBounds),
            "unknown-version" => (Signed(bye: true, version: 2), IngressResult.UnknownVersion),
            _ => throw new InvalidOperationException($"Unknown failure '{failure}'."),
        };

        IngressOutcome outcome = Evaluate(datagram);

        Assert.Equal(expected, outcome.Result);
        Assert.False(outcome.IsAccepted);
    }

    /// <summary>
    /// An <c>activeOwner</c> outside the roster is a §5.5 reducer outcome - both audible,
    /// <c>error</c> raised - not an ingress rejection. Step 3 tests <c>machineId</c>, never
    /// <c>activeOwner</c>; conflating them would put a mute decision in the parser.
    /// </summary>
    [Fact]
    public void Accepts_a_datagram_whose_activeOwner_is_outside_the_roster()
    {
        IngressOutcome outcome = Evaluate(Signed(activeOwnerHex: WireVectorConstants.StrangerHex));

        Assert.Equal(IngressResult.Accepted, outcome.Result);
        Assert.Equal(WireVectorConstants.Stranger, outcome.Datagram.ActiveOwner);
    }

    [Fact]
    public void Accepts_the_pre_claim_owner_because_section_5_reserves_it()
    {
        IngressOutcome outcome = Evaluate(Signed(activeOwnerHex: WireVectorConstants.NoneHex));

        Assert.Equal(IngressResult.Accepted, outcome.Result);
        Assert.True(outcome.Datagram.ActiveOwner.IsNone);
    }

    /// <summary>
    /// A sender naming itself <see cref="MachineId.None"/> is not a machine. Rejecting it in
    /// the parser keeps the reserved value out of every roster path.
    /// </summary>
    [Fact]
    public void Rejects_a_datagram_whose_machineId_is_the_reserved_value()
    {
        string datagram = Encoding.UTF8.GetString(Signed())
            .Replace(
                $"\"machineId\":\"{WireVectorConstants.MachineBHex}\"",
                $"\"machineId\":\"{WireVectorConstants.NoneHex}\"",
                StringComparison.Ordinal);

        Assert.Equal(IngressResult.BadMac, Evaluate(Encoding.UTF8.GetBytes(datagram)).Result);
    }

    /// <summary>
    /// The context is a per-datagram snapshot. One held across datagrams would evaluate
    /// step 4 against a <c>seq</c> the reducer had already moved past, and nothing in the
    /// type would notice - so the contract gets an assertion rather than only a comment.
    /// </summary>
    [Fact]
    public void Judges_the_same_bytes_differently_under_different_local_seq()
    {
        byte[] datagram = Signed(seq: 5_000);

        Assert.Equal(IngressResult.SeqOutOfBounds, Evaluate(datagram, localSeq: 41).Result);
        Assert.Equal(IngressResult.Accepted, Evaluate(datagram, localSeq: 4_500).Result);
    }

    [Fact]
    public void Reports_the_pairId_of_unverifiable_traffic_so_the_rate_producer_can_count_it()
    {
        byte[] datagram = Signed(pairKey: [.. Enumerable.Repeat((byte)0xab, 32)]);
        IngressOutcome outcome = Evaluate(datagram);

        Assert.Equal(IngressResult.BadMac, outcome.Result);
        Assert.Equal(WireVectorConstants.PairId, outcome.PairId);
    }

    [Fact]
    public void Refuses_a_context_without_a_roster()
    {
        Assert.Throws<ArgumentNullException>(() =>
            IngressContext.ForDatagram(WireVectorConstants.PairId, null!, 0, pairingWindowOpen: false));
    }
}
