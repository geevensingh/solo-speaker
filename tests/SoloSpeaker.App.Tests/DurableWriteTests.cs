using SoloSpeaker.App;

namespace SoloSpeaker.App.Tests;

/// <summary>
/// Durable storage itself requires pulling power at the wrong instant, so these tests assert
/// the observable contract around the durable write rather than pretending to prove the
/// hardware flush.
/// </summary>
public sealed class DurableWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"solo-speaker-durable-write-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Write_atomic_round_trips_and_leaves_no_temp_file_behind()
    {
        var store = new FileStore();
        string path = PathFor("ledger.json");
        byte[] contents = [1, 2, 3, 4];

        store.WriteAtomic(path, contents);

        Assert.Equal(contents, store.Read(path));
        Assert.False(store.PendingWriteExists(path));
    }

    [Fact]
    public void Overwriting_an_existing_file_replaces_its_contents()
    {
        var store = new FileStore();
        string path = PathFor("ledger.json");

        store.WriteAtomic(path, [1]);
        store.WriteAtomic(path, [2, 3]);

        Assert.Equal([2, 3], store.Read(path));
        Assert.False(store.PendingWriteExists(path));
    }

    [Fact]
    public void Writing_into_a_directory_that_does_not_exist_yet_creates_it()
    {
        var store = new FileStore();
        string path = Path.Combine(_root, "nested", "ledger.json");

        store.WriteAtomic(path, [7]);

        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal([7], store.Read(path));
    }

    private string PathFor(string fileName) => Path.Combine(_root, fileName);
}
