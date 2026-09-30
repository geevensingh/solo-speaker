using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Ledger;
using SoloSpeaker.Core.Tests.Fakes;

namespace SoloSpeaker.Core.Tests;

/// <summary>Work item 7's mutation-ledger recovery matrix.</summary>
public sealed class LedgerTests
{
    private const string LedgerPath = @"C:\root\ledger.json";
    private const string EndpointId = "endpoint-a";
    private const string OtherEndpointId = "endpoint-b";

    [Fact]
    public void A_valid_ledger_restores_recorded_endpoints_and_clears_the_entries()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        ledger.RecordIntent(EndpointId, priorMute: false);
        ledger.RecordIntent(OtherEndpointId, priorMute: false);
        var actuator = new FakeMuteActuator();

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.Equal(2, result.Restored);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Unrepaired);
        Assert.False(result.Failed);
        Assert.True(result.IsClean);
        Assert.Empty(ledger.RecordedEndpoints);
        Assert.False(files.Exists(LedgerPath));
        Assert.Equal(
            [
                new MuteActuatorCall(nameof(IMuteActuator.TrySetMute), EndpointId, false),
                new MuteActuatorCall(nameof(IMuteActuator.TrySetMute), OtherEndpointId, false),
            ],
            actuator.Calls);
    }

    [Fact]
    public void A_ledger_for_a_mute_that_never_happened_replays_as_a_clean_no_op()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        ledger.RecordIntent(EndpointId, priorMute: false);
        var actuator = new FakeMuteActuator
        {
            CurrentEndpointId = EndpointId,
            ActualMute = false,
        };

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Restored);
        Assert.False(actuator.ActualMute);
        Assert.Empty(ledger.RecordedEndpoints);
        Assert.False(files.Exists(LedgerPath));
    }

    [Fact]
    public void A_corrupt_ledger_fails_without_muting_anything_and_leaves_the_file_on_disk()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        files.Seed(LedgerPath, "not json");
        var actuator = new FakeMuteActuator();

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.True(result.Failed);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Unrepaired);
        Assert.False(result.IsClean);
        Assert.Empty(actuator.MutationCalls);
        Assert.True(files.Exists(LedgerPath));
        Assert.True(ledger.IsCorrupt);
    }

    [Fact]
    public void A_ledger_entry_for_an_endpoint_that_is_gone_is_cleared_and_counted_as_skipped()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        ledger.RecordIntent(EndpointId, priorMute: false);
        var actuator = new FakeMuteActuator();
        actuator.SetTrySetMuteOutcome(EndpointId, MuteApplyOutcome.EndpointGone);

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Unrepaired);
        Assert.False(result.Failed);
        Assert.True(result.IsClean);
        Assert.Empty(ledger.RecordedEndpoints);
        Assert.False(files.Exists(LedgerPath));
    }

    [Fact]
    public void A_ledger_written_before_the_process_died_replays_as_a_clean_no_op()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        ledger.RecordIntent(EndpointId, priorMute: false);
        var actuator = new FakeMuteActuator
        {
            CurrentEndpointId = EndpointId,
            ActualMute = false,
        };

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Restored);
        Assert.False(actuator.ActualMute);
        Assert.Empty(ledger.RecordedEndpoints);
        Assert.False(files.Exists(LedgerPath));
    }

    [Fact]
    public void A_ledger_entry_whose_restore_fails_is_retained_and_is_not_clean()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        ledger.RecordIntent(EndpointId, priorMute: false);
        var actuator = new FakeMuteActuator();
        actuator.SetTrySetMuteOutcome(EndpointId, MuteApplyOutcome.Failed);

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Unrepaired);
        Assert.False(result.Failed);
        Assert.False(result.IsClean);
        Assert.Equal([EndpointId], ledger.RecordedEndpoints);
        Assert.True(files.Exists(LedgerPath));
    }

    /// <summary>
    /// Guards the revision 11 defect where <c>--restore</c> could mute, exit successfully,
    /// and let the uninstaller delete the only repair record behind it.
    /// </summary>
    [Fact]
    public void A_prior_muted_entry_is_never_reapplied_as_a_mute_during_replay()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        ledger.RecordIntent(EndpointId, priorMute: true);
        var actuator = new FakeMuteActuator();

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.IsClean);
        Assert.DoesNotContain(actuator.MutationCalls, call => call.Muted == true);
        Assert.Empty(ledger.RecordedEndpoints);
        Assert.False(files.Exists(LedgerPath));
    }

    [Fact]
    public void Recording_the_same_endpoint_twice_keeps_the_first_prior_mute_value()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        ledger.RecordIntent(EndpointId, priorMute: false);
        ledger.RecordIntent(EndpointId, priorMute: true);
        var actuator = new FakeMuteActuator();

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.Equal(1, result.Restored);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(
            [new MuteActuatorCall(nameof(IMuteActuator.TrySetMute), EndpointId, false)],
            actuator.Calls);
        Assert.False(files.Exists(LedgerPath));
    }

    [Fact]
    public void A_ledger_with_an_empty_endpoint_id_is_treated_as_corrupt_in_whole()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        files.Seed(LedgerPath, """
            {
              "schema": 1,
              "entries": [
                { "endpointId": "endpoint-a", "priorMute": false, "mutedAtUtc": "2026-09-30T00:00:00+00:00" },
                { "endpointId": "", "priorMute": false, "mutedAtUtc": "2026-09-30T00:00:00+00:00" }
              ]
            }
            """);
        var actuator = new FakeMuteActuator();

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.True(result.Failed);
        Assert.False(result.IsClean);
        Assert.Empty(actuator.MutationCalls);
        Assert.True(files.Exists(LedgerPath));
        Assert.True(ledger.IsCorrupt);
    }

    [Fact]
    public void An_absent_ledger_file_replays_cleanly_without_error()
    {
        (FakeFileStore files, JsonLedger ledger) = CreateLedger();
        var actuator = new FakeMuteActuator();

        LedgerReplayResult result = ledger.Replay(actuator);

        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Unrepaired);
        Assert.False(result.Failed);
        Assert.True(result.IsClean);
        Assert.Empty(actuator.MutationCalls);
        Assert.False(files.Exists(LedgerPath));
    }

    private static (FakeFileStore Files, JsonLedger Ledger) CreateLedger()
    {
        var files = new FakeFileStore();
        var clock = new FakeClock();

        return (files, new JsonLedger(files, LedgerPath, clock));
    }
}
