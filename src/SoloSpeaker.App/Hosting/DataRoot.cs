using SoloSpeaker.Core.StateStore;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// Resolves the directory the persisted files live in.
/// </summary>
/// <remarks>
/// <para>
/// The host's business by <c>AGENTS.md</c> §3, which assigns "the filesystem paths" to
/// <c>SoloSpeaker.App</c>. <c>SoloSpeaker.Core</c> knows the file <em>names</em> - see
/// <see cref="PersistedFiles"/> - and nothing about where they sit.
/// </para>
/// <para>
/// Overridable because three later work items need it to be: work item 6 runs two instances
/// on one host with "separate config roots and ports"; work item 12 scopes its
/// single-instance mutex by config root; and <c>uninstall.ps1</c> has to be told which root
/// to remove rather than assuming the default.
/// </para>
/// </remarks>
public static class DataRoot
{
    /// <summary>The environment variable that overrides the default, per <c>AGENTS.md</c> §4's prefix rule.</summary>
    public const string OverrideVariable = "SOLO_SPEAKER_CONFIG_ROOT";

    /// <summary>
    /// Resolves the root: an explicit switch first, then the environment, then
    /// <c>%LOCALAPPDATA%\SoloSpeaker</c>.
    /// </summary>
    public static string Resolve(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(OverrideVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoloSpeaker");
    }

    /// <summary>Composes a persisted file name onto a resolved root.</summary>
    public static string PathFor(string root, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.Combine(root, fileName);
    }

    /// <summary>
    /// The log directory for one data root - ADR 0015's <c>logs\</c> folder.
    /// </summary>
    /// <remarks>
    /// Under the data root rather than under a hardcoded <c>%LOCALAPPDATA%</c>, because the
    /// root is overridable and work item 7's single-instance guard is scoped by it. Two
    /// instances with different roots legitimately run at once - work item 6's two-instance
    /// test does exactly that - and a fixed path would put the log outside the only guard
    /// the product has, with both processes contending for one handle. ADR 0015's literal
    /// path stays true for the default root.
    /// </remarks>
    public static string LogsFor(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return Path.Combine(root, "logs");
    }
}
