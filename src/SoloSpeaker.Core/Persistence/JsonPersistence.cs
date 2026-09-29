using System.Text;
using System.Text.Json;

namespace SoloSpeaker.Core.Persistence;

/// <summary>
/// The JSON conventions every persisted file shares.
/// </summary>
/// <remarks>
/// The one genuinely cross-component piece of persistence. <c>StateStore</c> and
/// <c>ConfigStore</c> use it today; work item 7's <c>Ledger</c> is its third consumer, which
/// is why it lives here rather than beside either of them.
/// </remarks>
public static class JsonPersistence
{
    /// <summary>The schema version every persisted file this build writes carries.</summary>
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>
    /// Deserializes a persisted document, converting malformed input into
    /// <see langword="null"/> rather than an exception.
    /// </summary>
    /// <remarks>
    /// Persisted files are external input by <c>AGENTS.md</c> §4's definition, so the
    /// parser must reject rather than throw. The exception is converted into a result the
    /// caller turns into an error cause, never swallowed.
    /// </remarks>
    public static TDocument? TryDeserialize<TDocument>(ReadOnlySpan<byte> utf8)
        where TDocument : class
    {
        try
        {
            return JsonSerializer.Deserialize<TDocument>(utf8, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Serializes a persisted document, with a trailing newline.</summary>
    public static byte[] Serialize<TDocument>(TDocument document) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, Options) + Environment.NewLine);
}
