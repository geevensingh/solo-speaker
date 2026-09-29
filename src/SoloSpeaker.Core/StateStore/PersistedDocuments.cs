namespace SoloSpeaker.Core.StateStore;

/// <summary>
/// The on-disk shape of <c>state.json</c>, per <c>docs/design.md</c> §7.5.
/// </summary>
/// <remarks>
/// Frozen by §8 in phase 1. Every field is identity-bearing - there are no tunables here -
/// so a bad value refuses the whole document.
/// </remarks>
public sealed class StateDocument
{
    /// <summary>Which shape this file is in. An unrecognised higher value is refused, never migrated.</summary>
    public int Schema { get; set; }

    /// <summary>The pairing this state belongs to, for §7.5's cross-file check.</summary>
    public string? PairId { get; set; }

    /// <summary>A roster ID, or thirty-two zeros before the first claim.</summary>
    public string? ActiveOwner { get; set; }

    /// <summary>The Lamport clock. Bounded on read - see <see cref="StateStore"/>.</summary>
    public ulong Seq { get; set; }
}

/// <summary>
/// The on-disk shape of <c>config.json</c>, per <c>docs/design.md</c> §7.5.
/// </summary>
/// <remarks>
/// <para>
/// Every field §7.5 names is modelled, including the four whose consumers arrive at later
/// work items. If only the currently-needed fields were parsed, then any rewrite - roster
/// completion, most obviously - would <b>silently delete the user's port and hotkey</b>.
/// That is a data-loss defect reachable by the ordinary pairing path.
/// </para>
/// <para>
/// The fields split by what they gate. <see cref="PairId"/>, <see cref="PairKeyProtected"/>
/// and <see cref="Roster"/> are identity-bearing and refuse the document when malformed;
/// <see cref="Port"/>, <see cref="Hotkey"/>, <see cref="Denylist"/> and
/// <see cref="DebounceSeconds"/> are tunables and fall back to their defaults.
/// </para>
/// </remarks>
public sealed class ConfigDocument
{
    /// <inheritdoc cref="StateDocument.Schema"/>
    public int Schema { get; set; }

    /// <summary>The pairing GUID. Identity-bearing.</summary>
    public string? PairId { get; set; }

    /// <summary>Base64 of the DPAPI-protected <c>pairKey</c>, per ADR 0013. Identity-bearing.</summary>
    public string? PairKeyProtected { get; set; }

    /// <summary>One or two roster IDs, self first. Identity-bearing.</summary>
    public IReadOnlyList<string>? Roster { get; set; }

    /// <summary>The UDP port of §7.1. Tunable.</summary>
    public int? Port { get; set; }

    /// <summary>The global hotkey of §7.4. Tunable.</summary>
    public string? Hotkey { get; set; }

    /// <summary>Process names excluded from mic detection, per §7.2. Tunable; phase 2.</summary>
    public IReadOnlyList<string>? Denylist { get; set; }

    /// <summary>
    /// The §7.2 claim debounce. Named for its unit because under §8 the persisted key is
    /// itself the frozen artifact.
    /// </summary>
    public int? DebounceSeconds { get; set; }
}

/// <summary>The documented fallbacks for <see cref="ConfigDocument"/>'s tunables.</summary>
/// <remarks>
/// They live here rather than in prose so that the value a bad field falls back to and the
/// value a fresh pairing writes are the same one.
/// </remarks>
public static class ConfigDefaults
{
    /// <summary>§7.1's default broadcast port.</summary>
    public const int Port = 48292;

    /// <summary>§7.4's default global hotkey.</summary>
    public const string Hotkey = "Ctrl+Alt+Shift+M";

    /// <summary>§7.2's default claim debounce.</summary>
    public const int DebounceSeconds = 5;
}
