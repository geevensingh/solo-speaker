using System.Collections.Immutable;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.Tests.StateMachine;

/// <summary>
/// Drives the reducer against a controlled monotonic clock.
/// </summary>
/// <remarks>
/// No socket, no disk, no device, and no real time - <c>docs/implementation-plan.md</c>
/// §4.1's claim is that the whole arbitration model is provable this way, and this is the
/// only machinery those tests need.
/// </remarks>
internal sealed class ReducerHarness
{
    /// <summary>Machine A. Lexicographically the <em>larger</em> roster ID, so B wins §5.4's tiebreak.</summary>
    internal const string AHex = "7f3a9c1e2d4b6a8035179246ab13cd5e";

    /// <summary>Machine B.</summary>
    internal const string BHex = "2d81e407fa63b95c18204e7dc6395fa1";

    /// <summary>A machine on neither roster.</summary>
    internal const string StrangerHex = "aa00bb11cc22dd33ee44ff5566778899";

    private readonly ArbitrationContext _context;

    private ReducerHarness(Roster roster, ArbitrationTunables tunables)
    {
        _context = ArbitrationContext.ForEvent(roster, tunables);
        State = ArbitrationState.Fresh();
    }

    internal ArbitrationState State { get; private set; }

    internal TimeSpan Now { get; private set; }

    internal ImmutableArray<ArbitrationEffect> Effects { get; private set; } = [];

    internal bool ShouldMute { get; private set; }

    internal TrayState Tray { get; private set; }

    internal ErrorCause Error { get; private set; }

    internal MachineId Self => _context.Roster.Self;

    internal MachineId Peer => _context.Roster.Peer!.Value;

    internal ArbitrationTunables Tunables => _context.Tunables;

    /// <summary>A complete roster, this machine first.</summary>
    internal static ReducerHarness For(
        string selfHex = AHex,
        string? peerHex = BHex,
        ArbitrationTunables? tunables = null)
    {
        MachineId? peer = peerHex is null ? null : Id(peerHex);

        if (!Roster.TryCreate(Id(selfHex), peer, out Roster? roster))
        {
            throw new InvalidOperationException("Test roster is invalid.");
        }

        return new ReducerHarness(roster!, tunables ?? ArbitrationTunables.Default);
    }

    internal static MachineId Id(string hex)
    {
        if (!MachineId.TryParseOwner(hex, out MachineId id))
        {
            throw new InvalidOperationException($"Test identifier '{hex}' is not canonical.");
        }

        return id;
    }

    /// <summary>Starts the reducer from persisted state rather than from a fresh install.</summary>
    internal ReducerHarness Persisted(MachineId activeOwner, ulong seq)
    {
        State = ArbitrationState.FromPersisted(activeOwner, seq);
        return this;
    }

    /// <summary>Advances the monotonic clock without raising an event.</summary>
    internal ReducerHarness Advance(TimeSpan by)
    {
        Now += by;
        return this;
    }

    /// <summary>Reduces one event at the current time.</summary>
    internal ReducerHarness Apply(ArbitrationEvent arbitrationEvent)
    {
        ReducerResult result = Reducer.Reduce(_context, State, arbitrationEvent, Now);

        State = result.State;
        Effects = result.Effects;
        ShouldMute = result.ShouldMute;
        Tray = result.TrayState;
        Error = result.ErrorCause;

        return this;
    }

    /// <summary>Advances the clock, then reduces.</summary>
    internal ReducerHarness Apply(TimeSpan after, ArbitrationEvent arbitrationEvent) =>
        Advance(after).Apply(arbitrationEvent);

    internal ReducerHarness Claim() => Apply(new ArbitrationEvent.ManualClaim(ClaimSource.Hotkey));

    internal ReducerHarness MicEdge() => Apply(new ArbitrationEvent.MicRisingEdge());

    internal ReducerHarness Tick() => Apply(new ArbitrationEvent.Tick());

    internal ReducerHarness PeerState(MachineId owner, ulong seq, bool micLive = false) =>
        Apply(new ArbitrationEvent.PeerStateReceived(owner, seq, micLive));

    internal ReducerHarness PeerBye() => Apply(new ArbitrationEvent.PeerDeparted());

    internal ReducerHarness EnterQuarantine() => Apply(new ArbitrationEvent.QuarantineEntered());

    internal ReducerHarness RestartQuarantine() => Apply(new ArbitrationEvent.QuarantineRestarted());

    /// <summary>The broadcasts emitted by the most recent reduction.</summary>
    internal ImmutableArray<ArbitrationEffect.Broadcast> Broadcasts =>
        [.. Effects.OfType<ArbitrationEffect.Broadcast>()];

    /// <summary>The persists emitted by the most recent reduction.</summary>
    internal ImmutableArray<ArbitrationEffect.PersistState> Persists =>
        [.. Effects.OfType<ArbitrationEffect.PersistState>()];

    internal bool PeerPresent => State.IsPeerPresent(Now, Tunables.PresenceWindow);
}
