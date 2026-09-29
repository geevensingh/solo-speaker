using System.Buffers.Binary;

namespace SoloSpeaker.Core.Identity;

/// <summary>
/// Parsing and formatting for a 128-bit identifier as exactly 32 lowercase hex characters -
/// canonicalization rule 5 of <c>docs/wire-format.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Formatting goes through <see cref="Convert.TryToHexStringLower(ReadOnlySpan{byte}, Span{char}, out int)"/>,
/// which is culture-free <em>by construction</em> rather than by build flag. That
/// distinction is the point: <c>InvariantGlobalization</c> exists so a culture-sensitive
/// comparison cannot be introduced by accident, not so culture-sensitive code is acceptable
/// while the flag holds.
/// </para>
/// <para>
/// Parsing is hand-rolled because <see cref="Convert.FromHexString(string)"/> is
/// case-<em>insensitive</em>, which is exactly what rule 5 forbids. An uppercase hex string
/// is not a different spelling of the same identifier; it is not an identifier at all.
/// </para>
/// </remarks>
internal static class Hex128
{
    /// <summary>Characters in the canonical rendering.</summary>
    internal const int CharCount = 32;

    /// <summary>Bytes in the underlying identifier.</summary>
    internal const int ByteCount = 16;

    private const int HalfByteCount = ByteCount / 2;

    /// <summary>
    /// Parses exactly <see cref="CharCount"/> characters drawn from <c>[0-9a-f]</c>,
    /// big-endian, into a high and low half. Any other length, and any uppercase character,
    /// fails.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<char> text, out ulong high, out ulong low)
    {
        high = 0;
        low = 0;

        if (text.Length != CharCount)
        {
            return false;
        }

        ulong parsedHigh = 0;
        ulong parsedLow = 0;

        for (int index = 0; index < CharCount; index++)
        {
            int nibble = ParseNibble(text[index]);
            if (nibble < 0)
            {
                return false;
            }

            if (index < CharCount / 2)
            {
                parsedHigh = (parsedHigh << 4) | (uint)nibble;
            }
            else
            {
                parsedLow = (parsedLow << 4) | (uint)nibble;
            }
        }

        high = parsedHigh;
        low = parsedLow;
        return true;
    }

    /// <summary>
    /// Writes the two halves as big-endian bytes. Big-endian is what makes an unsigned
    /// comparison of <c>(high, low)</c> identical to a byte-lexicographic comparison, which
    /// is the equivalence <c>docs/design.md</c> §5.4's tiebreak rests on.
    /// </summary>
    internal static void WriteBytes(ulong high, ulong low, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[HalfByteCount..], low);
    }

    /// <summary>Reads two halves from big-endian bytes.</summary>
    internal static void ReadBytes(ReadOnlySpan<byte> source, out ulong high, out ulong low)
    {
        high = BinaryPrimitives.ReadUInt64BigEndian(source);
        low = BinaryPrimitives.ReadUInt64BigEndian(source[HalfByteCount..]);
    }

    /// <summary>Renders the canonical 32-character lowercase hex form.</summary>
    internal static string Format(ulong high, ulong low)
    {
        Span<byte> bytes = stackalloc byte[ByteCount];
        WriteBytes(high, low, bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Writes the canonical form as <see cref="CharCount"/> ASCII bytes, for the UTF-8
    /// canonical datagram. Hex is ASCII by definition, so narrowing from the shared
    /// character implementation cannot lose information - and keeping one hex
    /// implementation is what stops the wire and the log from drifting apart.
    /// </summary>
    internal static void FormatUtf8(ulong high, ulong low, Span<byte> destination)
    {
        Span<byte> bytes = stackalloc byte[ByteCount];
        WriteBytes(high, low, bytes);

        Span<char> characters = stackalloc char[CharCount];
        if (!Convert.TryToHexStringLower(bytes, characters, out int written) || written != CharCount)
        {
            throw new InvalidOperationException(
                "Hex formatting of a 128-bit identifier did not produce 32 characters.");
        }

        for (int index = 0; index < CharCount; index++)
        {
            destination[index] = (byte)characters[index];
        }
    }

    private static int ParseNibble(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        _ => -1,
    };
}
