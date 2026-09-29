using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.App;

/// <summary>
/// <see cref="IFileStore"/> over <c>System.IO</c>. Raw I/O, and nothing that reasons about
/// what the bytes mean.
/// </summary>
/// <remarks>
/// The thin half of review finding B-4's remediation: every persisted-file <em>policy</em>
/// lives in <c>SoloSpeaker.Core</c> over this seam, so the failure modes are reachable from
/// a <c>net10.0</c> test project. Anything here that started interpreting a document would
/// be the boundary leaking back.
/// </remarks>
public sealed class FileStore : IFileStore
{
    private const string PendingSuffix = ".tmp";

    /// <inheritdoc/>
    public byte[]? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            // An unreadable file is indistinguishable from a malformed one to the policy
            // above this seam, and both resolve to the same refusal there.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void WriteAtomic(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string pending = PendingPath(path);
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(pending, contents);

        if (File.Exists(path))
        {
            // Replace rather than Move: it is atomic on NTFS and it keeps the destination's
            // identity, so a crash between the two leaves the previous file whole.
            File.Replace(pending, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(pending, path);
        }
    }

    /// <inheritdoc/>
    public void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DeleteIfPresent(path);
    }

    /// <inheritdoc/>
    public bool PendingWriteExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(PendingPath(path));
    }

    /// <inheritdoc/>
    public void DiscardPendingWrite(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DeleteIfPresent(PendingPath(path));
    }

    /// <remarks>
    /// <see cref="File.Delete(string)"/> is silent for a missing file but throws for a
    /// missing <em>directory</em>, which would break this seam's documented "succeeds
    /// silently if it is already absent" - reachable on a first run, or after an uninstall
    /// removed the data root.
    /// </remarks>
    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string PendingPath(string path) => path + PendingSuffix;
}
