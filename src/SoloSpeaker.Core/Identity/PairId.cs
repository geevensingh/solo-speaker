namespace SoloSpeaker.Core.Identity;

/// <summary>
/// The 128-bit pairing GUID of <c>docs/design.md</c> §7.1 - transmitted on every datagram to
/// scope the broadcast namespace.
/// </summary>
/// <remarks>
/// <para>
/// A distinct type from <see cref="MachineId"/> despite the identical 128-bit lowercase-hex
/// shape, and the reason is specific rather than symmetry. Ingress step 1 compares
/// <c>pairId</c> and step 3 compares <c>machineId</c>. With one type, transposing them
/// compiles and yields a pipeline that accepts the wrong datagrams - a silent-accept defect
/// in the security-relevant path, and one that would also destroy the step-1-versus-step-2
/// discrimination §7.1's unverifiable-peer producer is built on. The compiler should
/// refuse.
/// </para>
/// <para>
/// Note the asymmetry with <c>pairKey</c>, which deliberately stays a plain
/// <see cref="byte"/> array at the <c>IConfigStore</c> seam. A <c>PairKey</c> struct would
/// acquire a hex <c>ToString</c> by the same symmetry pressure, and that is one careless log
/// line away from re-committing revision 1's Critical defect in a new shape.
/// </para>
/// <para>
/// All-zero is <em>not</em> rejected here. A zero <c>pairId</c> is merely improbable rather
/// than reserved, and letting it parse keeps ingress attribution clean: an incoming zero
/// simply mismatches our own and dies at step 1 as ordinary foreign traffic. Rejecting a
/// zero <c>pairId</c> belongs at the config boundary, where it means an uninitialised file.
/// </para>
/// </remarks>
public readonly struct PairId : IEquatable<PairId>
{
    private readonly ulong _high;
    private readonly ulong _low;

    private PairId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>Whether this is all-zero, which at the config boundary means uninitialised.</summary>
    public bool IsZero => _high == 0 && _low == 0;

    /// <summary>Parses the canonical 32-character lowercase hex form. Uppercase does not parse.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out PairId id)
    {
        if (!Hex128.TryParse(text, out ulong high, out ulong low))
        {
            id = default;
            return false;
        }

        id = new PairId(high, low);
        return true;
    }

    /// <summary>Reads a pairing GUID from exactly sixteen big-endian bytes.</summary>
    public static PairId FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length != Hex128.ByteCount)
        {
            throw new ArgumentException(
                "A pair ID is exactly 16 bytes.", nameof(source));
        }

        Hex128.ReadBytes(source, out ulong high, out ulong low);
        return new PairId(high, low);
    }

    /// <summary>Writes this pairing GUID as sixteen big-endian bytes.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Hex128.ByteCount)
        {
            throw new ArgumentException(
                "A pair ID needs 16 bytes of destination.", nameof(destination));
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
    public bool Equals(PairId other) => _high == other._high && _low == other._low;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PairId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>The canonical 32-character lowercase hex rendering.</summary>
    public override string ToString() => Hex128.Format(_high, _low);

    public static bool operator ==(PairId left, PairId right) => left.Equals(right);

    public static bool operator !=(PairId left, PairId right) => !left.Equals(right);
}
