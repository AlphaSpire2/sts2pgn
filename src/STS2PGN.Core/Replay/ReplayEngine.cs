using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Replay;

/// <summary>
/// Drives a simulator adapter through a validated trace and stops at the first failure.
/// </summary>
public sealed class ReplayEngine
{
    private readonly ReplayEngineOptions _options;

    public ReplayEngine(ReplayEngineOptions? options = null)
    {
        _options = options ?? new ReplayEngineOptions();
        if (_options.MaximumDifferences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumDifferences must be positive.");
        }
    }

    public async ValueTask<ReplayExecutionResult> ExecuteAsync(
        RunRecord record,
        IReplaySimulatorAdapter simulator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(simulator);

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);
        if (!validation.IsValid)
        {
            string message = string.Join(
                "; ",
                validation.Errors.Take(5).Select(issue => $"{issue.Code}: {issue.Message}"));
            return Failure(
                record,
                ReplayFailureKind.InvalidTrace,
                null,
                message,
                null,
                null);
        }

        if (record.Result == RunResult.Incomplete)
        {
            return Failure(
                record,
                ReplayFailureKind.IncompleteTrace,
                null,
                "The trace has no finalized result.",
                null,
                null);
        }

        ReplayCompatibilityResult compatibility = await simulator.CheckCompatibilityAsync(
            record.Metadata,
            cancellationToken);
        if (!compatibility.IsSupported)
        {
            return Failure(
                record,
                ReplayFailureKind.UnsupportedTrace,
                null,
                compatibility.Message ?? "The simulator does not support this trace.",
                null,
                null);
        }

        ReplayTimeline timeline = new(record);
        ReplayStep runStart = timeline.First(step => step.Role == ReplayRecordRole.Initialization);
        ReplayConsumeResult initialization = await simulator.InitializeAsync(
            record.Metadata,
            runStart,
            cancellationToken);
        if (!initialization.Accepted)
        {
            return Failure(
                record,
                ReplayFailureKind.IllegalAction,
                runStart.Sequence,
                initialization.Message ?? "The simulator rejected run initialization.",
                null,
                null);
        }

        long? lastMatchingObservation = null;
        Dictionary<long, ReplayStep> stepsBySequence = timeline.ToDictionary(step => step.Sequence);
        foreach (ReplayStep step in timeline.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step.Role == ReplayRecordRole.Observation)
            {
                ReplayExecutionResult? divergence = await CompareObservationAsync(
                    record,
                    simulator,
                    step,
                    stepsBySequence,
                    lastMatchingObservation,
                    cancellationToken);
                if (divergence is not null)
                {
                    return divergence;
                }

                lastMatchingObservation = step.Sequence;
                continue;
            }

            if (step.Role == ReplayRecordRole.Terminal)
            {
                continue;
            }

            if (step.Role == ReplayRecordRole.Reinitialization)
            {
                // The real game rewound to its last save checkpoint here, so decisions
                // between that checkpoint and the resume never happened in the resumed
                // timeline. The simulator discards its state and re-anchors on the
                // run.resume payload instead of consuming it as an action.
                ReplayConsumeResult reinitialization = await simulator.InitializeAsync(
                    record.Metadata,
                    step,
                    cancellationToken);
                if (!reinitialization.Accepted)
                {
                    return Failure(
                        record,
                        ReplayFailureKind.IllegalAction,
                        step.Sequence,
                        reinitialization.Message ?? "The simulator rejected run.resume re-initialization.",
                        null,
                        lastMatchingObservation);
                }

                continue;
            }

            ReplayConsumeResult consumed = await simulator.ConsumeAsync(step, cancellationToken);
            if (!consumed.Accepted)
            {
                return Failure(
                    record,
                    ReplayFailureKind.IllegalAction,
                    step.Sequence,
                    consumed.Message ?? $"The simulator rejected {step.Kind}.",
                    null,
                    lastMatchingObservation);
            }
        }

        RunResult actualResult = await simulator.GetResultAsync(cancellationToken);
        if (actualResult != record.Result)
        {
            return Failure(
                record,
                ReplayFailureKind.ResultDivergence,
                timeline.Count == 0 ? null : timeline[^1].Sequence,
                $"Expected {record.Result.ToToken()}, simulator produced {actualResult.ToToken()}.",
                actualResult,
                lastMatchingObservation);
        }

        return ReplayExecutionResult.Success(record.Result, actualResult, lastMatchingObservation);
    }

    private async ValueTask<ReplayExecutionResult?> CompareObservationAsync(
        RunRecord record,
        IReplaySimulatorAdapter simulator,
        ReplayStep observation,
        IReadOnlyDictionary<long, ReplayStep> stepsBySequence,
        long? lastMatchingObservation,
        CancellationToken cancellationToken)
    {
        JsonElement payload = observation.Payload;
        long after = payload.GetProperty("after").GetInt64();
        string context = payload.GetProperty("context").GetString()!;
        int projectionVersion = payload.GetProperty("projection_version").GetInt32();
        string expectedDigest = payload.GetProperty("digest").GetString()!;
        JsonElement expectedState = payload.GetProperty("state");
        string? scope = expectedState.TryGetProperty("scope", out JsonElement scopeElement)
            && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()
                : null;
        ReplayObservationRequest request = new(
            observation.Sequence,
            after,
            context,
            projectionVersion,
            scope);
        JsonElement actualState = await simulator.CaptureStateAsync(request, cancellationToken);
        string actualDigest;
        try
        {
            actualDigest = CanonicalJson.ComputeSha256(actualState);
        }
        catch (ArgumentException exception)
        {
            return Failure(
                record,
                ReplayFailureKind.StateDivergence,
                observation.Sequence,
                $"The simulator returned uncanonicalizable state: {exception.Message}",
                null,
                lastMatchingObservation);
        }

        StateComparisonResult comparison = JsonStateComparer.Compare(
            expectedState,
            actualState,
            _options.ComparisonRules,
            _options.MaximumDifferences);
        if (comparison.IsMatch && string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal))
        {
            return null;
        }

        bool hasAction = stepsBySequence.TryGetValue(after, out ReplayStep? action);
        string actionKind = hasAction ? action!.Kind : "unknown";
        JsonElement actionPayload = hasAction
            ? action!.Payload
            : JsonSerializer.SerializeToElement<object?>(null);
        ReplayDivergenceReport report = new(
            record.Metadata.GetValue("RunId"),
            observation.Sequence,
            after,
            actionKind,
            actionPayload,
            context,
            expectedDigest,
            actualDigest,
            expectedState,
            actualState,
            comparison.Differences,
            comparison.DifferencesTruncated);
        return Failure(
            record,
            ReplayFailureKind.StateDivergence,
            observation.Sequence,
            $"Simulator state diverged after sequence {after} ({actionKind}).",
            null,
            lastMatchingObservation,
            report);
    }

    private static ReplayExecutionResult Failure(
        RunRecord record,
        ReplayFailureKind kind,
        long? sequence,
        string message,
        RunResult? actualResult,
        long? lastMatchingObservation,
        ReplayDivergenceReport? divergence = null) =>
        ReplayExecutionResult.Failed(
            record.Result,
            actualResult,
            lastMatchingObservation,
            new ReplayFailure(kind, sequence, message, divergence));
}
