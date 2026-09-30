using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;

namespace SoloSpeaker.Core.Diagnostics;

/// <summary>
/// The closed set of things worth recording - ADR 0015's list, as types.
/// </summary>
/// <remarks>
/// <para>
/// A union rather than a string, because <c>AGENTS.md</c> §6 forbids logging the
/// <c>pairKey</c> and the design's own revision 1 had a Critical for broadcasting it. A
/// free-form sink makes that a discipline problem forever: every future call site is one
/// interpolation away from re-introducing it. A closed union makes it a type problem - the
/// cases carry only the fields ADR 0015 lists, none of which is the key, and nothing can
/// log a secret without first adding a case that carries one, which is a visible change to
/// this file.
/// </para>
/// <para>
/// Every case overrides <see cref="object.ToString"/> to the type name. A union bounds
/// <em>fields</em>, not <em>rendering</em>, and a record's generated <c>ToString</c> prints
/// every member of anything it holds - so the bound only holds if no case can be rendered
/// by accident. <see cref="LogLineFormatter"/> is the one place a line is produced.
/// </para>
/// </remarks>
public abstract record LogEntry
{
    private LogEntry()
    {
    }

    /// <summary>When it happened. Wall-clock, because a log is read by a human.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// The ownership latch moved. ADR 0015: "ownership changes, <b>with their source</b>".
    /// </summary>
    public sealed record OwnershipChanged(MachineId ActiveOwner, ulong Seq, OwnershipSource Source) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(OwnershipChanged);
    }

    /// <summary>The tray state changed, with the cause that produced it.</summary>
    public sealed record TrayStateChanged(TrayState From, TrayState To, ErrorCause Cause) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(TrayStateChanged);
    }

    /// <summary>
    /// Presence was gained or lost, distinguishing a clean <c>bye</c> from a timeout.
    /// </summary>
    /// <remarks>
    /// The distinction is what makes manual row A9 answerable: a peer that said goodbye left
    /// deliberately, while one that timed out either crashed, went out of range, or is
    /// speaking a wire version we reject.
    /// </remarks>
    public sealed record PresenceChanged(bool Present, bool ViaBye) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(PresenceChanged);
    }

    /// <summary>A mutation ledger write, clear, or replay outcome.</summary>
    public sealed record LedgerActivity(string Action, string EndpointId, string Detail) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(LedgerActivity);
    }

    /// <summary>The default render endpoint changed.</summary>
    public sealed record EndpointChanged(string? From, string? To) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(EndpointChanged);
    }

    /// <summary>The §7.6 rejoin window opened, restarted, or closed.</summary>
    public sealed record QuarantineChanged(string Transition, string Reason) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(QuarantineChanged);
    }

    /// <summary>
    /// One minute's worth of rejected datagrams, by reason. Never one line per datagram.
    /// </summary>
    /// <param name="Reason">Why they were rejected.</param>
    /// <param name="Count">How many, in the minute just elapsed.</param>
    /// <param name="AnyAccepted">
    /// Whether anything at all was accepted in the same window. A bare <c>BadMac</c> count
    /// cannot distinguish an unverifiable peer from ordinary foreign noise; this is the
    /// second half of the condition §7.1's producer reads, and what makes matrix row G9's
    /// "both" two independent observations rather than one count.
    /// </param>
    /// <param name="PeerVersion">
    /// The peer's wire version, for <see cref="IngressResult.UnknownVersion"/> only.
    /// </param>
    /// <remarks>
    /// The version is present for exactly one reason: §7.1 verifies the <c>mac</c> at step 2
    /// and checks the version at step 5, so a step 5 rejection has already authenticated and
    /// its version number can be trusted. Every other version-skew shape dies at step 2 as
    /// <see cref="IngressResult.BadMac"/> with no number recoverable, and feeds the
    /// rate-based unverifiable-peer producer instead. Work item 8's done-criterion is that a
    /// version mismatch is distinguishable from an absent peer; the bare cause achieves that,
    /// and the number is what lets an operator learn <em>which</em> version the peer runs -
    /// the only case where that is knowable at all.
    /// </remarks>
    public sealed record IngressDrops(IngressResult Reason, int Count, bool AnyAccepted, int? PeerVersion = null)
        : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(IngressDrops);
    }

    /// <summary>The log itself lost entries. See the sink's failure handling.</summary>
    public sealed record LogGap(int LostEntries, string Reason) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(LogGap);
    }

    /// <summary>A plain note - startup, shutdown, replay summaries.</summary>
    public sealed record Note(string Text) : LogEntry
    {
        /// <inheritdoc/>
        public override string ToString() => nameof(Note);
    }

    // Deliberately absent, each with the work item that adds it:
    //   hotkey registration result            - work item 9
    //   pairing events, pairing-mode lifetime - work item 10
    //   denylist hits, mic signal changes     - phase 2 (MicWatcher is out of phase 1)
    // ADR 0015 lists all three. They describe behaviour that does not exist yet, so a case
    // for them now would be speculative - but omitting them silently would leave the next
    // reader wondering whether the ADR was honoured.
}
