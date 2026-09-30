using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink.Wire;

namespace SoloSpeaker.Core.PeerLink;

/// <summary>
/// What ingress decided about one datagram.
/// </summary>
/// <param name="Result">The first check the datagram failed, or acceptance.</param>
/// <param name="Datagram">
/// The parsed fields. Meaningful only for <see cref="IngressResult.Accepted"/> and
/// <see cref="IngressResult.PairingEnrollment"/>; every other result means the datagram was
/// never reconstructed, was never authenticated, or both - with one exception.
/// <see cref="IngressResult.UnknownVersion"/> <em>did</em> authenticate, because §7.1 checks
/// the <c>mac</c> at step 2 and the version at step 5; its one trustworthy field is carried
/// separately as <see cref="PeerVersion"/> rather than here, so that nothing is tempted to
/// act on the rest of a datagram this build cannot read.
/// </param>
/// <param name="PairId">
/// The sender's <c>pairId</c> where it was readable. Present even for
/// <see cref="IngressResult.BadMac"/>, because that is exactly the traffic §7.1's
/// unverifiable-peer producer counts - "something claims to be my pairing and I cannot
/// verify any of it".
/// </param>
/// <param name="PeerVersion">
/// The peer's wire version, present <b>only</b> for <see cref="IngressResult.UnknownVersion"/>.
/// </param>
/// <remarks>
/// <para>
/// <see cref="PeerVersion"/> is deliberately a bare number rather than the whole datagram.
/// It is the one field of a rejected datagram that is both readable and <em>trustworthy</em>,
/// and it is worth saying why: §7.1 verifies the <c>mac</c> at step 2 and checks the version
/// at step 5, so anything reaching step 5 has already authenticated against the
/// <c>pairKey</c>. Every other version-skew shape dies at step 2 as
/// <see cref="IngressResult.BadMac"/> - rule 2 makes the receiver re-canonicalize what it
/// parsed, so a datagram whose field set differs cannot be reconstructed and cannot verify -
/// and no number is recoverable from that path at all.
/// </para>
/// <para>
/// Carrying the number rather than <see cref="Datagram"/> follows the same rule
/// <c>PeerStateReceived</c> records: handing a consumer the wire record would give it read
/// access to fields it must never consult, and a rejected datagram is the last thing that
/// should be easy to act on.
/// </para>
/// </remarks>
public readonly record struct IngressOutcome(
    IngressResult Result,
    PeerDatagram Datagram,
    PairId PairId,
    int? PeerVersion = null)
{
    /// <summary>Whether the datagram may be acted on.</summary>
    public bool IsAccepted =>
        Result is IngressResult.Accepted or IngressResult.PairingEnrollment;
}

/// <summary>
/// The ordered ingress pipeline of <c>docs/design.md</c> §7.1 - a pure function from bytes
/// and a snapshot to a decision.
/// </summary>
/// <remarks>
/// <para>
/// Validation sits <em>above</em> the <c>IPeerTransport</c> seam by design, so the hostile
/// input cases of §10 run against real bytes without a socket. Nothing here touches a
/// socket, a clock, or a disk.
/// </para>
/// <para>
/// §7.1's list is normative and ordered, and a datagram failing two checks must be
/// attributed to the first. The one case the numbered list cannot cover is stated in the
/// document itself: steps 1 and 2 both presuppose a parse, so a datagram yielding no
/// <c>pairId</c> is attributable to no step and is dropped silently as
/// <see cref="IngressResult.Unreadable"/>.
/// </para>
/// </remarks>
public static class IngressPipeline
{
    /// <summary>
    /// Evaluates one datagram against one context.
    /// </summary>
    /// <param name="source">The received bytes, exactly as they came off the wire.</param>
    /// <param name="context">A snapshot valid for this datagram only.</param>
    /// <param name="pairKey">
    /// The HMAC key. A span, so the secret cannot be captured, boxed, or rendered - see
    /// <see cref="IngressContext"/>.
    /// </param>
    public static IngressOutcome Evaluate(
        ReadOnlySpan<byte> source,
        in IngressContext context,
        ReadOnlySpan<byte> pairKey)
    {
        ArgumentNullException.ThrowIfNull(context.Roster);

        Span<byte> receivedMac = stackalloc byte[WireProtocol.MacByteLength];
        DatagramParseResult parse = DatagramCodec.TryParse(
            source, receivedMac, out PeerDatagram datagram, out PairId pairId);

        if (parse == DatagramParseResult.Unreadable)
        {
            return new IngressOutcome(IngressResult.Unreadable, default, default);
        }

        // Step 1. Evaluated before any structural complaint, so that a peer on a later wire
        // version - which is a pairId-matching document we cannot reconstruct - is counted
        // at step 2 rather than discarded as noise.
        if (pairId != context.PairId)
        {
            return new IngressOutcome(IngressResult.ForeignPairId, default, pairId);
        }

        // Step 2. Rule 2 makes the receiver re-canonicalize parsed values, so anything that
        // cannot be parsed into the full canonical shape cannot be verified either.
        if (parse == DatagramParseResult.NonCanonical)
        {
            return new IngressOutcome(IngressResult.BadMac, default, pairId);
        }

        if (!DatagramCodec.VerifyMac(datagram, receivedMac, pairKey))
        {
            return new IngressOutcome(IngressResult.BadMac, default, pairId);
        }

        // Step 3, self-origin first. A machine hears its own broadcasts, and its own
        // machineId is a roster entry, so every later check would pass.
        if (context.Roster.IsSelf(datagram.SenderId))
        {
            return new IngressOutcome(IngressResult.SelfOrigin, default, pairId);
        }

        bool enrolling = false;
        if (!context.Roster.Contains(datagram.SenderId))
        {
            if (!context.PairingWindowOpen || context.Roster.IsComplete)
            {
                return new IngressOutcome(IngressResult.NotInRoster, default, pairId);
            }

            enrolling = true;
        }

        // Step 4. One-directional: §5.4 drops a seq that *exceeds* the local value by more
        // than the bound, and ignores a lower one silently. Checking the direction first is
        // what stops an unsigned subtraction from turning an ordinary reordered or replayed
        // datagram into a delta of 2^64-1 and a sticky error.
        if (datagram.Seq > context.LocalSeq &&
            datagram.Seq - context.LocalSeq > WireProtocol.MaxSeqDelta)
        {
            return new IngressOutcome(IngressResult.SeqOutOfBounds, default, pairId);
        }

        // Step 5. Steps 4 and 5 are unchanged by step 3's pairing exception, so an enrolling
        // datagram is held to them too.
        if (datagram.Version != WireProtocol.Version)
        {
            // The version is carried out because it is authenticated: step 2 above verified
            // the mac, so this number came from something holding the pairKey. It is the
            // only version-skew shape where an operator can learn what the peer is actually
            // running - everything else dies at step 2 with nothing recoverable.
            return new IngressOutcome(IngressResult.UnknownVersion, default, pairId, datagram.Version);
        }

        return new IngressOutcome(
            enrolling ? IngressResult.PairingEnrollment : IngressResult.Accepted,
            datagram,
            pairId);
    }
}
