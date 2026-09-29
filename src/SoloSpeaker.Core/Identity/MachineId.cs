namespace SoloSpeaker.Core.Identity;

/// <summary>
/// An opaque 128-bit roster identifier - the domain of <c>activeOwner</c> in
/// <c>docs/design.md</c> §5.3, together with the reserved <see cref="None"/> of §5.
/// </summary>
/// <remarks>
/// <para>
/// Stored as two <see cref="ulong"/> halves read big-endian, not as a byte array. Three
/// properties follow, and each is load-bearing:
/// </para>
/// <list type="bullet">
/// <item>
/// Equality is integer equality. A <c>byte[]</c> would have given <em>reference</em>
/// equality, so a roster membership test would have silently returned
/// <see langword="false"/> forever - on the right-hand side of §5.5's predicate.
/// </item>
/// <item>
/// Comparison of the halves as unsigned integers is byte-lexicographic over the sixteen
/// bytes, so §5.4's "lexicographically smaller roster ID, evaluated identically on both
/// sides" is exact. <c>MachineIdTests</c> asserts that equivalence against a byte-wise
/// comparison rather than trusting it, because both places that depend on it - the §5.4
/// tiebreak and ADR 0011's fingerprint over the roster IDs <em>in sorted order</em> - fail
/// misleadingly rather than loudly.
/// </item>
/// <item>
/// There is no string anywhere in the comparison path, which is the mechanism
/// <c>docs/review-2026-09-28.md</c> H-9 asked for. It is the representation that removes
/// the casing hazard, not any build flag.
/// </item>
/// </list>
/// </remarks>
public readonly struct MachineId : IEquatable<MachineId>, IComparable<MachineId>
{
    private readonly ulong _high;
    private readonly ulong _low;

    private MachineId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>
    /// The reserved pre-claim value of <c>docs/design.md</c> §5 - 128 bits of zero, naming
    /// no machine.
    /// </summary>
    /// <remarks>
    /// <c>activeOwner</c> holds this between the pairing ceremony and the first §5.1 write.
    /// §5.5's predicate is false on both machines while it does, so both stay audible until
    /// somebody deliberately claims. It is never generated at enrollment and is never a
    /// valid roster entry - see <see cref="TryParseRosterEntry"/> - because a roster
    /// containing it would satisfy <c>activeOwner == peerRosterId</c> and mute a machine
    /// for a peer that does not exist.
    /// </remarks>
    public static MachineId None => default;

    /// <summary>Whether this is the reserved <see cref="None"/> value.</summary>
    public bool IsNone => _high == 0 && _low == 0;

    /// <summary>
    /// Parses a roster entry - <c>machineId</c> on the wire, a roster ID in
    /// <c>config.json</c>, or a roster ID from a pairing bundle. Strict canonical hex, and
    /// <see cref="None"/> is <b>rejected</b>.
    /// </summary>
    /// <remarks>
    /// This is the guarded half of the pair. <see cref="None"/> must never reach a roster by
    /// any path, so the parse used for roster entries refuses it rather than relying on
    /// every caller to remember.
    /// </remarks>
    public static bool TryParseRosterEntry(ReadOnlySpan<char> text, out MachineId id)
    {
        if (!TryParseOwner(text, out MachineId parsed) || parsed.IsNone)
        {
            id = default;
            return false;
        }

        id = parsed;
        return true;
    }

    /// <summary>
    /// Parses an <c>activeOwner</c> value. Strict canonical hex, and <see cref="None"/> is
    /// accepted, because §5 makes it the ordinary pre-claim value.
    /// </summary>
    /// <remarks>
    /// Uppercase hex does not parse. <c>docs/wire-format.md</c> canonicalization rule 5
    /// makes lowercase normative, so an uppercase rendering never becomes a
    /// <see cref="MachineId"/> and therefore can never match a roster entry - which is what
    /// §10's case-only row requires. Note what that row now asserts and what it does not:
    /// it pins the parser, not the comparison. The comparison cannot be made
    /// case-sensitive wrongly because no string reaches it, but a stray
    /// <c>OrdinalIgnoreCase</c> in some future hex path remains issue #6's open concern.
    /// </remarks>
    public static bool TryParseOwner(ReadOnlySpan<char> text, out MachineId id)
    {
        if (!Hex128.TryParse(text, out ulong high, out ulong low))
        {
            id = default;
            return false;
        }

        id = new MachineId(high, low);
        return true;
    }

    /// <summary>Reads an identifier from exactly sixteen big-endian bytes.</summary>
    public static MachineId FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length != Hex128.ByteCount)
        {
            throw new ArgumentException(
                "A machine ID is exactly 16 bytes.", nameof(source));
        }

        Hex128.ReadBytes(source, out ulong high, out ulong low);
        return new MachineId(high, low);
    }

    /// <summary>Writes this identifier as sixteen big-endian bytes.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Hex128.ByteCount)
        {
            throw new ArgumentException(
                "A machine ID needs 16 bytes of destination.", nameof(destination));
        }

        Hex128.WriteBytes(_high, _low, destination);
    }

    /// <summary>
    /// Writes the canonical 32-character lowercase hex form as ASCII bytes, for the UTF-8
    /// canonical datagram.
    /// </summary>
    public void WriteHexUtf8(Span<byte> destination)
    {
        if (destination.Length < Hex128.CharCount)
        {
            throw new ArgumentException(
                "Canonical hex needs 32 bytes of destination.", nameof(destination));
        }

        Hex128.FormatUtf8(_high, _low, destination);
    }

    /// <inheritdoc/>
    public bool Equals(MachineId other) => _high == other._high && _low == other._low;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MachineId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>
    /// Orders identifiers byte-lexicographically, which is what <c>docs/design.md</c> §5.4
    /// means by "the lexicographically smaller roster ID".
    /// </summary>
    public int CompareTo(MachineId other)
    {
        int highComparison = _high.CompareTo(other._high);
        return highComparison != 0 ? highComparison : _low.CompareTo(other._low);
    }

    /// <summary>The canonical 32-character lowercase hex rendering.</summary>
    public override string ToString() => Hex128.Format(_high, _low);

    public static bool operator ==(MachineId left, MachineId right) => left.Equals(right);

    public static bool operator !=(MachineId left, MachineId right) => !left.Equals(right);

    public static bool operator <(MachineId left, MachineId right) => left.CompareTo(right) < 0;

    public static bool operator <=(MachineId left, MachineId right) => left.CompareTo(right) <= 0;

    public static bool operator >(MachineId left, MachineId right) => left.CompareTo(right) > 0;

    public static bool operator >=(MachineId left, MachineId right) => left.CompareTo(right) >= 0;
}
