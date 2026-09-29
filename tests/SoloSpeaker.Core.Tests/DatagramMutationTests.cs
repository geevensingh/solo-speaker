using System.Text;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Mutates the golden corpus and asserts the pipeline's Goal 1 invariant against every
/// result.
/// </summary>
/// <remarks>
/// <para>
/// Named for what it is. A fixed seed set is a large parameterized test rather than a
/// fuzzer: it is deterministic, so CI never flakes, but its coverage is frozen at the
/// moment it was written and it will not discover anything new on its own. The determinism
/// is the trade - a failure is reproducible from the seed printed in the message.
/// </para>
/// <para>
/// The corpus is the vectors file rather than a second hand-written set, so the two cannot
/// drift. Structure-aware mutation of known-good datagrams is the point: parser defects live
/// in near-valid input, which is the region a generic generator would rarely reach.
/// </para>
/// <para>
/// The invariant is stated over the <em>accepted output</em>, not over the MAC. "Accepted
/// only when the mac verifies" would be satisfied by a pipeline that verified the signature
/// and then skipped the roster test and the <c>seq</c> bound - and a MAC-valid datagram
/// carrying a wildly advanced <c>seq</c> is exactly how §5.4 says ownership gets pinned
/// forever. What has to hold is that an accepted datagram satisfies every precondition the
/// reducer assumes.
/// </para>
/// </remarks>
public sealed class DatagramMutationTests
{
    private const int MutationsPerSeed = 400;

    /// <summary>Larger than the 512-byte bound, so the oversize rejection is reachable.</summary>
    private const int MaxMutantLength = 1024;

    public static TheoryData<int> Seeds => [20260928, 1, 7919, 104729, 2147483];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_mutated_datagram_never_throws_and_never_produces_an_unsafe_acceptance(int seed)
    {
        byte[][] corpus = Corpus();
        var random = new Random(seed);

        for (int iteration = 0; iteration < MutationsPerSeed; iteration++)
        {
            byte[] mutant = Mutate(random, corpus);
            ulong localSeq = (ulong)random.NextInt64();
            bool pairingWindowOpen = random.Next(2) == 0;
            string? rosterPeer = random.Next(2) == 0 ? "B" : null;

            Roster roster = WireVectorConstants.RosterFor(rosterPeer);
            IngressContext context = IngressContext.ForDatagram(
                WireVectorConstants.PairId, roster, localSeq, pairingWindowOpen);

            IngressOutcome outcome;
            try
            {
                outcome = IngressPipeline.Evaluate(mutant, context, WireVectorConstants.TestPairKey);
            }
            catch (Exception error)
            {
                Assert.Fail(
                    $"seed {seed}, iteration {iteration}: {error.GetType().Name} on " +
                    $"{Convert.ToHexStringLower(mutant)}");
                throw;
            }

            AssertContextUnchanged(context, roster, localSeq, pairingWindowOpen, seed, iteration);

            if (!outcome.IsAccepted)
            {
                continue;
            }

            AssertReducerPreconditions(outcome, context, mutant, seed, iteration);
        }
    }

    /// <summary>
    /// Random bytes, with no relationship to a valid datagram. Cheap, and it covers the
    /// framing stage that corpus mutation mostly walks past.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Arbitrary_bytes_are_never_accepted(int seed)
    {
        var random = new Random(seed);
        Roster roster = WireVectorConstants.RosterFor("B");

        for (int iteration = 0; iteration < MutationsPerSeed; iteration++)
        {
            byte[] noise = new byte[random.Next(0, MaxMutantLength)];
            random.NextBytes(noise);

            IngressContext context = IngressContext.ForDatagram(
                WireVectorConstants.PairId, roster, 41, pairingWindowOpen: true);

            IngressOutcome outcome = IngressPipeline.Evaluate(noise, context, WireVectorConstants.TestPairKey);

            Assert.False(
                outcome.IsAccepted,
                $"seed {seed}, iteration {iteration}: accepted {Convert.ToHexStringLower(noise)}");
        }
    }

    /// <summary>
    /// The codec is the outermost layer an unauthenticated broadcast reaches, so it gets the
    /// same treatment one level down.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void The_parser_never_throws_on_a_mutated_datagram(int seed)
    {
        byte[][] corpus = Corpus();
        var random = new Random(seed);
        byte[] mac = new byte[WireProtocol.MacByteLength];

        for (int iteration = 0; iteration < MutationsPerSeed; iteration++)
        {
            byte[] mutant = Mutate(random, corpus);

            DatagramParseResult result = DatagramCodec.TryParse(mutant, mac, out PeerDatagram parsed, out PairId pairId);

            if (result == DatagramParseResult.Ok)
            {
                // A parsed datagram must re-encode to something, which exercises the writer
                // on attacker-influenced values - notably seq and sentUtc extremes.
                byte[] reencoded = new byte[WireProtocol.MaxDatagramBytes];
                DatagramCodec.Encode(parsed, WireVectorConstants.TestPairKey, reencoded);
                Assert.Equal(parsed.PairId, pairId);
            }
        }
    }

