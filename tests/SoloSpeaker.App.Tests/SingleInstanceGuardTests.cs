using SoloSpeaker.App.Hosting;

namespace SoloSpeaker.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void Two_guards_over_the_same_root_leave_only_the_first_one_held()
    {
        string root = UniqueRoot();

        using SingleInstanceGuard first = SingleInstanceGuard.TryAcquire(root);
        using SingleInstanceGuard second = SingleInstanceGuard.TryAcquire(root);

        Assert.True(first.IsHeld);
        Assert.False(second.IsHeld);
    }

    [Fact]
    public void Two_guards_over_different_roots_can_both_be_held()
    {
        string firstRoot = UniqueRoot();
        string secondRoot = UniqueRoot();

        using SingleInstanceGuard first = SingleInstanceGuard.TryAcquire(firstRoot);
        using SingleInstanceGuard second = SingleInstanceGuard.TryAcquire(secondRoot);

        Assert.True(first.IsHeld);
        Assert.True(second.IsHeld);
    }

    [Fact]
    public void A_guard_released_by_dispose_can_be_acquired_again()
    {
        string root = UniqueRoot();

        using (SingleInstanceGuard first = SingleInstanceGuard.TryAcquire(root))
        {
            Assert.True(first.IsHeld);
        }

        using SingleInstanceGuard second = SingleInstanceGuard.TryAcquire(root);

        Assert.True(second.IsHeld);
    }

    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), $"solo-speaker-guard-{Guid.NewGuid():N}");
}
