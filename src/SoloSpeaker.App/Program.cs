using System.Windows.Forms;

namespace SoloSpeaker.App;

/// <summary>
/// Entry point placeholder.
/// </summary>
/// <remarks>
/// Phase 1 has not started. The real entry point must, in this order: replay the mutation
/// ledger (<c>docs/design.md</c> §7.3), handle <c>--restore</c> and exit if present, load
/// configuration and state (§7.5), then enter the rejoin quarantine window (§7.6) before
/// binding PeerLink. See <c>docs/implementation-plan.md</c> for the work breakdown.
/// </remarks>
internal static class Program
{
    internal const int ExitNotImplemented = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Any argument reaching this stub is a subcommand that does not exist yet, and
        // --restore is the one that matters: scripts/uninstall.ps1 gates deletion of the
        // binary and the mutation ledger on its exit code. Reporting success would tell
        // the uninstaller a mute had been repaired when nothing was repaired at all, so
        // the stub fails loudly instead. A bare launch returns 0 because it genuinely did
        // what it was asked.
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

        MessageBox.Show(
            "SoloSpeaker is not implemented yet.\n\n" +
            "See docs/implementation-plan.md for the phase 1 work breakdown.",
            "SoloSpeaker",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        return 0;
    }
}
