using System.Windows.Forms;
using SoloSpeaker.App.Hosting;
using SoloSpeaker.App.PeerLink;
using SoloSpeaker.Core.StateStore;

namespace SoloSpeaker.App;

/// <summary>
/// Entry point.
/// </summary>
/// <remarks>
/// <para>
/// The launch path is real from work item 6. It acquires the single-instance guard and
/// replays the mutation ledger (both still no-ops, work items 12 and 7), loads configuration
/// and state (§7.5), then builds the host, which binds PeerLink and starts the cadence.
/// </para>
/// <para>
/// The machine is quarantined from the moment its state is decided, in Core's
/// <c>StartupDecision</c> - not after binding. §7.6 used to say the window opened on the
/// first successful bind and send, which could not be implemented: a quarantined machine
/// broadcasts nothing, so the trigger either never fired, or the first send escaped the
/// window carrying the persisted <c>(activeOwner, seq)</c> and recreated the lid-open
/// defect. See design revision 10.
/// </para>
/// <para>
/// There is no tray yet, so a successful launch is a headless process whose only graceful
/// exit is logoff or shutdown. Work item 9 adds the tray and its Exit item; work item 12
/// adds the shutdown channel <c>install.ps1</c> uses.
/// </para>
/// </remarks>
internal static class Program
{
    internal const int ExitNotImplemented = 2;
    internal const int ExitUnpaired = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // --restore is the subcommand that matters: scripts/uninstall.ps1 gates deletion of
        // the binary and the mutation ledger on its exit code. Reporting success would tell
        // the uninstaller a mute had been repaired when nothing was repaired at all, so it
        // still fails loudly until work item 7 implements it.
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

    private static int Run()
    {
        string root = DataRoot.Resolve();
        var clock = new SystemClock();

        var startup = new StartupSequence(new FileStore(), new DpapiSecretProtector(), clock, root);
        StartupResult result = startup.Run();

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
            new FileStore(), DataRoot.PathFor(root, PersistedFiles.State), config.PairId);

        using var window = new HostWindow();
        using var host = new SoloSpeakerHost(
            window,
            config,
            stateStore,
            clock,
            result.State,
            result.Cause,
            TransportEndpoint.Exclusive(config.Port));

        host.Start();

        // The message loop is the process. Everything arbitration-related runs on this
        // thread, marshalled through the host window - see HostWindow and EventDispatch.
        Application.Run();

        return 0;
    }
}
