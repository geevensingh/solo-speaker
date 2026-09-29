using System.Security.Cryptography;
using System.Text.Json;
using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.PeerLink.Wire;

/// <summary>
/// The outcome of reading raw bytes as a v1 datagram, before any ingress decision is taken.
/// </summary>
/// <remarks>
/// The three values exist because <c>docs/design.md</c> §7.1's numbered list presupposes a
/// parse: steps 1 and 2 both need fields read out of the document. A datagram that cannot
/// yield its <c>pairId</c> is attributable to no numbered step at all, which is what
/// <see cref="Unreadable"/> names, while one that yields a <c>pairId</c> and then fails any
/// other canonical rule is attributable to step 2 - see <see cref="NonCanonical"/>.
/// </remarks>
public enum DatagramParseResult
{
    /// <summary>A canonical v1 document. Every field is present and every scalar is in its canonical form.</summary>
    Ok,

    /// <summary>
    /// Not attributable. Over the size bound, not UTF-8, not a single JSON object, or with
    /// no readable canonical <c>pairId</c>. Indistinguishable from ordinary foreign traffic
    /// on the port, so the pipeline drops it silently rather than lighting an error icon
    /// that would then never go out.
    /// </summary>
    Unreadable,

    /// <summary>
    /// A readable document carrying a usable <c>pairId</c> that is not canonical v1 - a
    /// missing field, an unknown key, a duplicate key, an escaped string, trailing bytes, or
    /// a scalar outside its canonical form.
    /// </summary>
    /// <remarks>
    /// The <c>pairId</c> is still returned, because step 1 must be evaluated before this
    /// becomes a step 2 rejection. Attributing it any earlier is what would silently break
    /// §7.1's unverifiable-peer producer: a peer on a later wire version is exactly a
    /// <c>pairId</c>-matching document this receiver cannot reconstruct, and it has to be
    /// counted rather than discarded as noise.
    /// </remarks>
    NonCanonical,
}

/// <summary>
/// Reads and writes the frozen v1 byte form of <c>docs/wire-format.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Emitting is hand-rolled so that canonicalization rules 1 through 8 are readable as code,
/// and so that no formatting machinery sits between a value and its bytes. Every scalar -
/// hex, unsigned decimal, bare boolean, fixed-shape timestamp - is written digit by digit,
/// which makes the canonical form culture-free <em>by construction</em> rather than by build
/// flag.
/// </para>
/// <para>
/// Reading uses <see cref="Utf8JsonReader"/> for UTF-8 validation, depth limits, and
/// trailing-data detection, then applies the canonical scalar rules itself. The reader
/// signals malformed input by throwing; that exception is converted into an explicit
/// <see cref="DatagramParseResult"/> at this boundary and returned to the caller, which is
/// how a hostile datagram becomes a counted rejection reason rather than a crash. It is not
/// swallowed - every path that catches it returns a reason the pipeline records.
/// </para>
/// </remarks>
public static class DatagramCodec
{
    private const int IdHexLength = 32;
    private const int TimestampLength = 24;

    /// <summary>
    /// Writes the canonical form with the <c>mac</c> field omitted entirely - the bytes the
    /// HMAC is computed over, per canonicalization rule 2.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public static int WriteCanonical(in PeerDatagram datagram, Span<byte> destination)
    {
        if (datagram.Version < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(datagram), "Canonicalization rule 6 forbids a signed version.");
        }

        int offset = 0;
        WriteLiteral("{\"v\":"u8, destination, ref offset);
        WriteUnsigned((ulong)datagram.Version, destination, ref offset);

        WriteLiteral(",\"pairId\":\""u8, destination, ref offset);
        datagram.PairId.WriteHexUtf8(destination.Slice(offset, IdHexLength));
        offset += IdHexLength;

        WriteLiteral("\",\"machineId\":\""u8, destination, ref offset);
        datagram.SenderId.WriteHexUtf8(destination.Slice(offset, IdHexLength));
        offset += IdHexLength;

        WriteLiteral("\",\"seq\":"u8, destination, ref offset);
        WriteUnsigned(datagram.Seq, destination, ref offset);

