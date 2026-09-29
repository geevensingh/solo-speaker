using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.Tests.Fakes;

/// <summary>A monotonic clock the test drives by hand.</summary>
/// <remarks>
/// One instance drives both nodes in the two-node harness. That is sound rather than a
/// simplification: the two machines never compare clocks. §7.1 makes <c>sentUtc</c>
/// "informational and logged, but not a drop condition", and every window the reducer
/// evaluates compares <c>now</c> to a timestamp the same node wrote. A shared monotonic base
/// is therefore indistinguishable from two independent ones - which also means the harness
/// demonstrates nothing about clock skew, and `implementation-plan.md` §4.1 says so.
/// </remarks>
internal sealed class FakeClock : IClock
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 25, 21, 0, 0, TimeSpan.Zero);

    public TimeSpan Elapsed { get; private set; }

    public DateTimeOffset UtcNow => Epoch + Elapsed;

    internal void Advance(TimeSpan by) => Elapsed += by;
}

/// <summary>
/// An in-memory <see cref="IStateStore"/> that round-trips through the same canonical
/// rendering the real store will write.
/// </summary>
/// <remarks>
/// The round trip is the point. Holding a <see cref="MachineId"/> struct directly would make
/// the lid-open test prove "the reducer behaves correctly given a hypothetical persisted
/// state" rather than "the node persisted that state and read it back", and it would never
/// exercise <see cref="IStateStore.TryLoadState"/>'s documented rejection of a stored owner
/// that fails the strict canonical parse. Work item 5 swaps <c>IFileStore</c> underneath and
/// this assertion survives unchanged.
/// </remarks>
internal sealed class FakeStateStore : IStateStore
{
    private string? _activeOwnerHex;
    private ulong _seq;

    internal int SaveCount { get; private set; }

    /// <summary>Set to make the next write fail, for the §7.5 persist-failure path.</summary>
    internal bool FailNextWrite { get; set; }

    public PairId? StatePairId { get; internal set; }

    public void SaveState(MachineId activeOwner, ulong seq)
    {
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("Simulated write failure.");
        }

        _activeOwnerHex = activeOwner.ToString();
        _seq = seq;
        SaveCount++;
    }

    public bool TryLoadState(out MachineId activeOwner, out ulong seq)
    {
        activeOwner = MachineId.None;
        seq = 0;

        if (_activeOwnerHex is null)
        {
            return false;
        }

        if (!MachineId.TryParseOwner(_activeOwnerHex, out activeOwner))
        {
            return false;
        }

        seq = _seq;
        return true;
    }
}

/// <summary>An in-memory <see cref="IConfigStore"/> over a fixed pairing.</summary>
internal sealed class FakeConfigStore : IConfigStore
{
    private readonly byte[] _pairKey;

    internal FakeConfigStore(PairId pairId, Roster roster, byte[] pairKey)
    {
        PairId = pairId;
        Roster = roster;
        _pairKey = pairKey;
    }

    public Roster Roster { get; private set; }

    public PairId PairId { get; }

    public bool IsPaired => Roster.IsComplete;

    /// <summary>Set to simulate a config that cannot be decrypted on this profile.</summary>
    internal bool PairKeyUnavailable { get; set; }

    public bool TryGetPairKey(out byte[] pairKey)
    {
        pairKey = PairKeyUnavailable ? [] : _pairKey;
        return !PairKeyUnavailable;
    }

    public bool MatchesState(PairId statePairId) => statePairId == PairId;

    public bool TryCompleteRoster(MachineId peerId)
    {
        if (Roster.IsComplete || !Roster.TryCreate(Roster.Self, peerId, out Roster? completed))
        {
            return false;
        }

        Roster = completed!;
        return true;
    }
}
