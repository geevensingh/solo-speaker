using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.PeerLink.Wire;

/// <summary>
/// The eight signed fields of the v1 payload in <c>docs/wire-format.md</c>. The ninth field,
/// <c>mac</c>, is the tag over these and is therefore not one of them.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately carries no <c>pairKey</c>. A generated <c>ToString</c> prints every member,
/// so a secret on a record is one assertion-failure message or log line from the disclosure
/// <c>AGENTS.md</c> §6 forbids. The key is passed as a <see cref="ReadOnlySpan{T}"/>
/// parameter instead, which cannot be captured into a record, boxed, or rendered.
/// </para>
/// <para>
/// <see cref="SentUtc"/> is supplied by the caller and is never read from a clock inside
/// <c>SoloSpeaker.Core</c>. Per <c>AGENTS.md</c> §3, nothing outside the composition root
/// reads the system clock, and a canonicalizer that quietly called
/// <see cref="DateTimeOffset.UtcNow"/> would be the most plausible place for that rule to
/// break, because it reads as harmless.
/// </para>
/// </remarks>
/// <param name="Version">
/// Format version. A value this machine does not know, from an authenticated peer, raises
/// <c>error</c> at ingress step 5 - the only case that reaches step 5 at all.
/// </param>
/// <param name="PairId">The pairing GUID. Transmitted; scopes the broadcast namespace.</param>
/// <param name="SenderId">The sending machine's roster ID - the <c>machineId</c> field.</param>
/// <param name="Seq">The Lamport clock. Orders by event count, never by wall-clock time.</param>
/// <param name="ActiveOwner">
/// A roster ID, or <see cref="MachineId.None"/> before the first claim.
/// </param>
/// <param name="MicLive">
/// Always emitted explicitly, including in phase 1 where it is hardcoded
/// <see langword="false"/>. An absent field is a version mismatch, never a default.
/// </param>
/// <param name="Bye">
/// Deliberate departure. Clears peer presence immediately and <b>never</b> alters
/// <c>activeOwner</c>; enforcement of that lives in the reducer, which is what
/// <see cref="IsDeparture"/> exists to let it route.
/// </param>
/// <param name="SentUtc">Informational and logged only. Never a drop condition.</param>
public readonly record struct PeerDatagram(
    int Version,
    PairId PairId,
    MachineId SenderId,
    ulong Seq,
    MachineId ActiveOwner,
    bool MicLive,
    bool Bye,
    DateTimeOffset SentUtc)
{
    /// <summary>
    /// Whether this datagram goes to the presence layer alone. A departure bypasses §5.4
    /// entirely: its <c>seq</c> and <c>activeOwner</c> are ignored in both directions of
    /// ordering, which is what keeps §5.2 true.
    /// </summary>
    public bool IsDeparture => Bye;
}
