using SoloSpeaker.Core;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Proves the test harness runs and that <c>SoloSpeaker.Core</c> is reachable from it.
/// </summary>
/// <remarks>
/// The tray-state table assertion that used to live here has been replaced by
/// <c>TrayStateTests</c>, which asserts the stronger property its own comment promised: not
/// that the enum has a particular shape, but that every value has a producer in the reducer
/// and that the mapping is total.
/// </remarks>
public sealed class HarnessSmokeTests
{
    [Fact]
    public void Core_assembly_is_referenced_and_loadable()
    {
        Assert.Equal("SoloSpeaker.Core", typeof(TrayState).Assembly.GetName().Name);
    }
}
