using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.Core.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IFileStore"/> that models the temp-file half of an atomic write.
/// </summary>
/// <remarks>
/// The pending file is modelled rather than ignored because §4.5's "temp file present but
/// replace never happened" row has nothing to assert against otherwise. Work item 5's real
/// <c>FileStore</c> uses the same two-step shape over <c>System.IO</c>.
/// </remarks>
internal sealed class FakeFileStore : IFileStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _pending = new(StringComparer.Ordinal);

    /// <summary>Stop <see cref="WriteAtomic"/> before the replace, leaving an orphan temp file.</summary>
    internal bool InterruptNextWrite { get; set; }

    /// <summary>Make the next write throw, for §7.5's persist-failure cause.</summary>
    internal bool FailNextWrite { get; set; }

    internal int WriteCount { get; private set; }

    internal int DiscardCount { get; private set; }

    public byte[]? Read(string path) => _files.TryGetValue(path, out byte[]? contents) ? contents : null;

    public void WriteAtomic(string path, ReadOnlySpan<byte> contents)
    {
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("Simulated write failure.");
        }

        _pending[path] = contents.ToArray();

        if (InterruptNextWrite)
        {
            InterruptNextWrite = false;
            return;
        }

        _files[path] = _pending[path];
        _pending.Remove(path);
        WriteCount++;
    }

    public void Delete(string path) => _files.Remove(path);

    public bool PendingWriteExists(string path) => _pending.ContainsKey(path);

    public void DiscardPendingWrite(string path)
    {
        if (_pending.Remove(path))
        {
            DiscardCount++;
        }
    }

    /// <summary>Writes raw bytes directly, for hand-edited and corrupt-file cases.</summary>
    internal void Seed(string path, string contents) =>
        _files[path] = System.Text.Encoding.UTF8.GetBytes(contents);

    /// <summary>Reads a file back as text, to assert what was written.</summary>
    internal string TextAt(string path) =>
        _files.TryGetValue(path, out byte[]? contents)
            ? System.Text.Encoding.UTF8.GetString(contents)
            : throw new InvalidOperationException($"No file at '{path}'.");

    internal bool Exists(string path) => _files.ContainsKey(path);
}

/// <summary>
/// An <see cref="ISecretProtector"/> that is reversible without DPAPI, so the persisted-file
/// policy is testable from <c>net10.0</c>.
/// </summary>
/// <remarks>
/// It models the two properties the real one has that the policy depends on: the entropy
/// binds the ciphertext, and a mismatch returns <see langword="false"/> rather than throwing.
/// </remarks>
internal sealed class FakeSecretProtector : ISecretProtector
{
    /// <summary>Refuse everything, standing in for a different Windows profile.</summary>
    internal bool RefuseEverything { get; set; }

    public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> entropy)
    {
        byte[] result = new byte[entropy.Length + secret.Length];
        entropy.CopyTo(result);
        secret.CopyTo(result.AsSpan(entropy.Length));
        return result;
    }

    public bool TryUnprotect(ReadOnlySpan<byte> protectedSecret, ReadOnlySpan<byte> entropy, out byte[] secret)
    {
        secret = [];

        if (RefuseEverything || protectedSecret.Length < entropy.Length)
        {
            return false;
        }

        if (!protectedSecret[..entropy.Length].SequenceEqual(entropy))
        {
            return false;
        }

        secret = protectedSecret[entropy.Length..].ToArray();
        return true;
    }
}
