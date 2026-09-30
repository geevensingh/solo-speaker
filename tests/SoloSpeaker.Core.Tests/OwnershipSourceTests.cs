using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// Ownership changes must name their writer because all five writers funnel through one
/// reducer write path and emit otherwise-identical persist effects. Without the source on
/// the effect, an observer cannot tell a local claim from a peer adoption; manual rows A9
/// and G9 are exactly about explaining the peer cases.
/// </summary>
public sealed class OwnershipSourceTests
{
    [Fact]
    public void A_manual_claim_names_manual_claim_as_its_ownership_source()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        Assert.Equal(OwnershipSource.ManualClaim, Assert.Single(harness.Persists).Source);
    }

    [Fact]
    public void A_mic_rising_edge_names_mic_edge_as_its_ownership_source()
    {
        ReducerHarness harness = ReducerHarness.For().MicEdge();

        Assert.Equal(OwnershipSource.MicEdge, Assert.Single(harness.Persists).Source);
    }

    [Fact]
    public void A_peer_datagram_with_a_strictly_higher_seq_names_peer_adoption_as_its_ownership_source()
    {
        ReducerHarness harness = ReducerHarness.For();

        harness.PeerState(harness.Peer, 7);

        Assert.Equal(OwnershipSource.PeerAdoption, Assert.Single(harness.Persists).Source);
    }

    [Fact]
    public void An_equal_seq_collision_resolved_by_the_tiebreak_names_tiebreak_win_as_its_ownership_source()
    {
        ReducerHarness harness = ReducerHarness.For().Claim();

        harness.PeerState(harness.Peer, 1);

        Assert.Equal(OwnershipSource.TiebreakWin, Assert.Single(harness.Persists).Source);
    }

    [Fact]
    public void Quarantine_expiring_with_an_observed_peer_pair_names_quarantine_adoption_as_its_ownership_source()
    {
        ReducerHarness harness = ReducerHarness.For()
            .Persisted(ReducerHarness.Id(ReducerHarness.AHex), 500)
            .EnterQuarantine();

        harness.PeerState(harness.Peer, 7);
        harness.Apply(TimeSpan.FromSeconds(12.001), new ArbitrationEvent.Tick());

        Assert.Equal(OwnershipSource.QuarantineAdoption, Assert.Single(harness.Persists).Source);
    }
}
