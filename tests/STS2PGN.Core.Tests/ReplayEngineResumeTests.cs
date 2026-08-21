using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Replay;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class ReplayEngineResumeTests
{
    [Fact]
    public async Task EngineReinitializesTheSimulatorAtRunResume()
    {
        JsonElement state = State(hp: 60);
        RunRecord record = ResumedTrace(state);
        RecordingSimulator simulator = new(state, RunResult.Defeat);

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.True(result.Succeeded);
        Assert.Equal(["run.start", "run.resume"], simulator.InitializedKinds);
        Assert.DoesNotContain(simulator.Consumed, step => step.Kind == "run.resume");
        Assert.Contains(simulator.Consumed, step => step.Kind == "combat.end_turn");
    }

    [Fact]
    public async Task EngineFailsWhenTheSimulatorRejectsReinitialization()
    {
        JsonElement state = State(hp: 60);
        RunRecord record = ResumedTrace(state);
        RecordingSimulator simulator = new(state, RunResult.Defeat) { RejectResume = true };

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.False(result.Succeeded);
        Assert.Equal(ReplayFailureKind.IllegalAction, result.Failure!.Kind);
        Assert.Equal(3, result.Failure.Sequence);
    }

    private static RunRecord ResumedTrace(JsonElement state)
    {
        string stateJson = state.GetRawText();
        string digest = CanonicalJson.ComputeSha256(state);
        return TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("combat.end_turn", "{\"actor\":\"p0\",\"data\":{\"turn\":1}}")
            .AppendJson("run.resume", $"{{\"data\":{{\"reloads\":1,\"state\":{stateJson}}}}}")
            .AppendJson(
                "state.observe",
                $"{{\"after\":3,\"context\":\"run.resume\",\"digest\":\"{digest}\",\"projection_version\":1,\"state\":{stateJson}}}")
            .AppendJson("run.finish", "{\"data\":{\"result\":\"DEFEAT\"}}")
            .Build(RunResult.Defeat);
    }

    private static JsonElement State(int hp) => JsonSerializer.SerializeToElement(new
    {
        scope = "run.core",
        run = new { seed = "TEST", act = 0, floor = 1 },
        acts = new[]
        {
            new { index = 0, model_id = "ACT.OVERGROWTH" },
        },
        players = new[]
        {
            new { slot = "p0", character_model = "CHARACTER.IRONCLAD", current_hp = hp },
        },
        rooms = Array.Empty<object>(),
        choices = new
        {
            player_choices = Array.Empty<object>(),
            rewards = Array.Empty<object>(),
        },
        special_event = (object?)null,
        combat = (object?)null,
    });

    private sealed class RecordingSimulator(JsonElement state, RunResult result) : IReplaySimulatorAdapter
    {
        private readonly JsonElement _state = state.Clone();
        private readonly RunResult _result = result;

        public bool RejectResume { get; init; }
        public List<string> InitializedKinds { get; } = [];
        public List<ReplayStep> Consumed { get; } = [];

        public ValueTask<ReplayCompatibilityResult> CheckCompatibilityAsync(
            RunMetadata metadata,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReplayCompatibilityResult.Supported);

        public ValueTask<ReplayConsumeResult> InitializeAsync(
            RunMetadata metadata,
            ReplayStep runStart,
            CancellationToken cancellationToken = default)
        {
            InitializedKinds.Add(runStart.Kind);
            return ValueTask.FromResult(RejectResume && runStart.Kind == "run.resume"
                ? ReplayConsumeResult.Rejected("resume rejected for test")
                : ReplayConsumeResult.Success);
        }

        public ValueTask<ReplayConsumeResult> ConsumeAsync(
            ReplayStep step,
            CancellationToken cancellationToken = default)
        {
            Consumed.Add(step);
            return ValueTask.FromResult(ReplayConsumeResult.Success);
        }

        public ValueTask<JsonElement> CaptureStateAsync(
            ReplayObservationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_state.Clone());

        public ValueTask<RunResult> GetResultAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_result);
    }
}
