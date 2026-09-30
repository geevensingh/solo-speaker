using SoloSpeaker.App.Hosting;
using SoloSpeaker.App.MuteActuator;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Ledger;
using SoloSpeaker.Core.StateStore;

namespace SoloSpeaker.App.Tests;

public sealed class RestoreCommandTests : IDisposable
{
    private const string EndpointId = "endpoint-a";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"solo-speaker-restore-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void An_absent_ledger_exits_successfully_so_uninstall_can_remove_files()
    {
        var actuator = new RestoreFakeMuteActuator();
        List<string> report = [];

        int exitCode = RunRestore(actuator, report);

        Assert.Equal(0, exitCode);
        Assert.Empty(actuator.MutationCalls);
    }

    [Fact]
    public void A_ledger_with_all_entries_restored_exits_successfully_and_clears_the_file()
    {
        RecordLedgerEntry(EndpointId);
        var actuator = new RestoreFakeMuteActuator();
        List<string> report = [];

        int exitCode = RunRestore(actuator, report);

        Assert.Equal(0, exitCode);
        Assert.Contains(new RestoreActuatorCall(EndpointId, false), actuator.MutationCalls);
        Assert.False(File.Exists(LedgerPath));
    }

    [Fact]
    public void A_corrupt_ledger_exits_non_zero_and_leaves_the_file_on_disk()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(LedgerPath, "not json");
        var actuator = new RestoreFakeMuteActuator();
        List<string> report = [];

        int exitCode = RunRestore(actuator, report);

        Assert.NotEqual(0, exitCode);
        Assert.True(File.Exists(LedgerPath));
        Assert.Empty(actuator.MutationCalls);
    }

    [Fact]
    public void A_restore_failure_exits_non_zero_and_keeps_the_entry()
    {
        RecordLedgerEntry(EndpointId);
        var actuator = new RestoreFakeMuteActuator();
        actuator.SetOutcome(EndpointId, MuteApplyOutcome.Failed);
        List<string> report = [];

        int exitCode = RunRestore(actuator, report);

        Assert.NotEqual(0, exitCode);
        Assert.True(File.Exists(LedgerPath));
        Assert.Contains(EndpointId, File.ReadAllText(LedgerPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Another_instance_holding_the_guard_exits_non_zero_and_does_not_touch_the_ledger()
    {
        RecordLedgerEntry(EndpointId);
        string before = File.ReadAllText(LedgerPath);
        var actuator = new RestoreFakeMuteActuator();
        List<string> report = [];

        using SingleInstanceGuard guard = SingleInstanceGuard.TryAcquire(_root);
        Assert.True(guard.IsHeld);

        int exitCode = RunRestore(actuator, report);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(before, File.ReadAllText(LedgerPath));
        Assert.Empty(actuator.MutationCalls);
    }

    [Fact]
    public void The_report_includes_the_resolved_data_root()
    {
        var actuator = new RestoreFakeMuteActuator();
        List<string> report = [];

        int exitCode = RunRestore(actuator, report);

        Assert.Equal(0, exitCode);
        Assert.Contains(report, line => line.Contains(_root, StringComparison.Ordinal));
    }

    private string LedgerPath => DataRoot.PathFor(_root, PersistedFiles.Ledger);

    private int RunRestore(RestoreFakeMuteActuator actuator, List<string> report) =>
        RestoreCommand.Run(_root, new FileStore(), new FakeClock(), () => actuator, report.Add);

    private void RecordLedgerEntry(string endpointId)
    {
        var ledger = new JsonLedger(new FileStore(), LedgerPath, new FakeClock());
        ledger.RecordIntent(endpointId, priorMute: false);
    }

    private readonly record struct RestoreActuatorCall(string EndpointId, bool Muted);

    private sealed class RestoreFakeMuteActuator : IMuteActuator
    {
        private readonly Dictionary<string, MuteApplyOutcome> _outcomes = new(StringComparer.Ordinal);
        private readonly List<RestoreActuatorCall> _mutationCalls = [];

        internal IReadOnlyList<RestoreActuatorCall> MutationCalls => _mutationCalls;

        public string? CurrentEndpointId => EndpointId;

        public bool? ReadActualMute() => false;

        public MuteApplyOutcome SetMute(bool muted) => MuteApplyOutcome.Applied;

        public void Retarget()
        {
        }

        public MuteApplyOutcome TrySetMute(string endpointId, bool muted)
        {
            _mutationCalls.Add(new RestoreActuatorCall(endpointId, muted));
            return _outcomes.TryGetValue(endpointId, out MuteApplyOutcome outcome)
                ? outcome
                : MuteApplyOutcome.Applied;
        }

        public void Dispose()
        {
        }

        internal void SetOutcome(string endpointId, MuteApplyOutcome outcome) =>
            _outcomes[endpointId] = outcome;
    }
}
