using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Covers <c>docs/design.md</c> §5.3's ordinal-comparison requirement and §5's reserved
/// <see cref="MachineId.None"/>, plus the byte-lexicographic ordering §5.4's tiebreak and
/// ADR 0011's fingerprint both depend on.
/// </summary>
public sealed class MachineIdTests
{
    private const string CanonicalHex = "7f3a9c1e2d4b6a8035179246ab13cd5e";

    [Fact]
    public void Round_trips_the_canonical_lowercase_hex_rendering()
    {
        Assert.True(MachineId.TryParseRosterEntry(CanonicalHex, out MachineId id));
        Assert.Equal(CanonicalHex, id.ToString());
    }

    [Fact]
    public void Rejects_an_uppercase_rendering_of_an_otherwise_valid_identifier()
    {
        Assert.False(MachineId.TryParseOwner(CanonicalHex.ToUpperInvariant(), out _));
        Assert.False(MachineId.TryParseRosterEntry(CanonicalHex.ToUpperInvariant(), out _));
    }

    [Fact]
    public void Rejects_a_rendering_with_a_single_uppercase_character()
    {
        string oneUpper = string.Concat("7F", CanonicalHex.AsSpan(2));

        Assert.False(MachineId.TryParseOwner(oneUpper, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("7f3a")]
    [InlineData("7f3a9c1e2d4b6a8035179246ab13cd5")]
    [InlineData("7f3a9c1e2d4b6a8035179246ab13cd5e0")]
    [InlineData("7f3a9c1e2d4b6a8035179246ab13cd5g")]
    [InlineData("7f3a9c1e2d4b6a80 5179246ab13cd5e")]
    [InlineData("0x3a9c1e2d4b6a8035179246ab13cd5e")]
    public void Rejects_anything_that_is_not_exactly_thirty_two_lowercase_hex_characters(string text)
    {
        Assert.False(MachineId.TryParseOwner(text, out _));
    }

    [Fact]
    public void None_is_one_hundred_and_twenty_eight_bits_of_zero()
    {
        Assert.True(MachineId.None.IsNone);
        Assert.Equal(new string('0', 32), MachineId.None.ToString());
    }

    [Fact]
    public void Parsing_an_owner_accepts_None_because_it_is_the_ordinary_pre_claim_value()
    {
        Assert.True(MachineId.TryParseOwner(new string('0', 32), out MachineId owner));
        Assert.True(owner.IsNone);
        Assert.Equal(MachineId.None, owner);
    }

    [Fact]
    public void Parsing_a_roster_entry_rejects_None_so_it_can_never_be_enrolled()
    {
        Assert.False(MachineId.TryParseRosterEntry(new string('0', 32), out _));
    }

    [Fact]
    public void Equality_compares_values_not_references()
    {
        Assert.True(MachineId.TryParseRosterEntry(CanonicalHex, out MachineId first));
        Assert.True(MachineId.TryParseRosterEntry(CanonicalHex, out MachineId second));

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Round_trips_through_sixteen_big_endian_bytes()
    {
        Assert.True(MachineId.TryParseRosterEntry(CanonicalHex, out MachineId id));

        byte[] bytes = new byte[16];
        id.WriteTo(bytes);

        Assert.Equal(0x7f, bytes[0]);
        Assert.Equal(0x5e, bytes[15]);
        Assert.Equal(id, MachineId.FromBytes(bytes));
    }

    [Fact]
    public void Writes_the_canonical_hex_as_ascii_for_the_utf8_datagram()
    {
        Assert.True(MachineId.TryParseRosterEntry(CanonicalHex, out MachineId id));

        byte[] hex = new byte[32];
        id.WriteHexUtf8(hex);

        Assert.Equal(CanonicalHex, System.Text.Encoding.ASCII.GetString(hex));
    }

    /// <summary>
    /// The premise of storing an identifier as two big-endian halves: comparing them as
    /// unsigned integers must be indistinguishable from comparing the sixteen bytes. Both
    /// places that rely on it - §5.4's tiebreak and ADR 0011's fingerprint over the roster
    /// IDs in sorted order - fail misleadingly rather than loudly if it ever stops holding.
    /// </summary>
    [Fact]
    public void CompareTo_orders_identically_to_a_bytewise_comparison()
    {
        var random = new Random(Seed: 20260928);
        byte[] leftBytes = new byte[16];
        byte[] rightBytes = new byte[16];

        for (int iteration = 0; iteration < 2000; iteration++)
        {
            random.NextBytes(leftBytes);
            random.NextBytes(rightBytes);

            int expected = Math.Sign(((ReadOnlySpan<byte>)leftBytes).SequenceCompareTo(rightBytes));
            int actual = Math.Sign(MachineId.FromBytes(leftBytes).CompareTo(MachineId.FromBytes(rightBytes)));

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// Random pairs almost always differ in the first byte, which never exercises the low
    /// half. This walks the difference across all sixteen positions, so an endianness flip
    /// in either half is caught.
    /// </summary>
    [Fact]
    public void CompareTo_orders_identically_when_only_one_byte_differs()
    {
        for (int position = 0; position < 16; position++)
        {
            byte[] lowerBytes = new byte[16];
            byte[] higherBytes = new byte[16];
            lowerBytes[position] = 0x10;
            higherBytes[position] = 0x20;

            MachineId lower = MachineId.FromBytes(lowerBytes);
            MachineId higher = MachineId.FromBytes(higherBytes);

            Assert.True(lower < higher, $"byte {position} should order low-to-high");
            Assert.Equal(
                Math.Sign(((ReadOnlySpan<byte>)lowerBytes).SequenceCompareTo(higherBytes)),
                Math.Sign(lower.CompareTo(higher)));
        }
    }

    /// <summary>
    /// Bytes are unsigned, so an identifier with the top bit set is the larger one. A
    /// signed comparison of the halves would invert this.
    /// </summary>
    [Fact]
    public void CompareTo_treats_the_high_bit_as_magnitude_not_sign()
    {
        byte[] smallBytes = new byte[16];
        byte[] largeBytes = new byte[16];
        smallBytes[0] = 0x7f;
        largeBytes[0] = 0x80;

        Assert.True(MachineId.FromBytes(smallBytes) < MachineId.FromBytes(largeBytes));
    }

    [Fact]
    public void Reading_from_the_wrong_number_of_bytes_throws_rather_than_truncating()
    {
        Assert.Throws<ArgumentException>(() => MachineId.FromBytes(new byte[15]));
        Assert.Throws<ArgumentException>(() => MachineId.FromBytes(new byte[17]));
    }
}
