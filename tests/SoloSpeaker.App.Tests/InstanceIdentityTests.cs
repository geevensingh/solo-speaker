using SoloSpeaker.App.Hosting;

namespace SoloSpeaker.App.Tests;

public sealed class InstanceIdentityTests
{
    [Fact]
    public void Two_different_data_roots_produce_different_mutex_names()
    {
        string firstRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity", "first");
        string secondRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity", "second");

        string firstName = InstanceIdentity.ForRoot(firstRoot);
        string secondName = InstanceIdentity.ForRoot(secondRoot);

        Assert.NotEqual(firstName, secondName);
    }

    /// <summary>
    /// Work item 12's guard must not treat one root as two instances just because the path
    /// came from a switch, an environment variable, or a hand-written shortcut.
    /// </summary>
    [Fact]
    public void The_same_root_spelled_differently_produces_the_same_mutex_name()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity", "same-root");
        string trailingSeparator = root + Path.DirectorySeparatorChar;
        string differentCase = root.ToUpperInvariant();
        string withParentSegment = Path.Combine(root, "child", "..");

        string expected = InstanceIdentity.ForRoot(root);

        Assert.Equal(expected, InstanceIdentity.ForRoot(trailingSeparator));
        Assert.Equal(expected, InstanceIdentity.ForRoot(differentCase));
        Assert.Equal(expected, InstanceIdentity.ForRoot(withParentSegment));
    }

    [Fact]
    public void The_mutex_name_stays_in_the_local_kernel_namespace()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity", "namespaced");

        string name = InstanceIdentity.ForRoot(root);

        Assert.StartsWith(@"Local\", name, StringComparison.Ordinal);
    }
}
