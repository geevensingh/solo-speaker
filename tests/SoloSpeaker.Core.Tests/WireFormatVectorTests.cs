using System.Text;
using System.Text.RegularExpressions;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Asserts the frozen v1 byte form against <c>docs/wire-format.vectors.json</c>.
/// </summary>
/// <remarks>
/// Every load path fails loudly. A suite that silently parsed zero vectors would be green
/// while asserting nothing, which would remove the only enforcement behind
/// <c>docs/design.md</c> §8's freeze - and with it the only checks on identifier endianness
/// and on 64-bit <c>seq</c> parse precision.
/// </remarks>
public sealed class WireFormatVectorTests
{
    private const int ExpectedRoundTripCount = 10;
    private const int ExpectedIngressCount = 20;

    [Fact]
    public void The_vector_file_is_present_and_holds_every_vector()
    {
        WireVectorFile file = WireVectorSource.Load();

        Assert.Equal(ExpectedRoundTripCount, file.RoundTrip.Count);
        Assert.Equal(ExpectedIngressCount, file.Ingress.Count);
        Assert.Equal(Convert.ToHexStringLower(WireVectorConstants.TestPairKey), file.TestPairKeyHex);
    }

    /// <summary>
    /// The meta-test for the guard: a missing artifact must fail the suite rather than
    /// quietly yield an empty set of vectors to iterate.
    /// </summary>
    [Fact]
    public void A_missing_vector_file_fails_loudly_rather_than_yielding_nothing()
    {
        string absent = Path.Combine(Path.GetTempPath(), $"solo-speaker-absent-{Guid.NewGuid():N}.json");

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => WireVectorSource.Load(absent));

        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_vector_file_fails_loudly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"solo-speaker-empty-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");

        try
        {
            Assert.Throws<InvalidOperationException>(() => WireVectorSource.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Vector_names_are_unique()
    {
        WireVectorFile file = WireVectorSource.Load();
        string[] names =
        [
            .. file.RoundTrip.Select(vector => vector.Name),
            .. file.Ingress.Select(vector => vector.Name),
        ];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// <c>docs/wire-format.md</c> tables the vectors and says what each covers. If a vector
    /// is added to the artifact without a row, or a row outlives its vector, the normative
    /// document has stopped describing the normative artifact.
    /// </summary>
    [Fact]
    public void Vector_names_match_the_tables_in_wire_format_md()
    {
        WireVectorFile file = WireVectorSource.Load();
        string markdown = File.ReadAllText(WireVectorSource.MarkdownPath());

        List<string> documented =
        [
            .. Regex.Matches(markdown, "`(v1-[a-z0-9]+(?:-[a-z0-9]+)*)`", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        List<string> defined =
        [
            .. file.RoundTrip.Select(vector => vector.Name)
                .Concat(file.Ingress.Select(vector => vector.Name))
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        Assert.NotEmpty(documented);
        Assert.Equal(defined, documented);
    }

    [Fact]
    public void Round_trip_vectors_encode_to_their_frozen_bytes()
    {
        WireVectorFile file = WireVectorSource.Load();
        byte[] canonical = new byte[WireProtocol.MaxDatagramBytes];
        byte[] full = new byte[WireProtocol.MaxDatagramBytes];

        foreach (RoundTripVector vector in file.RoundTrip)
        {
            PeerDatagram datagram = ToDatagram(vector);

            int canonicalLength = DatagramCodec.WriteCanonical(datagram, canonical);
            Assert.Equal(vector.Canonical, Encoding.UTF8.GetString(canonical, 0, canonicalLength));

            int fullLength = DatagramCodec.Encode(datagram, WireVectorConstants.TestPairKey, full);
            Assert.Equal(vector.Datagram, Encoding.UTF8.GetString(full, 0, fullLength));

            Assert.EndsWith($"\"mac\":\"{vector.Mac}\"}}", vector.Datagram, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Round_trip_vectors_parse_back_to_their_field_values()
    {
        WireVectorFile file = WireVectorSource.Load();
        byte[] mac = new byte[WireProtocol.MacByteLength];

        foreach (RoundTripVector vector in file.RoundTrip)
        {
            DatagramParseResult result = DatagramCodec.TryParse(
                Encoding.UTF8.GetBytes(vector.Datagram), mac, out PeerDatagram parsed, out PairId pairId);

            Assert.Equal(DatagramParseResult.Ok, result);
            Assert.Equal(ToDatagram(vector), parsed);
            Assert.Equal(WireVectorConstants.ParsePairId(vector.PairId), pairId);
            Assert.Equal(vector.Mac, Convert.ToHexStringLower(mac));
            Assert.True(DatagramCodec.VerifyMac(parsed, mac, WireVectorConstants.TestPairKey), vector.Name);
        }
    }

    /// <summary>
    /// Freezes canonicalization rule 1. A reordered canonicalizer would still round-trip
    /// cleanly; only a byte-exact vector catches it, and only if the field order is asserted
    /// somewhere a reader can check.
    /// </summary>
    [Fact]
    public void The_canonical_field_order_is_the_one_wire_format_md_tables()
    {
        RoundTripVector baseline = WireVectorSource.Load().RoundTrip.Single(vector => vector.Name == "v1-baseline");

        Assert.StartsWith(
            "{\"v\":1,\"pairId\":\"", baseline.Canonical, StringComparison.Ordinal);
        Assert.Equal(
            ["v", "pairId", "machineId", "seq", "activeOwner", "micLive", "bye", "sentUtc"],
            Regex.Matches(baseline.Canonical, "\"([a-zA-Z]+)\":", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(match => match.Groups[1].Value)
                .ToArray());
        Assert.DoesNotContain("\"mac\":", baseline.Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards 64-bit precision explicitly. A parser routing the number through a double
    /// would turn <c>18446744073709551615</c> into <c>18446744073709551616</c>, and every
    /// other assertion in the suite would still pass.
    /// </summary>
    [Fact]
    public void The_seq_max_vector_survives_a_parse_without_losing_precision()
    {
        RoundTripVector vector = WireVectorSource.Load().RoundTrip.Single(candidate => candidate.Name == "v1-seq-max");

        Assert.Equal(ulong.MaxValue, vector.Seq);
        Assert.Contains("\"seq\":18446744073709551615,", vector.Canonical, StringComparison.Ordinal);

        byte[] mac = new byte[WireProtocol.MacByteLength];
        Assert.Equal(
            DatagramParseResult.Ok,
            DatagramCodec.TryParse(Encoding.UTF8.GetBytes(vector.Datagram), mac, out PeerDatagram parsed, out _));
        Assert.Equal(ulong.MaxValue, parsed.Seq);
    }

    /// <summary>
    /// The pre-claim owner of §5 is thirty-two zeros on the wire, and it round-trips as
    /// <see cref="MachineId.None"/> rather than failing to parse.
    /// </summary>
    [Fact]
    public void The_pre_claim_owner_is_frozen_as_thirty_two_zeros()
    {
        RoundTripVector vector = WireVectorSource.Load().RoundTrip.Single(candidate => candidate.Name == "v1-owner-none");

        Assert.Contains($"\"activeOwner\":\"{new string('0', 32)}\"", vector.Canonical, StringComparison.Ordinal);

        byte[] mac = new byte[WireProtocol.MacByteLength];
        Assert.Equal(
            DatagramParseResult.Ok,
            DatagramCodec.TryParse(Encoding.UTF8.GetBytes(vector.Datagram), mac, out PeerDatagram parsed, out _));
        Assert.True(parsed.ActiveOwner.IsNone);
    }

    [Fact]
    public void Ingress_vectors_produce_their_frozen_outcome()
    {
        foreach (IngressVector vector in WireVectorSource.Load().Ingress)
        {
            IngressContext context = WireVectorConstants.Context(
                vector.RosterPeer, vector.LocalSeq, vector.PairingWindowOpen);

            IngressOutcome outcome = IngressPipeline.Evaluate(
                vector.ToBytes(), context, WireVectorConstants.TestPairKey);

            Assert.Equal(Enum.Parse<IngressResult>(vector.Expected), outcome.Result);
        }
    }

    /// <summary>
    /// Regenerates the artifact. Deliberately gated on an environment variable: changing a
    /// vector must be a deliberate act that forces the question "does this need a <c>v</c>
    /// bump, and does it need both machines updated at once?"
    /// </summary>
    [Fact]
    public void Regenerate_the_vector_file_when_explicitly_asked()
    {
        if (Environment.GetEnvironmentVariable("SOLO_SPEAKER_UPDATE_WIRE_VECTORS") != "1")
        {
            return;
        }

        WireVectorSource.Write(WireVectorDefinitions.Build());
    }

    private static PeerDatagram ToDatagram(RoundTripVector vector) =>
        WireVectorConstants.Datagram(
            vector.Version, vector.PairId, vector.MachineId, vector.Seq,
            vector.ActiveOwner, vector.MicLive, vector.Bye, vector.SentUtc);
}
