using SoloSpeaker.App.Logging;
using SoloSpeaker.App.MuteActuator;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Ledger;
using SoloSpeaker.Core.StateStore;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// <c>--restore</c>: replay and clear the mutation ledger without starting the service.
/// </summary>
/// <remarks>
/// <para>
/// §7.3's uninstall path, and the reason <c>uninstall.ps1</c> is a script rather than a
/// folder delete. It is <see cref="StartupSequence"/> minus <c>LoadPersisted</c> - the guard
/// and the replay, in that order - and it is written that way deliberately so a later reader
/// does not have to diff two orderings to find out whether they agree.
/// </para>
/// <para>
/// <b>It talks to a console it was not built with.</b> <c>SoloSpeaker.exe</c> is a
/// <c>WinExe</c>, so it has no console of its own and a caller sees nothing. Worse,
/// PowerShell's call operator does not wait on a GUI-subsystem binary at all, so
/// <c>$LASTEXITCODE</c> reflects the launch rather than the exit - which meant
/// <c>uninstall.ps1</c> read success unconditionally and deleted the binary and the ledger
/// on that reading. <c>AttachConsole</c> plus the script's <c>Start-Process -Wait</c> is what
/// makes the exit code and the message both real.
/// </para>
/// </remarks>
public static class RestoreCommand
{
    /// <summary>Exit code when another instance holds the guard.</summary>
    public const int ExitInstanceRunning = 4;

    /// <summary>Exit code when the ledger could not be read, or an endpoint was not repaired.</summary>
    public const int ExitNotRepaired = 5;

    /// <summary>Replays the ledger for one data root.</summary>
    /// <returns>Zero only when nothing is left muted by this app.</returns>
    public static int Run(string root, IFileStore files, IClock clock, Func<IMuteActuator> actuatorFactory, Action<string> report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(actuatorFactory);
        ArgumentNullException.ThrowIfNull(report);

        // The resolved root is printed because the behaviour depends on ambient environment:
        // LOCALAPPDATA is per-profile, so running elevated or as another user silently looks
        // at a different ledger. A repair tool that cannot say which machine-state it
        // inspected is a repair tool nobody can trust.
        report($"SoloSpeaker --restore, data root: {root}");

        using SingleInstanceGuard guard = SingleInstanceGuard.TryAcquire(root);

        if (!guard.IsHeld)
        {
            // ADR 0012, and manual row E9. Replaying a live instance's ledger would restore
            // an endpoint it believes it holds and clear the record of it.
            report("SoloSpeaker is running; exit it first.");
            return ExitInstanceRunning;
        }

        // ADR 0015 wants ledger replays recorded, and a repair is exactly the thing worth
        // having a record of afterwards. This path has no host and no cycle, so the sink is
        // constructed here and disposed with the command - which also flushes it.
        using var sink = new RollingFileLogSink(DataRoot.LogsFor(root), clock);
        var diagnostics = new DiagnosticLog(sink, clock);

        diagnostics.Note($"--restore, data root: {root}");

        string ledgerPath = DataRoot.PathFor(root, PersistedFiles.Ledger);
        var ledger = new JsonLedger(files, ledgerPath, clock, diagnostics.LedgerActivity);

        using IMuteActuator actuator = actuatorFactory();

        LedgerReplayResult result = ledger.Replay(actuator);

        if (result.Failed)
        {
            report("The mutation ledger could not be read. Nothing was restored, and it has been left in place.");
            diagnostics.Flush(force: true);
            return ExitNotRepaired;
        }

        if (result.Unrepaired > 0)
        {
            report($"{result.Unrepaired} endpoint(s) could not be restored. Their ledger entries have been kept.");
            diagnostics.Flush(force: true);
            return ExitNotRepaired;
        }

        report($"Restored {result.Restored} endpoint(s); {result.Skipped} entr(ies) needed no action.");
        diagnostics.Flush(force: true);

        if (sink.HasStopped || sink.DroppedEntries > 0)
        {
            // The log is the only record of what a repair did. If it could not be written,
            // say so on the channel that is working rather than leaving the operator to
            // discover an empty file later.
            report("Note: the diagnostic log could not be written, so this repair is unrecorded.");
        }

        return 0;
    }
}
