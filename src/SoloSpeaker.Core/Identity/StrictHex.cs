namespace SoloSpeaker.Core.Identity;

/// <summary>
/// The single implementation of "strict lowercase hex" for the whole codebase.
/// </summary>
/// <remarks>
/// Canonicalization rule 5 of <c>docs/wire-format.md</c> makes lowercase normative, and
/// every hex-shaped value on the wire - <c>pairId</c>, <c>machineId</c>, <c>activeOwner</c>,
/// and the 256-bit <c>mac</c> - obeys the same rule. Keeping one nibble table is the point:
/// a second copy is how the roster path and the MAC path come to disagree about whether
/// <c>"AB"</c> is a valid byte.
/// </remarks>
internal static class StrictHex
{
    /// <summary>
    /// Converts one lowercase hex character to its value, or <c>-1</c>. Uppercase is not a
    /// different spelling of the same nibble; it is not a nibble.
    /// </summary>
    internal static int ParseNibble(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        _ => -1,
    };

    /// <summary>
    /// Decodes exactly <c>2 * destination.Length</c> lowercase hex characters into bytes.
    /// Any other length, and any character outside <c>[0-9a-f]</c>, fails.
    /// </summary>
    internal static bool TryDecode(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (text.Length != destination.Length * 2)
        {
            return false;
        }

        for (int index = 0; index < destination.Length; index++)
        {
            int high = ParseNibble(text[index * 2]);
            int low = ParseNibble(text[(index * 2) + 1]);

            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }
}
