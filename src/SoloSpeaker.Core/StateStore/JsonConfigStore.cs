using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.Persistence;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.StateStore;

/// <summary>
/// <c>config.json</c> over <see cref="IFileStore"/> and <see cref="ISecretProtector"/>, per
/// <c>docs/design.md</c> §7.5 and ADR 0013.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only writer of <c>config.json</c>.</b> ADR 0011's <c>--pair-init</c> creates a
/// configuration from nothing, so work item 10 would otherwise have authored a second write
/// path into a §8-frozen shape, with its own field set, its own defaults, and the only call
/// to <see cref="ISecretProtector.Protect"/>. <see cref="Create"/> is that path; work item
/// 10 supplies the inputs and drives the ceremony.
/// </para>
/// <para>
/// The decrypted <c>pairKey</c> is held once for the store's lifetime rather than decrypted
/// per call. <c>ArbitrationLoop</c> needs it for every inbound datagram and every broadcast,
/// so a per-call round trip would put a DPAPI decrypt on the path of unauthenticated
/// broadcast traffic.
/// </para>
/// </remarks>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly byte[] _pairKey;

    private JsonConfigStore(
        IFileStore files,
        string path,
        ISecretProtector protector,
        PairId pairId,
        Roster roster,
        byte[] pairKey,
        ConfigDocument document)
    {
        Files = files;
        Path = path;
        Protector = protector;
        PairId = pairId;
        Roster = roster;
        _pairKey = pairKey;
        Document = document;
        Port = document.Port ?? ConfigDefaults.Port;
        Hotkey = document.Hotkey ?? ConfigDefaults.Hotkey;
    }

    /// <inheritdoc/>
    public Roster Roster { get; private set; }

    /// <inheritdoc/>
    public PairId PairId { get; }

    /// <inheritdoc/>
    public bool IsPaired => Roster.IsComplete;

    /// <inheritdoc/>
    public int Port { get; }

    /// <inheritdoc/>
    public string Hotkey { get; }

    /// <summary>
    /// A tunable that failed validation and fell back to its default, or
    /// <see cref="ErrorCause.None"/>.
    /// </summary>
    public ErrorCause TunableFault { get; private init; }

    /// <summary>The field the fallback applied to, for §7.4's tooltip.</summary>
    public string? FaultedField { get; private init; }

    private IFileStore Files { get; }

    private string Path { get; }

    private ISecretProtector Protector { get; }

    private ConfigDocument Document { get; set; }

    /// <summary>
    /// Reads and validates <c>config.json</c>.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="false"/> for an absent file is what lets
    /// <see cref="IConfigStore.Roster"/> stay non-nullable: before the pairing ceremony
    /// there is no <c>Self</c> and therefore no roster, and the host simply does not build
    /// an arbitration loop. A nullable roster would have pushed an "absent" sentinel into
    /// the right-hand side of §5.5's predicate, which is the hazard
    /// <see cref="Identity.Roster"/>'s private constructor exists to prevent.
    /// </remarks>
    public static PersistedReadResult TryLoad(
        IFileStore files,
        string path,
        ISecretProtector protector,
        out JsonConfigStore? store)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        store = null;

        if (files.PendingWriteExists(path))
        {
            files.DiscardPendingWrite(path);
        }

        byte[]? contents = files.Read(path);

        if (contents is null)
        {
            return PersistedReadResult.Absent;
        }

        ConfigDocument? document = JsonPersistence.TryDeserialize<ConfigDocument>(contents);

        if (document is null)
        {
            return PersistedReadResult.Refused(ErrorCause.ConfigUnreadable);
        }

        if (document.Schema != JsonPersistence.CurrentSchema)
        {
            return PersistedReadResult.Refused(ErrorCause.PersistedSchemaUnknown);
        }

        // Identity-bearing fields refuse the document. They are the right-hand side of
        // §5.5's predicate, and coercing one is how the invariant breaks.
        if (!PairId.TryParse(document.PairId, out PairId pairId) || pairId.IsZero)
        {
            return PersistedReadResult.Refused(ErrorCause.ConfigUnreadable);
        }

        if (!TryReadRoster(document.Roster, out Roster? roster))
        {
            return PersistedReadResult.Refused(ErrorCause.ConfigUnreadable);
        }

        if (document.PairKeyProtected is not { } protectedKey ||
            !TryDecodeBase64(protectedKey, out byte[] cipherText))
        {
            return PersistedReadResult.Refused(ErrorCause.ConfigUnreadable);
        }

        byte[] entropy = new byte[16];
        pairId.WriteTo(entropy);

        if (!protector.TryUnprotect(cipherText, entropy, out byte[] pairKey))
        {
            return PersistedReadResult.Refused(ErrorCause.ConfigUnreadable);
        }

        if (!IsStructurallySound(pairKey))
        {
            return PersistedReadResult.Refused(ErrorCause.PairKeyRefused);
        }

        // Tunables fall back rather than refusing, and name themselves. Goal 8 invites hand
        // editing, and taking the pair dark over a mistyped denylist entry would be a worse
        // outcome than the deletion the explicit model exists to prevent.
        (ErrorCause tunableFault, string? faultedField) = ValidateTunables(document);

        store = new JsonConfigStore(files, path, protector, pairId, roster!, pairKey, document)
        {
            TunableFault = tunableFault,
            FaultedField = faultedField,
        };

        return PersistedReadResult.Loaded;
    }

    /// <summary>
    /// Writes a fresh <c>config.json</c> for the pairing ceremony of §7.7 and ADR 0011.
    /// </summary>
    public static void Create(
        IFileStore files,
        string path,
        ISecretProtector protector,
        PairId pairId,
        ReadOnlySpan<byte> pairKey,
        Roster roster)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(roster);

        Span<byte> entropy = stackalloc byte[16];
        pairId.WriteTo(entropy);

        var document = new ConfigDocument
        {
            Schema = JsonPersistence.CurrentSchema,
            PairId = pairId.ToString(),
            PairKeyProtected = Convert.ToBase64String(protector.Protect(pairKey, entropy)),
            Roster = RosterFields(roster),
            Port = ConfigDefaults.Port,
            Hotkey = ConfigDefaults.Hotkey,
            Denylist = [],
            DebounceSeconds = ConfigDefaults.DebounceSeconds,
        };

        files.WriteAtomic(path, JsonPersistence.Serialize(document));
    }

    /// <inheritdoc/>
    public bool TryGetPairKey(out byte[] pairKey)
    {
        pairKey = _pairKey;
        return true;
    }

    /// <inheritdoc/>
    public bool MatchesState(PairId statePairId) => statePairId == PairId;

    /// <inheritdoc/>
    public bool TryCompleteRoster(MachineId peerId)
    {
        if (Roster.IsComplete || !Roster.TryCreate(Roster.Self, peerId, out Roster? completed))
        {
            return false;
        }

        // Every field is rewritten from the document we parsed, so the tunables survive a
        // roster completion rather than being silently dropped.
        Document = new ConfigDocument
        {
            Schema = JsonPersistence.CurrentSchema,
            PairId = Document.PairId,
            PairKeyProtected = Document.PairKeyProtected,
            Roster = RosterFields(completed!),
            Port = Document.Port,
            Hotkey = Document.Hotkey,
            Denylist = Document.Denylist,
            DebounceSeconds = Document.DebounceSeconds,
        };

        Files.WriteAtomic(Path, JsonPersistence.Serialize(Document));
        Roster = completed!;
        return true;
    }

    private static IReadOnlyList<string> RosterFields(Roster roster) =>
        roster.Peer is { } peer
            ? [roster.Self.ToString(), peer.ToString()]
            : [roster.Self.ToString()];

    private static bool TryReadRoster(IReadOnlyList<string>? entries, out Roster? roster)
    {
        roster = null;

        if (entries is null || entries.Count is 0 or > 2)
        {
            return false;
        }

        if (!MachineId.TryParseRosterEntry(entries[0], out MachineId self))
        {
            return false;
        }

        MachineId? peer = null;

        if (entries.Count == 2)
        {
            if (!MachineId.TryParseRosterEntry(entries[1], out MachineId parsedPeer))
            {
                return false;
            }

            peer = parsedPeer;
        }

        return Roster.TryCreate(self, peer, out roster);
    }

    /// <remarks>
    /// Structural rejections only - the wrong length, or every byte identical, which
    /// subsumes all-zero. Deliberately <em>not</em> a denylist of known values: the
    /// published vector key is the one the two-node harness signs with, so denying it here
    /// would make work item 4's own restart scenario impossible. The published-key hazard is
    /// closed where it is reachable, by <c>--pair-init</c> refusing to emit it.
    /// </remarks>
    private static bool IsStructurallySound(byte[] pairKey)
    {
        if (pairKey.Length != 32)
        {
            return false;
        }

        foreach (byte value in pairKey)
        {
            if (value != pairKey[0])
            {
                return true;
            }
        }

        return false;
    }

    private static (ErrorCause Cause, string? Field) ValidateTunables(ConfigDocument document)
    {
        if (document.Port is { } port && (port is < 1 or > 65535))
        {
            document.Port = null;
            return (ErrorCause.TunableFellBackToDefault, nameof(ConfigDocument.Port));
        }

        if (document.Hotkey is { Length: 0 })
        {
            document.Hotkey = null;
            return (ErrorCause.TunableFellBackToDefault, nameof(ConfigDocument.Hotkey));
        }

        if (document.DebounceSeconds is { } debounce && debounce < 0)
        {
            document.DebounceSeconds = null;
            return (ErrorCause.TunableFellBackToDefault, nameof(ConfigDocument.DebounceSeconds));
        }

        return (ErrorCause.None, null);
    }

    private static bool TryDecodeBase64(string text, out byte[] decoded)
    {
        decoded = [];
        byte[] buffer = new byte[((text.Length + 3) / 4) * 3];

        if (!Convert.TryFromBase64String(text, buffer, out int written) || written == 0)
        {
            return false;
        }

        decoded = buffer[..written];
        return true;
    }
}
