using System.Windows.Forms;
using SoloSpeaker.App.Hosting;
using SoloSpeaker.App.MuteActuator;
using SoloSpeaker.App.PeerLink;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Ledger;
using SoloSpeaker.Core.StateStore;
using Windows.Win32;

namespace SoloSpeaker.App;

/// <summary>
/// Entry point.
/// </summary>
/// <remarks>
/// <para>
/// Both entry paths - the launch and <c>--restore</c> - acquire ADR 0012's single-instance
/// guard before touching the mutation ledger. That ordering is the whole point of the ADR:
/// a second instance replaying the first's ledger would restore an endpoint the first still
/// believes it holds and clear the record of it, so a later hard kill would strand the mute
/// with nothing able to repair it.
/// </para>
/// <para>
/// The machine is quarantined from the moment its state is decided, in Core's
/// <c>StartupDecision</c>, not after binding - see design revision 10.
/// </para>
/// <para>
/// There is still no tray, so a successful launch is a headless process whose only graceful
/// exit is logoff or shutdown. Work item 9 adds the tray and its Exit item; work item 12 adds
/// the shutdown channel <c>install.ps1</c> uses.
/// </para>
/// </remarks>
internal static class Program
{
    internal const int ExitNotImplemented = 2;
    internal const int ExitUnpaired = 3;
    internal const int ExitInstanceRunning = 6;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 1 && string.Equals(args[0], "--restore", StringComparison.Ordinal))
        {
            return Restore();
        }

        if (args.Length > 0)
        {
            MessageBox.Show(
                $"'{string.Join(' ', args)}' is not implemented yet.\n\n" +
                "No audio endpoint was restored. See docs/implementation-plan.md.",
                "SoloSpeaker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return ExitNotImplemented;
        }

        return Run();
    }

    private static int Restore()
    {
        // A WinExe has no console of its own, so an invoking shell sees nothing. Attaching to
        // the parent's is what makes --restore's report readable - and uninstall.ps1 reads
        // both the message and the exit code before deciding whether to delete anything.
        bool attached = PInvoke.AttachConsole(unchecked((uint)-1));

        var lines = new List<string>();

        int exitCode = RestoreCommand.Run(
            DataRoot.Resolve(),
            new FileStore(),
            new SystemClock(),
            static () => new WasapiMuteActuator(),
            message =>
            {
                lines.Add(message);
                Console.WriteLine(message);
            });

        if (!attached)
        {
            // Launched from Explorer or a scheduled task. Say it somewhere the user can see
            // rather than exiting silently with a code nobody reads.
            MessageBox.Show(
                string.Join(Environment.NewLine, lines),
                "SoloSpeaker --restore",
                MessageBoxButtons.OK,
                exitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        return exitCode;
    }

    private static int Run()
    {
        string root = DataRoot.Resolve();
        var clock = new SystemClock();
        var files = new FileStore();

        // Acquired here rather than inside StartupSequence because the handle must outlive
        // startup: ADR 0012 requires the guard to be held for as long as the app runs.
        using SingleInstanceGuard guard = SingleInstanceGuard.TryAcquire(root);

        if (!guard.IsHeld)
        {
            // Matrix row E8: the second instance touches nothing - no ledger write, no
            // endpoint change. Work item 12 adds the balloon.
            MessageBox.Show(
                "SoloSpeaker is already running.",
                "SoloSpeaker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return ExitInstanceRunning;
        }

        using IMuteActuator actuator = new WasapiMuteActuator();
        var ledger = new JsonLedger(files, DataRoot.PathFor(root, PersistedFiles.Ledger), clock);

        var startup = new StartupSequence(files, new DpapiSecretProtector(), clock, root, ledger, actuator);
        StartupResult result = startup.Run(guard);

        if (!result.IsPaired)
        {
            // §7.7: an unpaired machine has no roster, so there is nothing to arbitrate and
            // no loop to build. Work item 10 replaces this with the pairing ceremony.
            MessageBox.Show(
                "SoloSpeaker is not paired yet.\n\n" +
                "Pairing arrives in work item 10. See docs/implementation-plan.md.",
                "SoloSpeaker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return ExitUnpaired;
        }

        JsonConfigStore config = result.Config!;
        var stateStore = new JsonStateStore(
            files, DataRoot.PathFor(root, PersistedFiles.State), config.PairId);

        using var window = new HostWindow();
        using var host = new SoloSpeakerHost(
            window,
            config,
            stateStore,
            clock,
            result.State,
            result.Cause,
            TransportEndpoint.Exclusive(config.Port),
            ledger,
            actuator);

        host.Start();

        // The message loop is the process. Everything arbitration-related runs on this
        // thread, marshalled through the host window - see HostWindow and EventDispatch.
        Application.Run();

        return 0;
    }
}
