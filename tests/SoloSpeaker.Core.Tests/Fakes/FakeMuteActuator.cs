using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.Core.Tests.Fakes;

internal readonly record struct MuteActuatorCall(string Method, string? EndpointId, bool? Muted);

internal sealed class FakeMuteActuator : IMuteActuator
{
    private readonly List<MuteActuatorCall> _calls = [];
    private readonly Dictionary<string, MuteApplyOutcome> _setMuteOutcomes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MuteApplyOutcome> _trySetMuteOutcomes = new(StringComparer.Ordinal);

    internal IReadOnlyList<MuteActuatorCall> Calls => _calls;

    internal IEnumerable<MuteActuatorCall> MutationCalls =>
        _calls.Where(call => call.Method is nameof(SetMute) or nameof(TrySetMute));

    internal bool? ActualMute { get; set; }

    internal Action<string, bool>? BeforeSetMute { get; set; }

    public string? CurrentEndpointId { get; set; } = "default-endpoint";

    public bool? ReadActualMute()
    {
        _calls.Add(new MuteActuatorCall(nameof(ReadActualMute), CurrentEndpointId, null));
        return ActualMute;
    }

    public MuteApplyOutcome SetMute(bool muted)
    {
        string? endpointId = CurrentEndpointId;
        _calls.Add(new MuteActuatorCall(nameof(SetMute), endpointId, muted));

        if (endpointId is null)
        {
            return MuteApplyOutcome.Failed;
        }

        BeforeSetMute?.Invoke(endpointId, muted);

        MuteApplyOutcome outcome = OutcomeFor(_setMuteOutcomes, endpointId);

        if (outcome == MuteApplyOutcome.Applied)
        {
            ActualMute = muted;
        }

        return outcome;
    }

    public void Retarget() => _calls.Add(new MuteActuatorCall(nameof(Retarget), CurrentEndpointId, null));

    public MuteApplyOutcome TrySetMute(string endpointId, bool muted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        _calls.Add(new MuteActuatorCall(nameof(TrySetMute), endpointId, muted));

        MuteApplyOutcome outcome = OutcomeFor(_trySetMuteOutcomes, endpointId);

        if (outcome == MuteApplyOutcome.Applied && string.Equals(endpointId, CurrentEndpointId, StringComparison.Ordinal))
        {
            ActualMute = muted;
        }

        return outcome;
    }

    public void Dispose() => _calls.Add(new MuteActuatorCall(nameof(Dispose), null, null));

    internal void ClearCalls() => _calls.Clear();

    internal void SetSetMuteOutcome(string endpointId, MuteApplyOutcome outcome) =>
        _setMuteOutcomes[endpointId] = outcome;

    internal void SetTrySetMuteOutcome(string endpointId, MuteApplyOutcome outcome) =>
        _trySetMuteOutcomes[endpointId] = outcome;

    private static MuteApplyOutcome OutcomeFor(Dictionary<string, MuteApplyOutcome> outcomes, string endpointId) =>
        outcomes.TryGetValue(endpointId, out MuteApplyOutcome outcome)
            ? outcome
            : MuteApplyOutcome.Applied;
}
