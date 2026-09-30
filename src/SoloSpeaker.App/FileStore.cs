using Microsoft.Win32.SafeHandles;
using SoloSpeaker.Core.Abstractions;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

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

        WriteDurable(pending, contents);

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

        FlushDirectory(directory);
    }

    /// <summary>
    /// Writes and forces the bytes to stable storage before the handle closes.
    /// </summary>
    /// <remarks>
    /// <see cref="File.WriteAllBytes(string, byte[])"/> cannot do this: it opens, writes and
    /// closes, and closing only flushes to the OS cache. The distinction is the whole of the
    /// ledger's crash-recovery guarantee - §7.3 records an intent "before <c>SetMute</c>,
    /// flushed to disk, then the mutation is applied", and manual row D11 pulls the power
    /// while muted. An entry lost in the write-back window leaves a mute the audio stack
    /// still remembers and no record that anyone made it.
    /// </remarks>
    private static void WriteDurable(string path, ReadOnlySpan<byte> contents)
    {
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);

        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Forces the directory entry created by the rename to stable storage.
    /// </summary>
    /// <remarks>
    /// Flushing the file is necessary but not sufficient. It buys <em>ordering</em> - the
    /// bytes precede the name that publishes them - but NTFS logs the rename itself lazily,
    /// so a power cut just after <c>File.Replace</c> returns can still lose it. This
    /// closes that window. It is best-effort: a directory handle is not obtainable on every
    /// filesystem, and failing to flush is not a reason to fail the write that succeeded.
    /// </remarks>
    private static void FlushDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            using SafeFileHandle handle = PInvoke.CreateFile(
                directory,
                0x40000000u,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                lpSecurityAttributes: null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
                hTemplateFile: null);

            if (!handle.IsInvalid)
            {
                PInvoke.FlushFileBuffers(handle);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
