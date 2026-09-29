using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink.Wire;
using SoloSpeaker.Core.Persistence;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.StateStore;

/// <summary>The outcome of reading a persisted file.</summary>
/// <param name="Cause">
/// <see cref="ErrorCause.None"/> when the read succeeded or the file was simply absent.
/// </param>
/// <param name="Found">Whether a file was present at all.</param>
public readonly record struct PersistedReadResult(ErrorCause Cause, bool Found)
{
    /// <summary>The file was read and every identity-bearing field was accepted.</summary>
    public static PersistedReadResult Loaded => new(ErrorCause.None, Found: true);

    /// <summary>No file. Ordinary before the pairing ceremony has run.</summary>
    public static PersistedReadResult Absent => new(ErrorCause.None, Found: false);

    /// <summary>A file was present and refused.</summary>
    public static PersistedReadResult Refused(ErrorCause cause) => new(cause, Found: true);

    /// <summary>Whether the caller may use the loaded values.</summary>
    public bool IsUsable => Found && Cause == ErrorCause.None;
}

/// <summary>
/// <c>state.json</c> over <see cref="IFileStore"/>, per <c>docs/design.md</c> §7.5.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="PairId"/> is injected rather than read from the configuration.
/// <see cref="SaveState"/> carries none and <see cref="StatePairId"/> is a read of what was
/// loaded - null on first run - yet every write must emit one or the next start has nothing
/// to cross-check. Injection keeps the two stores independent, puts the point where the two
/// files are tied together in the composition (which is how §7.5 frames the check anyway),
/// and answers "can a machine with no configuration write state?" with a structural no.
/// </para>
/// <para>
/// Policy lives here, in <c>net10.0</c>, over a thin I/O seam - review finding B-4's
/// remediation. <see cref="IFileStore"/> supplies bytes and knows nothing about what they
/// mean.
/// </para>
/// </remarks>
public sealed class JsonStateStore : IStateStore
{
    /// <summary>
    /// The largest <c>seq</c> that may be read back from disk.
    /// </summary>
    /// <remarks>
    /// §5.4 bounds <c>seq</c> in the datagram because one hostile value "poisons the
    /// persisted state on both machines". Enforcing that on the wire and not on the file
    /// leaves the same poisoning reachable through a hand-edit, a restore, or a half-written
    /// file. The bound is the point at which a further claim could no longer advance the
    /// clock - see <c>Reducer.Advance</c>, which saturates rather than wrapping.
    /// </remarks>
    public static ulong MaxPersistedSeq => ulong.MaxValue - WireProtocol.MaxSeqDelta;

    private readonly IFileStore _files;
    private readonly string _path;
    private readonly PairId _pairId;

    /// <summary>Creates a store over one file, bound to one pairing.</summary>
    public JsonStateStore(IFileStore files, string path, PairId pairId)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _files = files;
        _path = path;
        _pairId = pairId;
    }

    /// <inheritdoc/>
    public PairId? StatePairId { get; private set; }

    /// <summary>The last read's outcome, for the caller that turns it into a tray state.</summary>
    public PersistedReadResult LastRead { get; private set; } = PersistedReadResult.Absent;

    /// <inheritdoc/>
    public void SaveState(MachineId activeOwner, ulong seq)
    {
        var document = new StateDocument
        {
            Schema = JsonPersistence.CurrentSchema,
            PairId = _pairId.ToString(),
            ActiveOwner = activeOwner.ToString(),
            Seq = seq,
        };

        _files.WriteAtomic(_path, JsonPersistence.Serialize(document));
        StatePairId = _pairId;
    }

    /// <inheritdoc/>
    public bool TryLoadState(out MachineId activeOwner, out ulong seq)
    {
        activeOwner = MachineId.None;
        seq = 0;
        StatePairId = null;

        // An interrupted write leaves a temp file beside the real one. §4.5 asks that the
        // previous state be intact, not that an error be raised: the main file is whole, and
        // the ledger is what covers whatever mute that crash may have stranded.
        if (_files.PendingWriteExists(_path))
        {
            _files.DiscardPendingWrite(_path);
        }

        byte[]? contents = _files.Read(_path);

        if (contents is null)
        {
            LastRead = PersistedReadResult.Absent;
            return false;
        }

        StateDocument? document = JsonPersistence.TryDeserialize<StateDocument>(contents);

        if (document is null)
        {
            LastRead = PersistedReadResult.Refused(ErrorCause.StateUnreadable);
            return false;
        }

        if (document.Schema != JsonPersistence.CurrentSchema)
        {
            LastRead = PersistedReadResult.Refused(ErrorCause.PersistedSchemaUnknown);
            return false;
        }

        if (!PairId.TryParse(document.PairId, out PairId storedPairId) || storedPairId.IsZero)
        {
            LastRead = PersistedReadResult.Refused(ErrorCause.StateUnreadable);
            return false;
        }

        if (!MachineId.TryParseOwner(document.ActiveOwner, out MachineId storedOwner))
        {
            LastRead = PersistedReadResult.Refused(ErrorCause.StateUnreadable);
            return false;
        }

        if (document.Seq > MaxPersistedSeq)
        {
            LastRead = PersistedReadResult.Refused(ErrorCause.SeqBoundExceeded);
            return false;
        }

        StatePairId = storedPairId;
        activeOwner = storedOwner;
        seq = document.Seq;
        LastRead = PersistedReadResult.Loaded;
        return true;
    }
}
