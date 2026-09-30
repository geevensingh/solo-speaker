using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// The name of the single-instance mutex, derived from the data root this instance runs on.
/// </summary>
/// <remarks>
/// <para>
/// ADR 0012 originally fixed the name as <c>Local\SoloSpeaker</c>, unscoped. That
/// contradicted <see cref="DataRoot"/>, whose own remarks already said work item 12 "scopes
/// its single-instance mutex by config root" - the repository disagreed with itself, and
/// nothing depended on which half was right.
/// </para>
/// <para>
/// Work item 6 ended the deferral by shipping a two-instance-on-one-host test that has to
/// keep passing when work item 12 lands the guard. A <c>Local\</c> name is per-logon-session,
/// so two instances contend for it whether they are two processes or two hosts inside one -
/// which would have turned work item 12 into a red build against an Accepted ADR.
/// </para>
/// <para>
/// Only the <em>name</em> lives here. Acquisition, the tray balloon, the non-zero exit code
/// and the shutdown channel are still work item 12's: none of them can be demonstrated
/// before there is a tray.
/// </para>
/// </remarks>
public static class InstanceIdentity
{
    private const string Prefix = @"Local\SoloSpeaker";

    /// <summary>
    /// The mutex name for one data root. Two roots give two names; the same root always
    /// gives the same name, however it was spelled.
    /// </summary>
    /// <remarks>
    /// The root is hashed rather than embedded. A path can exceed the mutex-name length
    /// limit, and it can contain the backslash that separates a kernel-object namespace from
    /// its name - so appending one raw would produce names that are invalid, or worse,
    /// accidentally in a different namespace.
    /// </remarks>
    public static string ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // Ordinal-invariant so that two spellings of one directory cannot be read as two
        // instances. AGENTS.md §2 keeps InvariantGlobalization on, so ToUpperInvariant here
        // is culture-free by construction.
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}-{Convert.ToHexString(digest.AsSpan(0, 16))}");
    }
}
