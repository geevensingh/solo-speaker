using SoloSpeaker.Core.Identity;

namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// The paired configuration of <c>docs/design.md</c> §7.5 - `pairId`, `pairKey`, the
/// roster, and the tunables.
/// </summary>
/// <remarks>
/// Split out of <see cref="IStateStore"/> per <c>docs/review-2026-09-28.md</c> finding
/// B-4. The roster previously had no seam at all, despite being the right-hand side of
/// §5.5's predicate - so the reducer could not evaluate its central invariant through a
/// declared abstraction, and configuration would have arrived by some undeclared path.
/// </remarks>
public interface IConfigStore
{
    /// <summary>
    /// The enrolled roster of §5.3, or an incomplete one during the §7.7 pairing window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Identity.Roster"/> rather than a list of byte arrays, and the original
    /// reasoning survives the change rather than being dropped by it: ordinal byte equality
    /// is the requirement, and it is the <em>representation</em> that removes the casing
    /// hazard - not any build flag. <see cref="MachineId"/> is bytes with a type around it,
    /// so it satisfies that criterion more strongly than <c>byte[]</c> did, which gave
    /// <em>reference</em> equality and would have made a membership test on the right-hand
    /// side of §5.5's predicate silently false forever.
    /// </para>
    /// <para>
    /// This machine's own ID is <see cref="Identity.Roster.Self"/>. There is deliberately
    /// no separate <c>SelfId</c> member: two copies of one fact diverge the first time a
    /// pairing path writes one and not the other, which is the asymmetric roster of review
    /// finding H-6 - a state §5.5 cannot catch, because both local views stay
    /// self-consistent.
    /// </para>
    /// <para>
    /// A persisted roster ID that fails the strict canonical parse - uppercase hex, wrong
    /// length, or <see cref="MachineId.None"/> - is rejected rather than coerced, and raises
    /// <see cref="TrayState.Error"/>. The machine then has no complete roster and can never
    /// mute, which is the correct direction.
    /// </para>
    /// </remarks>
    Roster Roster { get; }

    /// <summary>The pairing GUID, transmitted on every datagram to scope the namespace.</summary>
    PairId PairId { get; }

    /// <summary>
    /// The 256-bit HMAC key. Never transmitted, never logged, never rendered. Held
    /// protected at rest per <c>adr/0013</c> and decrypted on demand.
    /// </summary>
    /// <remarks>
    /// Deliberately a plain <see cref="byte"/> array rather than a <c>PairKey</c> struct
    /// paralleling <see cref="Identity.PairId"/>. A struct would acquire a hex
    /// <c>ToString</c> by the same symmetry pressure, and a secret with a rendering is one
    /// careless log line or assertion-failure message away from re-committing revision 1's
    /// Critical defect in a new shape. The asymmetry is the safeguard.
    /// <para>
    /// The key is decrypted once and held by the implementation, not decrypted per call.
    /// <c>ArbitrationLoop</c> needs it for every inbound datagram and every broadcast, so a
    /// per-call DPAPI round trip would put a decrypt on the path of unauthenticated
    /// broadcast traffic - and handing out a fresh array per datagram gives the caller no
    /// way to zero what it was given.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> if the protected value cannot be decrypted on this profile,
    /// which raises <see cref="TrayState.Error"/> with a re-pair cause rather than
    /// crashing or falling back.
    /// </returns>
    bool TryGetPairKey(out byte[] pairKey);

    /// <summary>
    /// The port §7.1 broadcasts on, and the §7.4 hotkey. Tunables, so a bad persisted value
    /// falls back to the documented default and raises an <see cref="TrayState.Error"/>
    /// naming the field rather than refusing the whole document - see §7.5.
    /// </summary>
    int Port { get; }

    /// <inheritdoc cref="Port"/>
    string Hotkey { get; }

    /// <summary>
    /// Whether the roster holds both entries. A machine with an incomplete roster can
    /// never mute - §5.5's positive predicate is false - and raises
    /// <see cref="TrayState.Error"/> with a "pairing incomplete" cause.
    /// </summary>
    bool IsPaired { get; }

    /// <summary>
    /// Cross-checks that this configuration and the persisted state carry the same
    /// <c>pairId</c>. §7.5's invariant spans both files, so restoring one from backup
    /// without the other must raise <see cref="TrayState.Error"/> rather than being
    /// silently tolerated.
    /// </summary>
    bool MatchesState(PairId statePairId);

    /// <summary>
    /// Enrolls the peer, completing the roster during the §7.7 pairing window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared in row 4 so that row 5 would not write JSON behind an interface unable to
    /// express the mutation, and **implemented by row 5's <c>ConfigStore</c>** - the write
    /// path is the one D-B's field-preservation argument is about, and a type cannot ship a
    /// member it does not implement. Row 10 owns *when* it is called; row 5 owns what it
    /// writes.
    /// </para>
    /// <para>
    /// Returns <see langword="false"/> if the roster is already complete or the candidate is
    /// rejected by <see cref="Identity.Roster.TryCreate"/> - it is <c>MachineId.None</c>, or
    /// it equals <see cref="Identity.Roster.Self"/>.
    /// </para>
    /// </remarks>
    bool TryCompleteRoster(MachineId peerId);
}
