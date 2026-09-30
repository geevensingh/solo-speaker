using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.StateStore;
using SoloSpeaker.Core.Tests.StateMachine;
using SoloSpeaker.Core.Tests.TwoNode;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// The six scenarios of <c>docs/implementation-plan.md</c> §4.3, run over two real
/// composition cycles exchanging real canonical bytes on a simulated broadcast subnet.
/// </summary>
public sealed class TwoNodeTests
{
    private static readonly ArbitrationTunables Tunables = ArbitrationTunables.Default;

    /// <summary>§4.3 scenario 1.</summary>
    [Fact]
    public void Claim_ping_pong_converges_on_every_alternation()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();

        for (int alternation = 0; alternation < 12; alternation++)
        {
            TwoNodeHarness.Node claimant = alternation % 2 == 0 ? harness.NodeA : harness.NodeB;
            TwoNodeHarness.Node other = alternation % 2 == 0 ? harness.NodeB : harness.NodeA;

            claimant.Claim();
            harness.Deliver().Beat();

            Assert.Equal(claimant.Self, claimant.State.ActiveOwner);
            Assert.Equal(claimant.Self, other.State.ActiveOwner);
            Assert.False(claimant.ShouldMute);
            Assert.True(other.ShouldMute);
        }
    }

    /// <summary>
    /// §4.3 scenario 2. The datagrams are held in flight so both nodes claim before either
    /// sees the other - otherwise the second adopts the first's claim, §5.4's tiebreak is
    /// never reached, and the two still agree, so the stated assertion passes while the code
    /// it exists to cover never runs.
    /// </summary>
    [Fact]
    public void Simultaneous_claims_converge_on_the_lexicographically_smaller_id()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();

        harness.Subnet.HoldInFlight = true;
        harness.NodeA.Claim();
        harness.NodeB.Claim();

        Assert.Equal(harness.NodeA.Self, harness.NodeA.State.ActiveOwner);
        Assert.Equal(harness.NodeB.Self, harness.NodeB.State.ActiveOwner);
        Assert.Equal(harness.NodeA.State.Seq, harness.NodeB.State.Seq);

        harness.Subnet.Release();
        harness.Deliver().Beat();

        MachineId expected = ReducerHarness.Id(ReducerHarness.BHex);

        Assert.Equal(expected, harness.NodeA.State.ActiveOwner);
        Assert.Equal(expected, harness.NodeB.State.ActiveOwner);
        Assert.Equal(harness.NodeA.State.Seq, harness.NodeB.State.Seq);
        Assert.True(harness.NodeA.ShouldMute);
        Assert.False(harness.NodeB.ShouldMute);
    }

    /// <summary>
    /// §4.3 scenario 3, the lid-open case. "Asleep" means the subnet stops delivering to that
    /// node and nobody ticks it, while the shared clock advances - the only one of the
    /// candidate meanings that models a machine that was actually off.
    /// </summary>
    [Fact]
    public void A_rejoining_node_defers_to_the_one_that_kept_running()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();

        // Both start from a claim B made while they could hear each other.
        harness.NodeB.Claim();
        harness.Deliver().Beat();

        // A goes off the subnet, then claims repeatedly. Its seq climbs where B cannot see
        // it, which is exactly the lid-open condition: a Lamport clock orders by event
        // count, so the sleeping machine's state looks newer while being older in wall-clock
        // terms.
        harness.Subnet.Disconnect("A");

        for (int claim = 0; claim < 5; claim++)
        {
            harness.NodeA.Claim();
            harness.Beat();
        }

        ulong sleepingSeq = harness.NodeA.State.Seq;

        Assert.True(harness.NodeB.State.Seq < sleepingSeq);
        Assert.Equal(harness.NodeB.Self, harness.NodeB.State.ActiveOwner);

        // A wakes into quarantine, hears B, and defers at expiry despite its higher seq.
        harness.Subnet.Reconnect("A");
        harness.NodeA.EnterQuarantine();
        harness.Beat(3);

        Assert.Equal(TrayState.Quarantine, harness.NodeA.Tray);
        Assert.False(harness.NodeA.ShouldMute);

        harness.Advance(Tunables.QuarantineWindow).Beat(2);

        Assert.Equal(harness.NodeB.Self, harness.NodeA.State.ActiveOwner);
        Assert.Equal(harness.NodeB.Self, harness.NodeB.State.ActiveOwner);
        Assert.True(harness.NodeA.ShouldMute);
        Assert.False(harness.NodeB.ShouldMute);
    }

    /// <summary>
    /// §4.3 scenario 4. Both quarantined nodes broadcast nothing - design revision 7 - so
    /// neither latch sets and both take the resume-from-persisted branch at expiry.
    /// Convergence then happens afterwards through ordinary beats and §5.4, which is why the
    /// assertion is made after the window plus a delivery rather than at the expiry tick.
    /// </summary>
    [Fact]
    public void Simultaneous_rejoin_leaves_both_audible_then_converges()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();
        harness.NodeA.Claim();
        harness.Deliver().Beat();

        harness.NodeA.EnterQuarantine();
        harness.NodeB.EnterQuarantine();

        for (int beat = 0; beat < 5; beat++)
        {
            harness.Beat();
            Assert.False(harness.NodeA.ShouldMute);
            Assert.False(harness.NodeB.ShouldMute);
            Assert.Equal(TrayState.Quarantine, harness.NodeA.Tray);
            Assert.Equal(TrayState.Quarantine, harness.NodeB.Tray);
        }

        harness.Advance(Tunables.QuarantineWindow).Beat(3);

        Assert.Null(harness.NodeA.State.Quarantine);
        Assert.Null(harness.NodeB.State.Quarantine);
        Assert.Equal(harness.NodeA.State.ActiveOwner, harness.NodeB.State.ActiveOwner);
    }

    /// <summary>
    /// §4.3 scenario 5. The window is 10 s against a 2 s cadence and its boundary is
    /// inclusive, so five missed beats lands exactly on it and presence survives; the sixth
    /// is what takes it past. §7.1's "five missed beats" is the round number, not the
    /// predicate.
    /// </summary>
    [Fact]
    public void Presence_survives_the_presence_window_and_lapses_one_beat_past_it()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat(2);

        Assert.True(harness.NodeA.PeerPresent);

        harness.Subnet.DropEverything = true;

        // Four missed beats is 8 s, comfortably inside the window.
        harness.Beat(4);
        Assert.True(harness.NodeA.PeerPresent);
        Assert.True(harness.NodeB.PeerPresent);

        // The fifth lands exactly on 10 s, and the boundary is inclusive.
        harness.Beat(1);
        Assert.True(harness.NodeA.PeerPresent);

        // The sixth is past it.
        harness.Beat(1);
        Assert.False(harness.NodeA.PeerPresent);
        Assert.False(harness.NodeB.PeerPresent);
        Assert.Equal(TrayState.Alone, harness.NodeA.Tray);

        // Recovery is a single delivered beat.
        harness.Subnet.DropEverything = false;
        harness.Beat();

        Assert.True(harness.NodeA.PeerPresent);
    }

    /// <summary>
    /// §4.3 scenario 6, with the expectation design revision 8 corrected. A replay cannot
    /// move ownership, advance <c>seq</c>, or cause a write. What it <em>can</em> do - hold
    /// presence after the peer has gone without a <c>bye</c> - is §9.2-8, accepted and
    /// covered by manual row F8, because only a real network produces it.
    /// </summary>
    [Fact]
    public void A_replay_moves_no_ownership_and_persists_nothing()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();
        harness.NodeB.Claim();
        harness.Deliver().Beat();

        byte[] captured = harness.Subnet.LastSentBy("B");

        MachineId owner = harness.NodeA.State.ActiveOwner;
        ulong seq = harness.NodeA.State.Seq;
        int writes = harness.NodeA.Files.WriteCount;

        for (int replay = 0; replay < 25; replay++)
        {
            harness.Subnet.Inject("A", captured);
            harness.Deliver();

            Assert.Equal(owner, harness.NodeA.State.ActiveOwner);
            Assert.Equal(seq, harness.NodeA.State.Seq);
            Assert.Equal(writes, harness.NodeA.Files.WriteCount);
            Assert.Equal(ErrorCause.None, harness.NodeA.Loop.LastResult.ErrorCause);
        }
    }

    /// <summary>
    /// The property a point-to-point simulator could never show: a machine hears its own
    /// broadcast and drops it at ingress step 3, so it never becomes its own peer. Design
    /// revision 6 records that the alternative leaves a machine "muted for a peer that no
    /// longer exists".
    /// </summary>
    [Fact]
    public void A_node_never_counts_its_own_broadcast_as_its_peer()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();
        harness.NodeB.Claim();
        harness.Deliver().Beat();

        Assert.True(harness.NodeA.ShouldMute);

        // B vanishes without a bye. A keeps beating, and hears only itself.
        harness.Subnet.Disconnect("B");
        harness.Beat(8);

        Assert.False(harness.NodeA.PeerPresent);
        Assert.False(harness.NodeA.ShouldMute);
        Assert.Equal(TrayState.Alone, harness.NodeA.Tray);
    }

    /// <summary>
    /// A node hears its own broadcast off the subnet - because that is what a broadcast
    /// does - and drops it at ingress step 3. Asserted on what the node actually received,
    /// not by handing it a datagram, so a point-to-point simulator fails this test.
    /// </summary>
    [Fact]
    public void A_node_receives_its_own_broadcast_and_drops_it_as_self_origin()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat(2);

        Assert.Contains(IngressResult.SelfOrigin, harness.NodeA.Received);
        Assert.Contains(IngressResult.SelfOrigin, harness.NodeB.Received);

        // And the peer's datagrams are still accepted, so the drop is selective.
        Assert.Contains(IngressResult.Accepted, harness.NodeA.Received);
    }

    /// <summary>
    /// The restart scenario work item 4 booked onto work item 5: a node is torn down and
    /// reconstructed from its own persisted state, through the real store rather than
    /// through <c>ArbitrationState.FromPersisted</c>. The round trip is the coverage - the
    /// owner has to survive serialisation, the strict parse, and §7.5's cross-file check.
    /// </summary>
    [Fact]
    public void A_restarted_node_resumes_the_latch_it_persisted()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();

        harness.NodeB.Claim();
        harness.Deliver().Beat();

        MachineId owner = harness.NodeA.State.ActiveOwner;
        ulong seq = harness.NodeA.State.Seq;

        Assert.Equal(harness.NodeB.Self, owner);
        Assert.True(harness.NodeA.ShouldMute);

        StartupOutcome outcome = harness.NodeA.Restart();

        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.Equal(owner, harness.NodeA.State.ActiveOwner);
        Assert.Equal(seq, harness.NodeA.State.Seq);

        // §5.5 is false immediately after a restart whatever the owner says, because
        // presence does not survive one - Goal 1's direction. Design revision 10 adds a
        // second, independent reason: the restart is quarantined by construction, and §5.5
        // gates shouldMute on the window being closed.
        Assert.False(harness.NodeA.ShouldMute);
        Assert.Equal(TrayState.Quarantine, harness.NodeA.Tray);

        // Hearing the peer again is not enough on its own now - the window has to close
        // first, which is what stops a rejoining machine muting on a claim it may have
        // already missed the end of.
        harness.Beat(2);
        Assert.False(harness.NodeA.ShouldMute);

        // ...and it comes back once the window expires and the peer is heard again.
        harness.Advance(harness.Tunables.QuarantineWindow);
        harness.Beat(2);
        Assert.True(harness.NodeA.ShouldMute);
    }

    /// <summary>
    /// §7.5's cross-file check, exercised through the composition rather than against
    /// <c>StartupDecision</c> alone: a state file from another pairing raises rather than
    /// being silently tolerated.
    /// </summary>
    [Fact]
    public void A_restarted_node_refuses_state_from_another_pairing()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();
        harness.NodeB.Claim();
        harness.Deliver().Beat();

        string statePath = harness.NodeA.Files.TextAt(@"C:\A\state.json");
        harness.NodeA.Files.Seed(
            @"C:\A\state.json",
            statePath.Replace(
                WireVectorConstants.PairIdHex, "00112233445566778899aabbccddeeff", StringComparison.Ordinal));

        StartupOutcome outcome = harness.NodeA.Restart();

        Assert.Equal(ErrorCause.StatePairIdMismatch, outcome.Cause);
        Assert.True(harness.NodeA.State.ActiveOwner.IsNone);
        Assert.False(harness.NodeA.ShouldMute);
    }

    /// <summary>Duplication is ordinary on a broadcast medium and must change nothing.</summary>
    [Fact]
    public void Duplicated_datagrams_change_nothing()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Subnet.DuplicateEverything = true;
        harness.Beat();

        harness.NodeB.Claim();
        harness.Deliver().Beat();

        int writes = harness.NodeA.Files.WriteCount;
        harness.Beat(3);

        Assert.Equal(harness.NodeB.Self, harness.NodeA.State.ActiveOwner);
        Assert.Equal(writes, harness.NodeA.Files.WriteCount);
    }

    /// <summary>
    /// §7.5 had no channel to report a write that fails with the process still alive. It
    /// reaches the tray through the one path §7.4 defines.
    /// </summary>
    [Fact]
    public void A_failed_persist_raises_an_error_rather_than_being_swallowed()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat();

        harness.NodeA.Files.FailNextWrite = true;
        harness.NodeA.Claim();

        Assert.Equal(ErrorCause.StatePersistFailed, harness.NodeA.Loop.LastResult.ErrorCause);
        Assert.Equal(TrayState.Error, harness.NodeA.Tray);
    }

    /// <summary>
    /// Design §8 makes <c>IProximitySource</c> a projection over reducer state in phase 1.
    /// The loop implements it, so there is one home and one snapshot.
    /// </summary>
    [Fact]
    public void The_loop_projects_presence_through_the_declared_seam()
    {
        TwoNodeHarness harness = TwoNodeHarness.Create();
        harness.Beat(2);

        // Read through the interface rather than the concrete loop, so the assertion is
        // about the seam being implemented and not about a property that happens to exist.
        static bool ThroughTheSeam(Core.Abstractions.IProximitySource source) => source.PeerPresent;

        Assert.True(ThroughTheSeam(harness.NodeA.Loop));
        Assert.Equal(harness.NodeA.Loop.LastResult.PeerPresent, ThroughTheSeam(harness.NodeA.Loop));
    }
}
