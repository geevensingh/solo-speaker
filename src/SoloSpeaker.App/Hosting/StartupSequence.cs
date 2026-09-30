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

    /// <summary>Creates a sequence over one data root.</summary>
    public StartupSequence(IFileStore files, ISecretProtector protector, IClock clock, string root)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _files = files;
        _protector = protector;
        _clock = clock;
        _root = root;
    }

    /// <summary>Runs the steps in order.</summary>
    public StartupResult Run()
    {
        AcquireSingleInstance();
        ReplayLedger();

        return LoadPersisted();
    }

    /// <summary>
    /// ADR 0012's named mutex, acquired <b>before</b> ledger replay so a second instance
    /// cannot restore an endpoint the first is legitimately holding. Work item 12.
    /// </summary>
    private static void AcquireSingleInstance()
    {
    }

    /// <summary>
    /// §7.3's "on every startup, before anything else" ledger replay - before anything this
    /// instance is entitled to do, which is why the guard precedes it. Work item 7.
    /// </summary>
    private static void ReplayLedger()
    {
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

        // A tunable that fell back names itself, but only when nothing louder is already
        // pending - §7.4 names the first cause raised.
        ErrorCause cause = decided.Cause != ErrorCause.None ? decided.Cause : config.TunableFault;

        return new StartupResult(config, decided.State, cause);
    }
}
