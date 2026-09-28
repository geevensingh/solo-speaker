using SoloSpeaker.Core;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Proves the test harness runs and that <c>SoloSpeaker.Core</c> is reachable from it.
/// The substantive suites arrive with phase 1; see <c>docs/implementation-plan.md</c> §5
/// for the mapping from <c>docs/design.md</c> §10 to test files.
/// </summary>
public sealed class HarnessSmokeTests
{
    [Fact]
    public void Core_assembly_is_referenced_and_loadable()
    {
        Assert.Equal("SoloSpeaker.Core", typeof(TrayState).Assembly.GetName().Name);
    }

    /// <summary>
    /// Guards the §7.4 requirement that every tray state has a producer. The enum is the
    /// closed list; if a state is added without a producer being wired up, the phase 1
    /// producer test that replaces this one must fail.
    /// </summary>
    [Fact]
    public void Tray_states_match_the_design_section_7_4_table()
    {
        Assert.Equal(
            [
                TrayState.Active,
                TrayState.Muted,
                TrayState.Alone,
                TrayState.Quarantine,
                TrayState.Error,
            ],
            Enum.GetValues<TrayState>());
    }
}