        WriteLiteral(",\"activeOwner\":\""u8, destination, ref offset);
        datagram.ActiveOwner.WriteHexUtf8(destination.Slice(offset, IdHexLength));
        offset += IdHexLength;

        WriteLiteral("\",\"micLive\":"u8, destination, ref offset);
        WriteLiteral(datagram.MicLive ? "true"u8 : "false"u8, destination, ref offset);

        WriteLiteral(",\"bye\":"u8, destination, ref offset);
        WriteLiteral(datagram.Bye ? "true"u8 : "false"u8, destination, ref offset);

        WriteLiteral(",\"sentUtc\":\""u8, destination, ref offset);
        WriteTimestamp(datagram.SentUtc, destination, ref offset);

        WriteLiteral("\"}"u8, destination, ref offset);
        return offset;
    }

    /// <summary>
    /// Writes the full datagram: the canonical form with <c>mac</c> appended as the last
    /// field.
    /// </summary>
    /// <param name="datagram">The eight signed fields.</param>
    /// <param name="pairKey">
    /// The HMAC key. A span rather than a stored member, so the secret cannot be captured
    /// into a record, boxed, or rendered by a generated <c>ToString</c>.
    /// </param>
    /// <param name="destination">At least <see cref="WireProtocol.MaxDatagramBytes"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Encode(in PeerDatagram datagram, ReadOnlySpan<byte> pairKey, Span<byte> destination)
    {
        Span<byte> canonical = stackalloc byte[WireProtocol.MaxDatagramBytes];
        int canonicalLength = WriteCanonical(datagram, canonical);

        Span<byte> mac = stackalloc byte[WireProtocol.MacByteLength];
        HMACSHA256.HashData(pairKey, canonical[..canonicalLength], mac);

        // Everything except the canonical form's closing brace, then the mac field.
        int bodyLength = canonicalLength - 1;
        canonical[..bodyLength].CopyTo(destination);

        int offset = bodyLength;
        WriteLiteral(",\"mac\":\""u8, destination, ref offset);
        WriteHexLower(mac, destination.Slice(offset, WireProtocol.MacHexLength));
        offset += WireProtocol.MacHexLength;
        WriteLiteral("\"}"u8, destination, ref offset);

        return offset;
    }

    /// <summary>
    /// Recomputes the MAC from the parsed field values and compares it in constant time.
    /// </summary>
    /// <remarks>
    /// Canonicalization rule 2 makes re-canonicalization normative: the receiver MACs the
    /// form it re-emits, never the bytes it received. One consequence is load-bearing - a
    /// datagram missing any signed field cannot be reconstructed, so it can never be
    /// verified and dies at ingress step 2 rather than step 5.
    /// </remarks>
    public static bool VerifyMac(in PeerDatagram datagram, ReadOnlySpan<byte> receivedMac, ReadOnlySpan<byte> pairKey)
    {
        if (receivedMac.Length != WireProtocol.MacByteLength)
        {
            return false;
        }

        Span<byte> canonical = stackalloc byte[WireProtocol.MaxDatagramBytes];
        int canonicalLength = WriteCanonical(datagram, canonical);

        Span<byte> computed = stackalloc byte[WireProtocol.MacByteLength];
        HMACSHA256.HashData(pairKey, canonical[..canonicalLength], computed);

        return CryptographicOperations.FixedTimeEquals(computed, receivedMac);
    }

    /// <summary>
    /// Reads raw bytes as a v1 datagram, applying every canonical rule but taking no ingress
    /// decision.
    /// </summary>
    /// <param name="source">The received bytes.</param>
    /// <param name="receivedMac">
    /// Receives the <c>mac</c> field. Exactly <see cref="WireProtocol.MacByteLength"/> bytes,
    /// and only meaningful when the result is <see cref="DatagramParseResult.Ok"/>.
    /// </param>
    /// <param name="parsed">The eight signed fields, when the result is <see cref="DatagramParseResult.Ok"/>.</param>
    /// <param name="pairId">
    /// The <c>pairId</c>, whenever it was readable - including when the rest of the document
    /// was not canonical, because ingress step 1 is evaluated before step 2.
    /// </param>
    public static DatagramParseResult TryParse(
        ReadOnlySpan<byte> source,
        Span<byte> receivedMac,
        out PeerDatagram parsed,
        out PairId pairId)
    {
        parsed = default;
        pairId = default;

        if (receivedMac.Length != WireProtocol.MacByteLength)
        {
            throw new ArgumentException(
                "A MAC destination is exactly 32 bytes.", nameof(receivedMac));
        }

        if (source.Length is 0 or > WireProtocol.MaxDatagramBytes)
        {
            return DatagramParseResult.Unreadable;
        }

        var fields = default(RawFields);
        bool wellFormed = TryReadFields(source, receivedMac, ref fields);

        if (!fields.HasPairId)
        {
            return DatagramParseResult.Unreadable;
        }

        pairId = fields.PairId;

        if (!wellFormed ||
            fields.HasUnknownKey ||
            fields.HasDuplicateKey ||
            fields.HasNonCanonicalValue ||
            !fields.IsComplete)
        {
            return DatagramParseResult.NonCanonical;
        }

        parsed = new PeerDatagram(
            fields.Version,
            fields.PairId,
            fields.SenderId,
            fields.Seq,
            fields.ActiveOwner,
            fields.MicLive,
            fields.Bye,
            fields.SentUtc);

        return DatagramParseResult.Ok;
    }

    private static bool TryReadFields(ReadOnlySpan<byte> source, Span<byte> receivedMac, ref RawFields fields)
    {
        var options = new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        };

        var reader = new Utf8JsonReader(source, options);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!ReadProperty(ref reader, receivedMac, ref fields))
                {
                    return false;
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                return false;
            }

            // Rule 4 forbids insignificant whitespace, so a canonical document is consumed
            // exactly. Trailing bytes of any kind make this non-canonical rather than
            // unreadable, because the pairId has already been recovered.
            if (reader.BytesConsumed != source.Length)
            {
                fields.HasNonCanonicalValue = true;
            }

            return true;
        }
        catch (JsonException)
        {
            // Converted into a reason the caller records, never discarded.
            return false;
        }
    }

    private static bool ReadProperty(ref Utf8JsonReader reader, Span<byte> receivedMac, ref RawFields fields)
    {
        if (reader.ValueIsEscaped)
        {
            fields.HasNonCanonicalValue = true;
        }

        if (reader.ValueTextEquals("v"u8))
        {
            MarkSeen(ref fields.HasVersion, ref fields);
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int version) || version < 0)
            {
                fields.HasNonCanonicalValue = true;
                return SkipValue(ref reader);
            }

            fields.Version = version;
            return true;
        }

        if (reader.ValueTextEquals("pairId"u8))
        {
            MarkSeen(ref fields.HasPairId, ref fields);
            if (!TryReadIdText(ref reader, out ReadOnlySpan<char> text) || !PairId.TryParse(text, out PairId value))
            {
                fields.HasNonCanonicalValue = true;
                fields.HasPairId = false;
                return SkipValue(ref reader);
            }

            fields.PairId = value;
            return true;
        }

        if (reader.ValueTextEquals("machineId"u8))
        {
            MarkSeen(ref fields.HasSenderId, ref fields);

            // A roster entry, so MachineId.None is refused here: no machine is named by it.
            if (!TryReadIdText(ref reader, out ReadOnlySpan<char> text) ||
                !MachineId.TryParseRosterEntry(text, out MachineId value))
            {
                fields.HasNonCanonicalValue = true;
                return SkipValue(ref reader);
            }

            fields.SenderId = value;
            return true;
        }

        if (reader.ValueTextEquals("seq"u8))
        {
            MarkSeen(ref fields.HasSeq, ref fields);
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out ulong value))
            {
                fields.HasNonCanonicalValue = true;
                return SkipValue(ref reader);
            }

            fields.Seq = value;
            return true;
        }

        if (reader.ValueTextEquals("activeOwner"u8))
        {
            MarkSeen(ref fields.HasActiveOwner, ref fields);

            // An owner, so MachineId.None is accepted: it is the pre-claim value of §5.
            if (!TryReadIdText(ref reader, out ReadOnlySpan<char> text) ||
                !MachineId.TryParseOwner(text, out MachineId value))
            {
                fields.HasNonCanonicalValue = true;
                return SkipValue(ref reader);
            }

            fields.ActiveOwner = value;
            return true;
        }

        if (reader.ValueTextEquals("micLive"u8))
        {
            MarkSeen(ref fields.HasMicLive, ref fields);
            return TryReadBoolean(ref reader, ref fields, out fields.MicLive);
        }

        if (reader.ValueTextEquals("bye"u8))
        {
            MarkSeen(ref fields.HasBye, ref fields);
            return TryReadBoolean(ref reader, ref fields, out fields.Bye);
        }

        if (reader.ValueTextEquals("sentUtc"u8))
        {
            MarkSeen(ref fields.HasSentUtc, ref fields);
            if (!TryReadRawString(ref reader, TimestampLength, out ReadOnlySpan<byte> text) ||
                !TryParseTimestamp(text, out DateTimeOffset value))
            {
                fields.HasNonCanonicalValue = true;
                return SkipValue(ref reader);
            }

            fields.SentUtc = value;
            return true;
        }

        if (reader.ValueTextEquals("mac"u8))
        {
            MarkSeen(ref fields.HasMac, ref fields);
            if (!TryReadIdTextOfLength(ref reader, WireProtocol.MacHexLength, out ReadOnlySpan<char> text) ||
                !StrictHex.TryDecode(text, receivedMac))
            {
                fields.HasNonCanonicalValue = true;
                return SkipValue(ref reader);
            }

            return true;
        }

        fields.HasUnknownKey = true;
        return SkipValue(ref reader);
    }

    private static void MarkSeen(ref bool seen, ref RawFields fields)
    {
        if (seen)
        {
            fields.HasDuplicateKey = true;
        }

        seen = true;
    }

    private static bool SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.PropertyName && !reader.Read())
        {
            return false;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            return reader.TrySkip();
        }

        return true;
    }

    private static bool TryReadBoolean(ref Utf8JsonReader reader, ref RawFields fields, out bool value)
    {
        value = false;

        if (!reader.Read() || reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
        {
            fields.HasNonCanonicalValue = true;
            return SkipValue(ref reader);
        }

        value = reader.TokenType == JsonTokenType.True;
        return true;
    }

    private static bool TryReadRawString(ref Utf8JsonReader reader, int expectedLength, out ReadOnlySpan<byte> text)
    {
        text = default;

        if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped)
        {
            return false;
        }

        ReadOnlySpan<byte> value = reader.ValueSpan;
        if (value.Length != expectedLength)
        {
            return false;
        }

        text = value;
        return true;
    }

    private static bool TryReadIdText(ref Utf8JsonReader reader, out ReadOnlySpan<char> text) =>
        TryReadIdTextOfLength(ref reader, IdHexLength, out text);

    private static bool TryReadIdTextOfLength(ref Utf8JsonReader reader, int expectedLength, out ReadOnlySpan<char> text)
    {
        text = default;

        if (!TryReadRawString(ref reader, expectedLength, out ReadOnlySpan<byte> raw))
        {
            return false;
        }

        // Hex is ASCII by definition. Widening here keeps one strict-hex implementation -
        // the one in Identity - rather than growing a second set of rules for the wire.
        char[] characters = new char[expectedLength];
        for (int index = 0; index < expectedLength; index++)
        {
            byte value = raw[index];
            if (value > 0x7f)
            {
                return false;
            }

            characters[index] = (char)value;
        }

        text = characters;
        return true;
    }

    private static bool TryParseTimestamp(ReadOnlySpan<byte> text, out DateTimeOffset value)
    {
        value = default;

        if (text.Length != TimestampLength ||
            text[4] != (byte)'-' || text[7] != (byte)'-' || text[10] != (byte)'T' ||
            text[13] != (byte)':' || text[16] != (byte)':' || text[19] != (byte)'.' ||
            text[23] != (byte)'Z')
        {
            return false;
        }

        if (!TryReadDigits(text[..4], out int year) ||
            !TryReadDigits(text.Slice(5, 2), out int month) ||
            !TryReadDigits(text.Slice(8, 2), out int day) ||
            !TryReadDigits(text.Slice(11, 2), out int hour) ||
            !TryReadDigits(text.Slice(14, 2), out int minute) ||
            !TryReadDigits(text.Slice(17, 2), out int second) ||
            !TryReadDigits(text.Slice(20, 3), out int millisecond))
        {
            return false;
        }

        // Range-checked before construction, because the parser must never throw: it is the
        // first thing an unauthenticated broadcast reaches.
        if (year < 1 || year > 9999 ||
            month < 1 || month > 12 ||
            day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        value = new DateTimeOffset(
            new DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc));
        return true;
    }

    private static bool TryReadDigits(ReadOnlySpan<byte> text, out int value)
    {
        value = 0;

        foreach (byte character in text)
        {
            if (character is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            value = (value * 10) + (character - '0');
        }

        return true;
    }

    private static void WriteLiteral(ReadOnlySpan<byte> literal, Span<byte> destination, ref int offset)
    {
        literal.CopyTo(destination[offset..]);
        offset += literal.Length;
    }

    private static void WriteUnsigned(ulong value, Span<byte> destination, ref int offset)
    {
        if (value == 0)
        {
            destination[offset++] = (byte)'0';
            return;
        }

        Span<byte> digits = stackalloc byte[20];
        int count = 0;

        while (value > 0)
        {
            digits[count++] = (byte)('0' + (int)(value % 10));
            value /= 10;
        }

        for (int index = count - 1; index >= 0; index--)
        {
            destination[offset++] = digits[index];
        }
    }

    private static void WriteTimestamp(DateTimeOffset value, Span<byte> destination, ref int offset)
    {
        DateTime utc = value.UtcDateTime;

        WriteFixedDigits(utc.Year, 4, destination, ref offset);
        destination[offset++] = (byte)'-';
        WriteFixedDigits(utc.Month, 2, destination, ref offset);
        destination[offset++] = (byte)'-';
        WriteFixedDigits(utc.Day, 2, destination, ref offset);
        destination[offset++] = (byte)'T';
        WriteFixedDigits(utc.Hour, 2, destination, ref offset);
        destination[offset++] = (byte)':';
        WriteFixedDigits(utc.Minute, 2, destination, ref offset);
        destination[offset++] = (byte)':';
        WriteFixedDigits(utc.Second, 2, destination, ref offset);
        destination[offset++] = (byte)'.';
        WriteFixedDigits(utc.Millisecond, 3, destination, ref offset);
        destination[offset++] = (byte)'Z';
    }

    private static void WriteFixedDigits(int value, int digits, Span<byte> destination, ref int offset)
    {
        for (int position = digits - 1; position >= 0; position--)
        {
            destination[offset + position] = (byte)('0' + (value % 10));
            value /= 10;
        }

        offset += digits;
    }

    private static void WriteHexLower(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        Span<char> characters = stackalloc char[WireProtocol.MacHexLength];
        if (!Convert.TryToHexStringLower(source, characters, out int written) || written != source.Length * 2)
        {
            throw new InvalidOperationException("Hex formatting of the MAC did not produce 64 characters.");
        }

        for (int index = 0; index < written; index++)
        {
            destination[index] = (byte)characters[index];
        }
    }

    private struct RawFields
    {
        public bool HasUnknownKey;
        public bool HasDuplicateKey;
        public bool HasNonCanonicalValue;

        public bool HasVersion;
        public int Version;
        public bool HasPairId;
        public PairId PairId;
        public bool HasSenderId;
        public MachineId SenderId;
        public bool HasSeq;
        public ulong Seq;
        public bool HasActiveOwner;
        public MachineId ActiveOwner;
        public bool HasMicLive;
        public bool MicLive;
        public bool HasBye;
        public bool Bye;
        public bool HasSentUtc;
        public DateTimeOffset SentUtc;
        public bool HasMac;

        public readonly bool IsComplete =>
            HasVersion && HasPairId && HasSenderId && HasSeq &&
            HasActiveOwner && HasMicLive && HasBye && HasSentUtc && HasMac;
    }
}
