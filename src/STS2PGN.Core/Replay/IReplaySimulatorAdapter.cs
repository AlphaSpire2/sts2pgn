using System.Text.Json;
using STS2PGN.Model;

namespace STS2PGN.Replay;

/// <summary>
/// Adapter implemented by a simulator. The portable replay engine owns ordering,
/// validation, observation comparison, and first-failure reporting.
/// </summary>
public interface IReplaySimulatorAdapter
{
    ValueTask<ReplayCompatibilityResult> CheckCompatibilityAsync(
        RunMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds simulator state from an anchoring record's payload. Called once for
    /// run.start, and again for every run.resume record, where the simulator must
    /// discard its current state and re-anchor on the resumed snapshot.
    /// </summary>
    ValueTask<ReplayConsumeResult> InitializeAsync(
        RunMetadata metadata,
        ReplayStep runStart,
        CancellationToken cancellationToken = default);

    ValueTask<ReplayConsumeResult> ConsumeAsync(
        ReplayStep step,
        CancellationToken cancellationToken = default);

    ValueTask<JsonElement> CaptureStateAsync(
        ReplayObservationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<RunResult> GetResultAsync(CancellationToken cancellationToken = default);
}
