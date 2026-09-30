using SoloSpeaker.Core.Diagnostics;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §7.1 step 5's version number, carried from ingress into the log.
/// </summary>
/// <remarks>
/// <para>
/// Work item 8's done-criterion is that a version mismatch is distinguishable from an absent
/// peer, and the bare cause achieves that. The number is what makes the distinction
/// <em>actionable</em>: it is the one thing an operator can read to learn which version the
/// peer is running.
/// </para>
/// <para>
/// It is trustworthy for a specific reason worth restating, because the whole feature rests
/// on it: §7.1 verifies the <c>mac</c> at step 2 and checks the version at step 5, so a step
/// 5 rejection has already authenticated against the <c>pairKey</c>. Every other
/// version-skew shape dies at step 2 as <c>BadMac</c> - rule 2 makes the receiver
/// re-canonicalize what it parsed, so a datagram whose field set differs cannot be
/// reconstructed - and nothing is recoverable from that path.
/// </para>
/// </remarks>
public sealed class UnknownVersionReportingTests
{
    /// <summary>
    /// The number reaches the outcome from <b>real signed bytes</b>, not from a hand-built
    /// fixture. That matters: a test that hands the pipeline a pre-made verdict would pass
    /// even if the version were never authenticated, which is the only property that makes
    /// it worth logging.
    /// </summary>
    [Fact]
    public void An_authenticated_datagram_with_an_unknown_version_reports_that_version()
    {
        IngressOutcome outcome = Evaluate(Signed(version: 7));

        Assert.Equal(IngressResult.UnknownVersion, outcome.Result);
        Assert.Equal(7, outcome.PeerVersion);
    }

    /// <summary>
    /// A rejection that never authenticated carries no version, because none is knowable.
    /// </summary>
    [Fact]
    public void A_rejection_that_did_not_authenticate_carries_no_version()
    {
        IngressOutcome badMac = Evaluate(Signed(pairKey: [.. Enumerable.Repeat((byte)0xab, 32)]));

        Assert.Equal(IngressResult.BadMac, badMac.Result);
        Assert.Null(badMac.PeerVersion);
    }

    /// <summary>
    /// An accepted datagram carries no <c>PeerVersion</c> either - it is not a rejection, and
    /// its version is by definition the one this build speaks.
    /// </summary>
    [Fact]
    public void An_accepted_datagram_carries_no_peer_version()
    {
        IngressOutcome accepted = Evaluate(Signed());

        Assert.Equal(IngressResult.Accepted, accepted.Result);
        Assert.Null(accepted.PeerVersion);
    }

    [Fact]
    public void The_aggregate_line_for_an_unknown_version_names_the_version()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.UnknownVersion, TimeSpan.Zero, peerVersion: 2);
        aggregator.Observe(IngressResult.UnknownVersion, TimeSpan.FromSeconds(2), peerVersion: 2);

        LogEntry entry = Assert.Single(
            aggregator.Flush(TimeSpan.FromMinutes(1), DateTimeOffset.UnixEpoch));

        var drops = Assert.IsType<LogEntry.IngressDrops>(entry);
        Assert.Equal(2, drops.Count);
        Assert.Equal(2, drops.PeerVersion);
        Assert.Contains("peerVersion=2", LogLineFormatter.Format(drops), StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>BadMac</c> line carries no version even when an unknown-version datagram shared
    /// the same minute. Attributing one reason's number to another would invent a fact.
    /// </summary>
    [Fact]
    public void A_bad_mac_line_never_names_a_version_even_when_one_was_seen_that_minute()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.UnknownVersion, TimeSpan.Zero, peerVersion: 3);
        aggregator.Observe(IngressResult.BadMac, TimeSpan.FromSeconds(1));

        IReadOnlyList<LogEntry> entries =
            aggregator.Flush(TimeSpan.FromMinutes(1), DateTimeOffset.UnixEpoch);

        LogEntry.IngressDrops badMac = entries
            .OfType<LogEntry.IngressDrops>()
            .Single(drops => drops.Reason == IngressResult.BadMac);

        Assert.Null(badMac.PeerVersion);
        Assert.DoesNotContain("peerVersion", LogLineFormatter.Format(badMac), StringComparison.Ordinal);
    }

    /// <summary>The version does not leak across flush windows.</summary>
    [Fact]
    public void A_version_seen_in_one_minute_does_not_appear_in_the_next()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.UnknownVersion, TimeSpan.Zero, peerVersion: 4);
        aggregator.Flush(TimeSpan.FromMinutes(1), DateTimeOffset.UnixEpoch);

        aggregator.Observe(IngressResult.UnknownVersion, TimeSpan.FromMinutes(1));

        LogEntry entry = Assert.Single(
            aggregator.Flush(TimeSpan.FromMinutes(2), DateTimeOffset.UnixEpoch));

        Assert.Null(Assert.IsType<LogEntry.IngressDrops>(entry).PeerVersion);
    }
    private static byte[] Signed(int version = 1, byte[]? pairKey = null)
    {
        PeerDatagram datagram = WireVectorConstants.Datagram(
            version,
            WireVectorConstants.PairIdHex,
            WireVectorConstants.MachineBHex,
            seq: 41,
            WireVectorConstants.MachineBHex,
            micLive: false,
            bye: false,
            WireVectorConstants.BaselineSentUtc);

        byte[] buffer = new byte[WireProtocol.MaxDatagramBytes];
        int length = DatagramCodec.Encode(datagram, pairKey ?? WireVectorConstants.TestPairKey, buffer);
        return buffer[..length];
    }

    private static IngressOutcome Evaluate(byte[] datagram) =>
        IngressPipeline.Evaluate(
            datagram,
            WireVectorConstants.Context("B", localSeq: 41, pairingWindowOpen: false),
            WireVectorConstants.TestPairKey);
}
