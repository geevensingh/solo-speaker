using SoloSpeaker.Core.PeerLink;

namespace SoloSpeaker.Core.Diagnostics;

/// <summary>
/// ADR 0015's per-minute rollup of rejected datagrams, by reason.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing rule: <b>never one line per datagram</b>. Two machines beating every two
/// seconds produce 86,400 datagrams a day, and a per-datagram drop log buries the signal in
/// the noise it is made of. What matters is a rate change by reason.
/// </para>
/// <para>
/// <see cref="IngressResult.SelfOrigin"/> is deliberately excluded. A machine's own broadcast
/// returning to it is expected on any bridged subnet - §7.1 says so, and drops it at ingress
/// step 3 for that reason - so counting it would log this machine's own heartbeat at 43,200
/// lines a day. <see cref="IngressResult.Accepted"/> and
/// <see cref="IngressResult.PairingEnrollment"/> are not drops at all; the former is tracked
/// only as the <c>anyAccepted</c> flag below.
/// </para>
/// <para>
/// Pure, and driven by a clock it is handed, so <c>Core.Tests</c> can prove the rollup rather
/// than a human squinting at a file.
/// </para>
/// </remarks>
public sealed class IngressDropAggregator
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly IngressResult[] Counted =
    [
        IngressResult.Unreadable,
        IngressResult.ForeignPairId,
        IngressResult.BadMac,
        IngressResult.NotInRoster,
        IngressResult.SeqOutOfBounds,
        IngressResult.UnknownVersion,
    ];

    private readonly Dictionary<IngressResult, int> _counts = [];
    private int? _peerVersion;

    private TimeSpan _windowStartedAt;
    private bool _started;
    private bool _anyAccepted;

    /// <summary>Records one ingress verdict.</summary>
    public void Observe(IngressResult result, TimeSpan now, int? peerVersion = null)
    {
        if (!_started)
        {
            _started = true;
            _windowStartedAt = now;
        }

        if (result == IngressResult.Accepted)
        {
            _anyAccepted = true;
            return;
        }

        if (!Counted.Contains(result))
        {
            return;
        }

        _counts[result] = _counts.GetValueOrDefault(result) + 1;

        if (result == IngressResult.UnknownVersion && peerVersion is not null)
        {
            // Step 5 is the only rejection whose version number is authenticated, so it is
            // the only one an operator can act on. Last-seen rather than a set: a real
            // version-mismatched peer has exactly one version, and a set would turn the
            // line into a list nobody reads.
            _peerVersion = peerVersion;
        }
    }

    /// <summary>
    /// Emits the elapsed minute's rollup, if a minute has elapsed and anything was dropped.
    /// </summary>
    /// <remarks>
    /// A quiet minute produces nothing at all rather than a row of zeroes - the log is read
    /// by scrolling, and zeroes are what makes scrolling useless.
    /// </remarks>
    public IReadOnlyList<LogEntry> Flush(TimeSpan now, DateTimeOffset stampedAt, bool force = false)
    {
        if (!_started || (!force && now - _windowStartedAt < Window))
        {
            return [];
        }

        List<LogEntry> entries = [];

        foreach (IngressResult reason in Counted)
        {
            if (_counts.TryGetValue(reason, out int count) && count > 0)
            {
                entries.Add(
                    new LogEntry.IngressDrops(
                        reason,
                        count,
                        _anyAccepted,
                        reason == IngressResult.UnknownVersion ? _peerVersion : null)
                    {
                        At = stampedAt,
                    });
            }
        }

        _counts.Clear();
        _anyAccepted = false;
        _peerVersion = null;
        _windowStartedAt = now;

        return entries;
    }
}
