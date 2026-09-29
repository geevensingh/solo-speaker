namespace SoloSpeaker.Core.StateStore;

/// <summary>
/// The names of the files SoloSpeaker keeps in its data root.
/// </summary>
/// <remarks>
/// The root itself is the host's business - <c>AGENTS.md</c> §3 assigns "the filesystem
/// paths" to <c>SoloSpeaker.App</c>, which resolves it and composes these names onto it.
/// Keeping the names here means work item 6's second-instance technique, work item 7's
/// ledger, work item 10's pairing bundle and work item 12's uninstall script all refer to
/// one list.
/// </remarks>
public static class PersistedFiles
{
    /// <summary>The replicated latch of <c>docs/design.md</c> §7.5.</summary>
    public const string State = "state.json";

    /// <summary>The pairing artifacts and tunables of §7.5.</summary>
    public const string Config = "config.json";

    /// <summary>The mutation ledger of §7.3. Work item 7.</summary>
    public const string Ledger = "ledger.json";

    /// <summary>The pairing bundle of ADR 0011, deleted on both sides once the ceremony completes.</summary>
    public const string PairingBundle = "pairing.json";
}
