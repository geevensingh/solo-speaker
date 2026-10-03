using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.StateStore;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// What a startup attempt produced.
/// </summary>
/// <param name="Config">
/// The loaded configuration, or <see langword="null"/> when the machine is not paired.
/// </param>
/// <param name="State">The arbitration state to start from.</param>
/// <param name="Cause">The cause to raise before the first tick, if any.</param>
public readonly record struct StartupResult(JsonConfigStore? Config, ArbitrationState State, ErrorCause Cause)
{
    /// <summary>
    /// Whether the host should build an arbitration loop at all.
    /// </summary>
    /// <remarks>
    /// An unpaired machine has no roster - <c>Roster</c> requires a <c>Self</c>, and before
    /// the ceremony there is none - so it has nothing to arbitrate and the host shows a
    /// first-run affordance instead. That is why <c>IConfigStore.Roster</c> stays
    /// non-nullable: the absent case is modelled by there being no configuration object,
    /// not by a null inside one.
    /// </remarks>
    public bool IsPaired => Config is not null;
}

/// <summary>
/// The ordered startup steps of <c>docs/design.md</c> §7.3, ADR 0012 and §7.5.
/// </summary>
/// <remarks>
/// <para>
/// The ordering lives here rather than in <c>SoloSpeaker.Core</c> because
/// <c>AGENTS.md</c> §3 assigns "process lifetime" and "the single-instance guard" to the
/// host - two of its three steps. Core owns the <em>decision</em>
/// (<see cref="StartupDecision"/>); this owns the sequence.
/// </para>
/// <para>
/// The steps are members rather than prose so that later work items fill a slot instead of
/// re-deriving the order from three documents. Two are no-ops today, and each names the
/// work item that fills it.
/// </para>
/// <para>
/// There is deliberately no <c>BindTransport</c> step. Work item 6 was the first to try to
/// fill a slot and found it could not: binding needs a port, which only exists after
/// <c>LoadPersisted</c>, and produces a <em>socket</em>, which <see cref="StartupResult"/>
/// cannot carry. Binding is not a persistence-ordering step, so the host owns it. Work items
/// 7 and 12 have the same resource-producing shape - a ledger and a mutex handle that must
/// outlive <see cref="Run"/> - and land in <c>SoloSpeakerHost</c>'s construction order for
/// the same reason.
/// </para>
/// </remarks>
public sealed class StartupSequence
{
    private readonly IFileStore _files;
    private readonly ISecretProtector _protector;
    private readonly IClock _clock;
    private readonly string _root;
    private readonly ILedger _ledger;
    private readonly IMuteActuator _actuator;

    /// <summary>Creates a sequence over one data root.</summary>
    public StartupSequence(
        IFileStore files,
        ISecretProtector protector,
        IClock clock,
        string root,
        ILedger ledger,
        IMuteActuator actuator)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(actuator);

        _files = files;
        _protector = protector;
        _clock = clock;
        _root = root;
        _ledger = ledger;
        _actuator = actuator;
    }

    /// <summary>Runs the steps in order.</summary>
    /// <param name="guard">
    /// The already-acquired single-instance guard. Acquisition happens in
    /// <c>Program</c> because the handle must outlive this call - ADR 0012 requires the
    /// guard to still be held while the app runs, and a <c>StartupResult</c> cannot carry it.
    /// </param>
    public StartupResult Run(SingleInstanceGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        ErrorCause replayCause = ReplayLedger();
        StartupResult loaded = LoadPersisted();

        // Behaviour-preserving: replay's two causes both rank MaySilence and the persisted
        // ones rank at most Impaired, so the rank decides every contested pair. The only
        // reachable tie is None/None, where the result is an equal record. The tie direction
        // is carried forward from the old expression, not argued - unlike EffectiveError,
        // neither operand here was ever raised, so §7.4's first-raised rule does not apply.
        // Matrix row E5 asserts the ledger cause specifically.
        return loaded with
        {
            Cause = ErrorCauseExtensions.Louder(preferred: replayCause, other: loaded.Cause),
        };
    }

    /// <summary>
    /// §7.3's "on every startup, before anything else" ledger replay - before anything this
    /// instance is entitled to do, which is why the guard precedes it.
    /// </summary>
    private ErrorCause ReplayLedger()
    {
        LedgerReplayResult result = _ledger.Replay(_actuator);

        if (result.Failed)
        {
            return ErrorCause.LedgerReplayFailed;
        }

        // Replay only ever unmutes - JsonLedger.Replay skips priorMute entries and calls
        // TrySetMute(muted: false) - so an unrepaired entry is always the Goal 1 direction.
        return result.Unrepaired > 0 ? ErrorCause.UnmuteWriteFailed : ErrorCause.None;
    }

    /// <summary>§7.5: read both files and apply the cross-file check. Work item 5.</summary>
    private StartupResult LoadPersisted()
    {
        string configPath = DataRoot.PathFor(_root, PersistedFiles.Config);
        string statePath = DataRoot.PathFor(_root, PersistedFiles.State);

        PersistedReadResult configRead = JsonConfigStore.TryLoad(_files, configPath, _protector, out JsonConfigStore? config);

        if (configRead.Found && !configRead.IsUsable)
        {
            return new StartupResult(null, ArbitrationState.Fresh(), configRead.Cause);
        }

        if (config is null)
        {
            // Not paired. Nothing to arbitrate, so no loop is built - but a stale state file
            // is still worth reporting rather than leaving on disk unexplained.
            var unpaired = new JsonStateStore(_files, statePath, default);
            unpaired.TryLoadState(out _, out _);

            StartupOutcome outcome = StartupDecision.Decide(
                null, unpaired.LastRead, unpaired.StatePairId, MachineId.None, 0, _clock.Elapsed);

            return new StartupResult(null, outcome.State, outcome.Cause);
        }

        var JsonStateStore = new JsonStateStore(_files, statePath, config.PairId);
        JsonStateStore.TryLoadState(out MachineId activeOwner, out ulong seq);

        StartupOutcome decided = StartupDecision.Decide(
            config.PairId, JsonStateStore.LastRead, JsonStateStore.StatePairId, activeOwner, seq, _clock.Elapsed);

        // A tunable that fell back names itself, but only when nothing louder is pending.
        // decided.Cause is preferred on a tie because that reproduces the old expression;
        // the tie is reachable (a refused state.json beside a usable config.json with an
        // out-of-range port) and both operands rank Impaired, so the rank does not decide
        // it. Not argued from §7.4 - neither operand has been raised at this point.
        ErrorCause cause = ErrorCauseExtensions.Louder(
            preferred: decided.Cause,
            other: config.TunableFault);

        return new StartupResult(config, decided.State, cause);
    }
}