    private static void AssertReducerPreconditions(
        IngressOutcome outcome, in IngressContext context, byte[] mutant, int seed, int iteration)
    {
        string where = $"seed {seed}, iteration {iteration}: {Convert.ToHexStringLower(mutant)}";
        PeerDatagram datagram = outcome.Datagram;

        Assert.True(datagram.PairId == context.PairId, $"{where} - accepted a foreign pairId");
        Assert.True(datagram.Version == WireProtocol.Version, $"{where} - accepted an unknown version");
        Assert.False(context.Roster.IsSelf(datagram.SenderId), $"{where} - accepted our own datagram");
        Assert.False(datagram.SenderId.IsNone, $"{where} - accepted the reserved value as a sender");

        if (outcome.Result == IngressResult.Accepted)
        {
            Assert.True(context.Roster.Contains(datagram.SenderId), $"{where} - accepted a sender outside the roster");
        }
        else
        {
            Assert.False(context.Roster.IsComplete, $"{where} - enrolled into a complete roster");
            Assert.True(context.PairingWindowOpen, $"{where} - enrolled with the pairing window closed");
        }

        Assert.False(
            datagram.Seq > context.LocalSeq && datagram.Seq - context.LocalSeq > WireProtocol.MaxSeqDelta,
            $"{where} - accepted a seq beyond the bound");

        byte[] mac = new byte[WireProtocol.MacByteLength];
        Assert.Equal(DatagramParseResult.Ok, DatagramCodec.TryParse(mutant, mac, out _, out _));
        Assert.True(
            DatagramCodec.VerifyMac(datagram, mac, WireVectorConstants.TestPairKey),
            $"{where} - accepted an unverified datagram");
    }

    private static void AssertContextUnchanged(
        in IngressContext context, Roster roster, ulong localSeq, bool pairingWindowOpen, int seed, int iteration)
    {
        string where = $"seed {seed}, iteration {iteration}";

        Assert.True(context.LocalSeq == localSeq, $"{where} - the pipeline mutated localSeq");
        Assert.True(context.PairingWindowOpen == pairingWindowOpen, $"{where} - the pipeline mutated the pairing window");
        Assert.True(ReferenceEquals(context.Roster, roster), $"{where} - the pipeline replaced the roster");
    }

    private static byte[][] Corpus()
    {
        WireVectorFile file = WireVectorSource.Load();

        return
        [
            .. file.RoundTrip.Select(vector => Encoding.UTF8.GetBytes(vector.Datagram)),
            .. file.Ingress.Select(vector => vector.ToBytes()),
        ];
    }

    private static byte[] Mutate(Random random, byte[][] corpus)
    {
        byte[] original = corpus[random.Next(corpus.Length)];
        byte[] mutant = [.. original];

        return random.Next(8) switch
        {
            0 => FlipBits(random, mutant),
            1 => Truncate(random, mutant),
            2 => Extend(random, mutant),
            3 => ReplaceRun(random, mutant),
            4 => Duplicate(mutant),
            5 => Splice(random, mutant, corpus),
            6 => Nest(mutant),
            _ => FlipBits(random, mutant),
        };
    }

    private static byte[] FlipBits(Random random, byte[] mutant)
    {
        int flips = random.Next(1, 5);

        for (int flip = 0; flip < flips && mutant.Length > 0; flip++)
        {
            int position = random.Next(mutant.Length);
            mutant[position] ^= (byte)(1 << random.Next(8));
        }

        return mutant;
    }

    private static byte[] Truncate(Random random, byte[] mutant) =>
        mutant.Length == 0 ? mutant : mutant[..random.Next(mutant.Length)];

    private static byte[] Extend(Random random, byte[] mutant)
    {
        int extra = random.Next(1, MaxMutantLength - Math.Min(mutant.Length, MaxMutantLength - 1));
        byte[] padding = new byte[extra];
        random.NextBytes(padding);
        return [.. mutant, .. padding];
    }

    private static byte[] ReplaceRun(Random random, byte[] mutant)
    {
        if (mutant.Length < 2)
        {
            return mutant;
        }

        int start = random.Next(mutant.Length - 1);
        int length = random.Next(1, Math.Min(16, mutant.Length - start));

        for (int index = start; index < start + length; index++)
        {
            mutant[index] = (byte)random.Next(256);
        }

        return mutant;
    }

    /// <summary>Duplicates the whole document, producing trailing data after a valid value.</summary>
    private static byte[] Duplicate(byte[] mutant) => [.. mutant, .. mutant];

    private static byte[] Splice(Random random, byte[] mutant, byte[][] corpus)
    {
        byte[] other = corpus[random.Next(corpus.Length)];

        if (mutant.Length == 0 || other.Length == 0)
        {
            return mutant;
        }

        int cut = random.Next(mutant.Length);
        int otherCut = random.Next(other.Length);

        return [.. mutant[..cut], .. other[otherCut..]];
    }

    /// <summary>Wraps the document, so an object value appears where a scalar belongs.</summary>
    private static byte[] Nest(byte[] mutant) =>
        [.. "{\"v\":"u8, .. mutant, .. "}"u8];
}
