using System.Text.Json.Serialization;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Persistence;

namespace SoloSpeaker.Core.Ledger;

/// <summary>
/// The §7.3 mutation ledger over <see cref="IFileStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// In <c>SoloSpeaker.Core</c> rather than <c>SoloSpeaker.App</c> because
/// <c>SoloSpeaker.Core.Tests</c> targets <c>net10.0</c> and cannot reference a
/// Windows-targeted assembly. Review finding B-4 is the whole of that argument: an earlier
/// design put the ledger behind the audio interface, where none of its failure modes could
/// be reached by a test.
/// </para>
/// <para>
/// The document is an <b>array</b> of entries plus a schema, where §7.3 originally sketched
/// a single object. A default-device change legitimately holds two entries for a moment -
/// the old endpoint is released before the new one is acquired - so one slot cannot express
/// the state the swap passes through. No shipped build has ever written a ledger, so there
/// is no old shape to migrate.
/// </para>
/// <para>
/// One asymmetry against the other two persisted files, worth knowing when reading the
/// refusal path below: for <c>state.json</c> and <c>config.json</c>, refusing a malformed
/// document is the Goal 1 direction, because a refused file leaves the machine audible. A
/// refused <c>ledger.json</c> leaves a stranded mute <em>unrepaired</em>. It still refuses -
/// acting on a half-understood recovery record is worse than declining to - which is why
/// §4.5's corrupt-ledger row also demands that nothing be muted in that state.
/// </para>
/// </remarks>
public sealed class JsonLedger : ILedger
{
    private readonly IFileStore _files;
    private readonly string _path;
    private readonly IClock _clock;
    private readonly Action<string, string, string>? _record;

    private List<LedgerEntry> _entries = [];
    private bool _loaded;
    private bool _corrupt;

    /// <summary>Creates a ledger over one file.</summary>
    /// <param name="files">Raw file I/O.</param>
    /// <param name="path">Where the ledger lives.</param>
    /// <param name="clock">Supplies the recorded timestamp.</param>
    /// <param name="record">
    /// Optional observer for ADR 0015's "ledger writes, replays, and clears". A callback
    /// rather than an <c>ILogSink</c> so that Core's recovery record does not depend on the
    /// diagnostic that watches it, and so the 281 tests that construct this keep working.
    /// </param>
    public JsonLedger(IFileStore files, string path, IClock clock, Action<string, string, string>? record = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(clock);

        _files = files;
        _path = path;
        _clock = clock;
        _record = record;
    }

    /// <summary>Endpoints this ledger currently records a mutation for.</summary>
    public IReadOnlyList<string> RecordedEndpoints
    {
        get
        {
            Load();
            return [.. _entries.Select(entry => entry.EndpointId)];
        }
    }

    /// <summary>Whether the ledger on disk could not be read or parsed.</summary>
    public bool IsCorrupt
    {
        get
        {
            Load();
            return _corrupt;
        }
    }

    /// <inheritdoc/>
    public void RecordIntent(string endpointId, bool priorMute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        Load();

        // Re-recording an endpoint keeps the ORIGINAL priorMute. The first record is the
        // only one taken before this app touched the endpoint; a later one would capture a
        // value we ourselves wrote, so the restore would restore our own mute.
        if (_entries.Any(entry => Matches(entry, endpointId)))
        {
            return;
        }

        _entries.Add(new LedgerEntry
        {
            EndpointId = endpointId,
            PriorMute = priorMute,
            MutedAtUtc = _clock.UtcNow,
        });

        Save();
        _record?.Invoke("intent", endpointId, $"priorMute={priorMute}");
    }

    /// <inheritdoc/>
    public void Clear(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        Load();

        if (_entries.RemoveAll(entry => Matches(entry, endpointId)) > 0)
        {
            Save();
            _record?.Invoke("clear", endpointId, string.Empty);
        }
    }

    /// <inheritdoc/>
    public LedgerReplayResult Replay(IMuteActuator actuator)
    {
        ArgumentNullException.ThrowIfNull(actuator);

        Load();

        if (_corrupt)
        {
            // §4.5: error raised, app still starts, nothing is muted. The file is left alone
            // rather than deleted - it is the only record of what may still be muted, and a
            // human may yet read it.
            _record?.Invoke("replay", string.Empty, "refused: unreadable");
            return new LedgerReplayResult(0, 0, 0, Failed: true);
        }

        int restored = 0;
        int skipped = 0;
        var unrepaired = new List<LedgerEntry>();

        foreach (LedgerEntry entry in _entries)
        {
            if (entry.PriorMute)
            {
                // Design revision 11: replay only ever unmutes. Restoring this faithfully
                // would mean muting an endpoint from the repair path, so --restore could
                // silence a machine, report success, and have uninstall.ps1 delete the
                // binary and this file behind it. §7.6 lists replay as an unmute path.
                skipped++;
                continue;
            }

            switch (actuator.TrySetMute(entry.EndpointId, muted: false))
            {
                case MuteApplyOutcome.Applied:
                    restored++;
                    break;

                case MuteApplyOutcome.EndpointGone:
                    skipped++;
                    break;

                default:
                    // Retained deliberately. The endpoint exists and may still be muted, so
                    // discarding the entry would discard the only record of it.
                    unrepaired.Add(entry);
                    break;
            }
        }

        _entries = unrepaired;
        Save();

        _record?.Invoke(
            "replay",
            string.Empty,
            $"restored={restored} skipped={skipped} unrepaired={unrepaired.Count}");

        return new LedgerReplayResult(restored, skipped, unrepaired.Count, Failed: false);
    }

    private static bool Matches(LedgerEntry entry, string endpointId) =>
        string.Equals(entry.EndpointId, endpointId, StringComparison.Ordinal);

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        byte[]? contents = _files.Read(_path);

        if (contents is null)
        {
            // An absent ledger is the ordinary case on a machine that has never muted.
            return;
        }

        LedgerDocument? document = JsonPersistence.TryDeserialize<LedgerDocument>(contents);

        if (document is null || document.Schema != JsonPersistence.CurrentSchema || document.Entries is null)
        {
            _corrupt = true;
            return;
        }

        foreach (LedgerEntry entry in document.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.EndpointId))
            {
                // One unusable entry condemns the document. A partly readable ledger cannot
                // be acted on safely, because the unreadable part may name the endpoint that
                // is actually muted.
                _corrupt = true;
                _entries.Clear();
                return;
            }

            _entries.Add(entry);
        }
    }

    private void Save()
    {
        if (_entries.Count == 0)
        {
            _files.Delete(_path);
            return;
        }

        var document = new LedgerDocument
        {
            Schema = JsonPersistence.CurrentSchema,
            Entries = _entries,
        };

        _files.WriteAtomic(_path, JsonPersistence.Serialize(document));
    }
}

/// <summary>The on-disk ledger document.</summary>
internal sealed class LedgerDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; }

    [JsonPropertyName("entries")]
    public List<LedgerEntry>? Entries { get; set; }
}

/// <summary>One recorded mutation, in the shape §7.3 specifies.</summary>
public sealed class LedgerEntry
{
    /// <summary>The endpoint this app mutated.</summary>
    [JsonPropertyName("endpointId")]
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>The endpoint's mute state before this app touched it.</summary>
    [JsonPropertyName("priorMute")]
    public bool PriorMute { get; set; }

    /// <summary>When the mutation was recorded. Diagnostic only; nothing compares it.</summary>
    [JsonPropertyName("mutedAtUtc")]
    public DateTimeOffset MutedAtUtc { get; set; }
}
