using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.App.Tests;

/// <summary>
/// Proves the App-side test harness runs and can reach a Windows-targeted assembly.
/// </summary>
/// <remarks>
/// This project exists because <c>docs/review-2026-09-28.md</c> finding B-4 established
/// that nothing in <c>SoloSpeaker.App</c> was reachable by any test. The substantive
/// suites arrive with the work items that add the implementations: the DPAPI
/// <see cref="ISecretProtector"/> at work item 5, the WASAPI
/// <see cref="IMuteActuator"/> at work item 7, and the tray icon resources at work item 9.
/// </remarks>
public sealed class HarnessSmokeTests
{
    [Fact]
    public void App_assembly_is_referenced_and_loadable()
    {
        Assert.Equal("SoloSpeaker", typeof(Program).Assembly.GetName().Name);
    }

    /// <summary>
    /// The stub exit-code contract, which <c>scripts/uninstall.ps1</c> depends on. An
    /// earlier stub returned <c>args.Length</c>, so <c>--restore</c> exited 1 and drove the
    /// uninstaller down a branch that deleted the binary and the mutation ledger behind a
    /// mute it had not repaired. The contract is asserted rather than assumed because the
    /// cost of getting it wrong is <c>design.md</c> Goal 1.
    /// </summary>
    [Fact]
    public void Unimplemented_subcommand_does_not_report_success()
    {
        Assert.NotEqual(0, Program.ExitNotImplemented);
    }
}
