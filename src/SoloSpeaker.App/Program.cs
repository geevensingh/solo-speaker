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
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        MessageBox.Show(
            "SoloSpeaker is not implemented yet.\n\n" +
            "See docs/implementation-plan.md for the phase 1 work breakdown.",
            "SoloSpeaker",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        return args.Length;
    }
}
