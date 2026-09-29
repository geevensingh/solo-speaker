using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SoloSpeaker.Core.Tests.WireFormat;

/// <summary>
/// Loads and writes <c>docs/wire-format.vectors.json</c>.
/// </summary>
/// <remarks>
/// The artifact is read from the repository rather than from a copy in the test output
/// directory. That is deliberate: the thing under assertion is the <em>committed</em>
/// normative file, and copying it would introduce a stale-copy failure mode in the one
/// suite whose entire job is to be hard to break by accident.
/// </remarks>
public static class WireVectorSource
{
    /// <summary>
    /// The artifact holds JSON documents inside JSON strings. Relaxed escaping keeps a
    /// canonical form readable as <c>{"v":1,...}</c> rather than a wall of <c>\u0022</c>,
    /// which is what makes a field-order change visible on review.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Loads the artifact, failing loudly rather than yielding an empty set.</summary>
    public static WireVectorFile Load() => Load(VectorPath());

    /// <summary>Loads from an explicit path, so the failure behaviour itself is testable.</summary>
    public static WireVectorFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The golden vector file is missing: {path}. Regenerate it by running this " +
                "suite with SOLO_SPEAKER_UPDATE_WIRE_VECTORS=1, then review the diff.");
        }

        WireVectorFile? file = JsonSerializer.Deserialize<WireVectorFile>(File.ReadAllText(path), Options);

        if (file is null || file.RoundTrip.Count == 0 || file.Ingress.Count == 0)
        {
            throw new InvalidOperationException(
                $"The golden vector file yielded no vectors: {path}. A suite that asserted " +
                "nothing would be green, which would remove the only enforcement behind the " +
                "design.md section 8 wire freeze.");
        }

        return file;
    }

    /// <summary>Writes the artifact, used only by the environment-gated regeneration path.</summary>
    public static void Write(WireVectorFile file)
    {
        string json = JsonSerializer.Serialize(file, Options);
        File.WriteAllText(VectorPath(), json.ReplaceLineEndings("\r\n") + "\r\n", new UTF8Encoding(false));
    }

    public static string VectorPath() =>
        Path.Combine(RepositoryRoot(), "docs", "wire-format.vectors.json");

    public static string MarkdownPath() =>
        Path.Combine(RepositoryRoot(), "docs", "wire-format.md");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SoloSpeaker.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException(
            $"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}
