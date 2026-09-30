using System.Globalization;
using System.Text;

namespace SoloSpeaker.Core.Diagnostics;

/// <summary>
/// Renders one <see cref="LogEntry"/> as one line.
/// </summary>
/// <remarks>
/// The only place a line's shape is decided, so "one entry, one line" is enforced rather
/// than assumed: every string-bearing field passes through <see cref="Sanitize"/>, which
/// collapses newlines. A device friendly-name or an endpoint id is attacker-influenced in
/// the general case (<c>AGENTS.md</c> §6), and a log a reader can trust to be line-oriented
/// is the difference between grepping it and parsing it.
/// </remarks>
public static class LogLineFormatter
{
    /// <summary>Formats one entry.</summary>
    public static string Format(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string stamp = entry.At.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        return $"{stamp} {Body(entry)}";
    }

    private static string Body(LogEntry entry) => entry switch
    {
        LogEntry.OwnershipChanged owner =>
            $"ownership activeOwner={owner.ActiveOwner} seq={owner.Seq} source={owner.Source}",

        LogEntry.TrayStateChanged tray =>
            $"tray {tray.From}->{tray.To} cause={tray.Cause}",

        LogEntry.PresenceChanged presence =>
            $"presence {(presence.Present ? "gained" : "lost")} via={(presence.ViaBye ? "bye" : "timeout")}",

        LogEntry.LedgerActivity ledger =>
            $"ledger {Sanitize(ledger.Action)} endpoint={Sanitize(ledger.EndpointId)} {Sanitize(ledger.Detail)}",

        LogEntry.EndpointChanged endpoint =>
            $"endpoint from={Sanitize(endpoint.From ?? "none")} to={Sanitize(endpoint.To ?? "none")}",

        LogEntry.QuarantineChanged quarantine =>
            $"quarantine {Sanitize(quarantine.Transition)} reason={Sanitize(quarantine.Reason)}",

        LogEntry.IngressDrops drops =>
            $"ingress-drops reason={drops.Reason} count={drops.Count} anyAccepted={drops.AnyAccepted}"
            + (drops.PeerVersion is { } version ? $" peerVersion={version}" : string.Empty),

        LogEntry.LogGap gap =>
            $"log-gap lost={gap.LostEntries} reason={Sanitize(gap.Reason)}",

        LogEntry.Note note => Sanitize(note.Text),

        _ => throw new NotSupportedException($"Unhandled log entry '{entry.GetType().Name}'."),
    };

    /// <summary>Collapses anything that would split one entry across two lines.</summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (char character in value)
        {
            builder.Append(character is '\r' or '\n' or '\t' ? ' ' : character);
        }

        return builder.ToString();
    }
}
