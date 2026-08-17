using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2PGN.Replay;

public sealed class ReplayDivergenceReport
{
    [JsonPropertyName("run_id")]
    public string? RunId { get; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; }

    [JsonPropertyName("after_action")]
    public long AfterAction { get; }

    [JsonPropertyName("action_kind")]
    public string ActionKind { get; }

    [JsonPropertyName("action_payload")]
    public JsonElement ActionPayload { get; }

    [JsonPropertyName("context")]
    public string Context { get; }

    [JsonPropertyName("expected_digest")]
    public string ExpectedDigest { get; }

    [JsonPropertyName("actual_digest")]
    public string ActualDigest { get; }

    [JsonPropertyName("expected_state")]
    public JsonElement ExpectedState { get; }

    [JsonPropertyName("actual_state")]
    public JsonElement ActualState { get; }

    [JsonPropertyName("differences")]
    public IReadOnlyList<StateDifference> Differences { get; }

    [JsonPropertyName("differences_truncated")]
    public bool DifferencesTruncated { get; }

    public ReplayDivergenceReport(
        string? runId,
        long sequence,
        long afterAction,
        string actionKind,
        JsonElement actionPayload,
        string context,
        string expectedDigest,
        string actualDigest,
        JsonElement expectedState,
        JsonElement actualState,
        IReadOnlyList<StateDifference> differences,
        bool differencesTruncated)
    {
        RunId = runId;
        Sequence = sequence;
        AfterAction = afterAction;
        ActionKind = actionKind;
        ActionPayload = actionPayload.Clone();
        Context = context;
        ExpectedDigest = expectedDigest;
        ActualDigest = actualDigest;
        ExpectedState = expectedState.Clone();
        ActualState = actualState.Clone();
        Differences = differences.ToArray();
        DifferencesTruncated = differencesTruncated;
    }
}
