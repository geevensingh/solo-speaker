using SoloSpeaker.App;
using SoloSpeaker.App.Hosting;

namespace SoloSpeaker.App.Tests;

/// <summary>
/// The Windows half of work item 5: real file I/O and real DPAPI.
/// </summary>
/// <remarks>
/// Everything that reasons about what the bytes <em>mean</em> is tested in
/// <c>SoloSpeaker.Core.Tests</c> over the same seams - review finding B-4's remediation.
/// What is left here is exactly the part that cannot cross to <c>net10.0</c>.
/// </remarks>
public sealed class FileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"solo-speaker-tests-{Guid.NewGuid():N}");

    private string PathFor(string name) => Path.Combine(_root, name);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Reading_an_absent_file_is_null_rather_than_an_exception()
    {
        Assert.Null(new FileStore().Read(PathFor("missing.json")));
    }

    [Fact]
    public void An_atomic_write_creates_the_directory_and_round_trips()
    {
        var store = new FileStore();
        byte[] contents = [1, 2, 3, 4];

        store.WriteAtomic(PathFor("state.json"), contents);

        Assert.Equal(contents, store.Read(PathFor("state.json")));
        Assert.False(store.PendingWriteExists(PathFor("state.json")));
    }

    [Fact]
    public void Overwriting_replaces_the_previous_contents_and_leaves_no_temp_file()
    {
        var store = new FileStore();
        string path = PathFor("state.json");

        store.WriteAtomic(path, [1]);
        store.WriteAtomic(path, [2]);

        Assert.Equal([2], store.Read(path));
        Assert.False(store.PendingWriteExists(path));
    }

    /// <summary>
    /// §4.5's "temp file present but replace never happened" case, from the side of the seam
    /// that owns the temp file's name.
    /// </summary>
    [Fact]
    public void An_orphaned_temp_file_is_observable_and_discardable()
    {
        var store = new FileStore();
        string path = PathFor("state.json");

        store.WriteAtomic(path, [1]);
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path + ".tmp", [9, 9, 9]);

        Assert.True(store.PendingWriteExists(path));

        store.DiscardPendingWrite(path);

        Assert.False(store.PendingWriteExists(path));
        Assert.Equal([1], store.Read(path));
    }

    [Fact]
    public void Discarding_a_temp_file_that_is_not_there_is_not_an_error()
    {
        new FileStore().DiscardPendingWrite(PathFor("state.json"));
    }

    [Fact]
    public void Deleting_an_absent_file_is_not_an_error()
    {
        new FileStore().Delete(PathFor("state.json"));
    }
}

/// <summary>ADR 0013's DPAPI protector.</summary>
public sealed class DpapiSecretProtectorTests
{
    private static readonly byte[] Secret = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private static readonly byte[] Entropy = [.. Enumerable.Range(100, 16).Select(value => (byte)value)];

    [Fact]
    public void A_secret_round_trips_under_the_same_entropy()
    {
        var protector = new DpapiSecretProtector();

        byte[] protectedSecret = protector.Protect(Secret, Entropy);

        Assert.NotEqual(Secret, protectedSecret);
        Assert.True(protector.TryUnprotect(protectedSecret, Entropy, out byte[] recovered));
        Assert.Equal(Secret, recovered);
    }

    /// <summary>
    /// The substitute for the cross-profile case, which a test process cannot produce: DPAPI
    /// surfaces a wrong profile and a wrong entropy through the same
    /// <c>CryptographicException</c>, so this exercises the same failure route. The profile
    /// case itself is manual row E10 - see `implementation-plan.md` §4.1.
    /// </summary>
    [Fact]
    public void A_different_entropy_fails_closed_rather_than_throwing()
    {
        var protector = new DpapiSecretProtector();
        byte[] protectedSecret = protector.Protect(Secret, Entropy);
        byte[] otherEntropy = [.. Entropy.Select(value => (byte)(value ^ 0xff))];

        Assert.False(protector.TryUnprotect(protectedSecret, otherEntropy, out byte[] recovered));
        Assert.Empty(recovered);
    }

    [Fact]
    public void Corrupted_ciphertext_fails_closed_rather_than_throwing()
    {
        var protector = new DpapiSecretProtector();
        byte[] protectedSecret = protector.Protect(Secret, Entropy);
        protectedSecret[^1] ^= 0xff;

        Assert.False(protector.TryUnprotect(protectedSecret, Entropy, out _));
    }

    /// <summary>Never a fallback to treating the ciphertext as plaintext.</summary>
    [Fact]
    public void Empty_input_fails_closed()
    {
        Assert.False(new DpapiSecretProtector().TryUnprotect([], Entropy, out _));
    }

    /// <summary>ADR 0013 binds the ciphertext to the pairing, so an older pairing cannot decrypt.</summary>
    [Fact]
    public void The_entropy_binds_the_ciphertext_to_one_pairing()
    {
        var protector = new DpapiSecretProtector();
        byte[] first = protector.Protect(Secret, Entropy);
        byte[] secondEntropy = new byte[16];
        secondEntropy[0] = 1;

        Assert.False(protector.TryUnprotect(first, secondEntropy, out _));
        Assert.True(protector.TryUnprotect(first, Entropy, out _));
    }
}

/// <summary>The data root of <c>AGENTS.md</c> §3, which the host owns.</summary>
public sealed class DataRootTests
{
    [Fact]
    public void An_explicit_root_wins()
    {
        string resolved = DataRoot.Resolve(@"C:\somewhere\else");

        Assert.Equal(Path.GetFullPath(@"C:\somewhere\else"), resolved);
    }

    [Fact]
    public void The_environment_override_is_used_when_no_switch_is_given()
    {
        string expected = Path.Combine(Path.GetTempPath(), "solo-speaker-root");

        try
        {
            Environment.SetEnvironmentVariable(DataRoot.OverrideVariable, expected);

            Assert.Equal(Path.GetFullPath(expected), DataRoot.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataRoot.OverrideVariable, null);
        }
    }

    [Fact]
    public void The_default_is_under_local_application_data()
    {
        Environment.SetEnvironmentVariable(DataRoot.OverrideVariable, null);

        string resolved = DataRoot.Resolve();

        Assert.EndsWith("SoloSpeaker", resolved, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            resolved,
            StringComparison.Ordinal);
    }
}
