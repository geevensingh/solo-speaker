using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Ledger;
using SoloSpeaker.Core.MuteActuator;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.Fakes;

namespace SoloSpeaker.Core.Tests;

/// <summary>Tests for §7.3's completed mute-reconcile table.</summary>
public sealed class MuteReconcilerTests
{
    private const string LedgerPath = @"C:\root\ledger.json";
    private const string EndpointId = "endpoint-a";
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Guards the review-caught Goal 1 defect: peer departure must unmute even when the
    /// actual endpoint state still matches the mute this process last wrote.
    /// </summary>
    [Fact]
    public void Desired_unmuted_and_actual_muted_unmutes_even_when_we_last_wrote_the_mute()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: false);
        Assert.Equal(ErrorCause.None, fixture.Reconciler.Reconcile(shouldMute: true).Cause);
        Assert.True(fixture.Actuator.ActualMute);
        fixture.Actuator.ClearCalls();

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: false);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.False(fixture.Actuator.ActualMute);
        Assert.Contains(new MuteActuatorCall(nameof(IMuteActuator.SetMute), EndpointId, false), fixture.Actuator.Calls);
        Assert.Empty(fixture.Ledger.RecordedEndpoints);
    }

    /// <summary>
    /// The crash window is closed only if the recovery record is durable before the audio
    /// mutation can strand an endpoint.
    /// </summary>
    [Fact]
    public void The_ledger_intent_is_recorded_before_the_endpoint_is_muted()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: false);
        bool observedIntentBeforeMutation = false;
        fixture.Actuator.BeforeSetMute = (endpointId, muted) =>
        {
            Assert.True(muted);
            Assert.Contains(endpointId, fixture.Ledger.RecordedEndpoints);
            observedIntentBeforeMutation = true;
        };

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: true);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.True(observedIntentBeforeMutation);
    }

    [Fact]
    public void A_recent_self_written_mute_echo_does_not_create_a_claim_or_write_again()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: false);
        Assert.Equal(ErrorCause.None, fixture.Reconciler.Reconcile(shouldMute: true).Cause);
        fixture.Actuator.ActualMute = false;
        fixture.Actuator.ClearCalls();

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: true);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.Empty(fixture.Actuator.MutationCalls);
        Assert.Equal([EndpointId], fixture.Ledger.RecordedEndpoints);
    }

    [Fact]
    public void An_old_external_unmute_of_an_endpoint_we_muted_becomes_a_claim_without_re_muting()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: false);
        Assert.Equal(ErrorCause.None, fixture.Reconciler.Reconcile(shouldMute: true).Cause);
        fixture.Actuator.ActualMute = false;
        fixture.Actuator.ClearCalls();
        fixture.Clock.Advance(SuppressionWindow + TimeSpan.FromMilliseconds(1));

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: true);

        Assert.True(outcome.Claim);
        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.DoesNotContain(fixture.Actuator.MutationCalls, call => call.Muted == true);
        Assert.Empty(fixture.Ledger.RecordedEndpoints);
    }

    [Fact]
    public void An_unreadable_actual_mute_state_reports_endpoint_enumeration_failure_without_a_claim()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: null);

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: true);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.EndpointEnumerationFailed, outcome.Cause);
        Assert.Empty(fixture.Actuator.MutationCalls);
        Assert.Empty(fixture.Ledger.RecordedEndpoints);
    }

    [Fact]
    public void A_missing_current_endpoint_reports_endpoint_enumeration_failure_without_mutating()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: false);
        fixture.Actuator.CurrentEndpointId = null;

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: true);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.EndpointEnumerationFailed, outcome.Cause);
        Assert.Empty(fixture.Actuator.MutationCalls);
        Assert.Empty(fixture.Ledger.RecordedEndpoints);
    }

    [Fact]
    public void A_muted_endpoint_we_do_not_own_is_left_alone_without_a_claim_or_ledger_entry()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: true);

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: true);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.Empty(fixture.Actuator.MutationCalls);
        Assert.Empty(fixture.Ledger.RecordedEndpoints);
    }

    [Fact]
    public void A_failed_mute_reports_mute_apply_failed_and_retains_the_ledger_entry()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: false);
        fixture.Actuator.SetSetMuteOutcome(EndpointId, MuteApplyOutcome.Failed);

        ReconcileOutcome outcome = fixture.Reconciler.Reconcile(shouldMute: true);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.MuteApplyFailed, outcome.Cause);
        Assert.Equal([EndpointId], fixture.Ledger.RecordedEndpoints);
        Assert.True(fixture.Files.Exists(LedgerPath));
    }

    /// <summary>
    /// Guards matrix rows D1/D2: clearing a failed release strands a retained headset mute
    /// across the next default-device change.
    /// </summary>
    [Fact]
    public void A_release_whose_restore_fails_reports_mute_apply_failed_and_retains_the_entry()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: true);
        fixture.Ledger.RecordIntent(EndpointId, priorMute: false);
        fixture.Actuator.SetTrySetMuteOutcome(EndpointId, MuteApplyOutcome.Failed);

        ReconcileOutcome outcome = fixture.Reconciler.Release(EndpointId);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.MuteApplyFailed, outcome.Cause);
        Assert.Equal([EndpointId], fixture.Ledger.RecordedEndpoints);
        Assert.True(fixture.Files.Exists(LedgerPath));
    }

    [Fact]
    public void A_successful_release_clears_the_ledger_entry()
    {
        ReconcilerFixture fixture = ReconcilerFixture.Create(actualMute: true);
        fixture.Ledger.RecordIntent(EndpointId, priorMute: false);

        ReconcileOutcome outcome = fixture.Reconciler.Release(EndpointId);

        Assert.False(outcome.Claim);
        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.Empty(fixture.Ledger.RecordedEndpoints);
        Assert.False(fixture.Files.Exists(LedgerPath));
    }

    private sealed class ReconcilerFixture
    {
        private ReconcilerFixture(
            FakeClock clock,
            FakeFileStore files,
            JsonLedger ledger,
            FakeMuteActuator actuator,
            MuteReconciler reconciler)
        {
            Clock = clock;
            Files = files;
            Ledger = ledger;
            Actuator = actuator;
            Reconciler = reconciler;
        }

        internal FakeClock Clock { get; }

        internal FakeFileStore Files { get; }

        internal JsonLedger Ledger { get; }

        internal FakeMuteActuator Actuator { get; }

        internal MuteReconciler Reconciler { get; }

        internal static ReconcilerFixture Create(bool? actualMute)
        {
            var clock = new FakeClock();
            var files = new FakeFileStore();
            var ledger = new JsonLedger(files, LedgerPath, clock);
            var actuator = new FakeMuteActuator
            {
                CurrentEndpointId = EndpointId,
                ActualMute = actualMute,
            };
            var tunables = ArbitrationTunables.Default with
            {
                SelfChangeSuppression = SuppressionWindow,
            };
            var reconciler = new MuteReconciler(actuator, ledger, clock, tunables);

            return new ReconcilerFixture(clock, files, ledger, actuator, reconciler);
        }
    }
}
