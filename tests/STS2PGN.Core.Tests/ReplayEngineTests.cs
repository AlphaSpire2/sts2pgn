using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Replay;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class ReplayEngineTests
{
    [Fact]
    public async Task MatchingReplayCompletesSuccessfully()
    {
        RunRecord record = CreateObservedRecord(80);
        FakeSimulator simulator = new(State(80), RunResult.Defeat);

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.True(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.Equal(3, result.LastMatchingObservationSequence);
        Assert.Equal(["map.choose"], simulator.Consumed.Select(step => step.Kind));
        ReplayObservationRequest request = Assert.Single(simulator.ObservationRequests);
        Assert.Equal("action.settled", request.Context);
        Assert.Equal(1, request.ProjectionVersion);
        Assert.Equal("run.core", request.Scope);
    }

    [Fact]
    public async Task ReplayStopsAtFirstStateDivergence()
    {
        RunRecord record = CreateObservedRecord(80);
        FakeSimulator simulator = new(State(79), RunResult.Defeat);

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.False(result.Succeeded);
        Assert.Equal(ReplayFailureKind.StateDivergence, result.Failure!.Kind);
        ReplayDivergenceReport report = Assert.IsType<ReplayDivergenceReport>(result.Failure.Divergence);
        Assert.Equal(3, report.Sequence);
        Assert.Equal(2, report.AfterAction);
        Assert.Equal("map.choose", report.ActionKind);
        Assert.Equal(1, report.ActionPayload.GetProperty("data").GetProperty("col").GetInt32());
        Assert.Equal(80, report.ExpectedState.GetProperty("players")[0].GetProperty("current_hp").GetInt32());
        Assert.Equal(79, report.ActualState.GetProperty("players")[0].GetProperty("current_hp").GetInt32());
        Assert.Equal("/players/p0/current_hp", Assert.Single(report.Differences).Path);
    }

    [Fact]
    public async Task ReplayReportsRejectedAction()
    {
        RunRecord record = CreateObservedRecord(80);
        FakeSimulator simulator = new(State(80), RunResult.Defeat)
        {
            RejectedKind = "map.choose",
        };

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.Equal(ReplayFailureKind.IllegalAction, result.Failure!.Kind);
        Assert.Equal(2, result.Failure.Sequence);
        Assert.Empty(simulator.ObservationRequests);
    }

    [Fact]
    public async Task ReplayReportsUnsupportedCompatibilityBeforeInitialization()
    {
        RunRecord record = CreateObservedRecord(80);
        FakeSimulator simulator = new(State(80), RunResult.Defeat)
        {
            Compatibility = ReplayCompatibilityResult.Unsupported("game build not implemented"),
        };

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.Equal(ReplayFailureKind.UnsupportedTrace, result.Failure!.Kind);
        Assert.False(simulator.WasInitialized);
    }

    [Fact]
    public async Task ReplayReportsTerminalResultDifference()
    {
        RunRecord record = CreateObservedRecord(80);
        FakeSimulator simulator = new(State(80), RunResult.Victory);

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.Equal(ReplayFailureKind.ResultDivergence, result.Failure!.Kind);
        Assert.Equal(RunResult.Victory, result.ActualResult);
        Assert.Equal(3, result.LastMatchingObservationSequence);
    }

    [Fact]
    public async Task ReplayComparesObservationAfterRunFinish()
    {
        JsonElement state = State(80);
        string digest = CanonicalJson.ComputeSha256(state);
        RunRecord record = TestRecordFactory.CreateBuilder("DEFEAT")
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("run.finish", "{\"data\":{\"result\":\"DEFEAT\"}}")
            .AppendJson(
                "state.observe",
                $"{{\"after\":2,\"context\":\"run.end\",\"digest\":\"{digest}\",\"projection_version\":1,\"state\":{state.GetRawText()}}}")
            .Build(RunResult.Defeat);
        FakeSimulator simulator = new(state, RunResult.Defeat);

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(record, simulator);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.LastMatchingObservationSequence);
        Assert.Equal("run.end", Assert.Single(simulator.ObservationRequests).Context);
    }

    [Fact]
    public async Task ReplayRejectsInvalidTraceBeforeCallingAdapter()
    {
        RunRecord invalid = new(
            TestRecordFactory.CreateValid().Metadata,
            [new RunRecordEntry(1, "map.choose", JsonSerializer.SerializeToElement(new { }))],
            RunResult.Defeat);
        FakeSimulator simulator = new(State(80), RunResult.Defeat);

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(invalid, simulator);

        Assert.Equal(ReplayFailureKind.InvalidTrace, result.Failure!.Kind);
        Assert.False(simulator.WasInitialized);
    }

    [Fact]
    public async Task ReplayClassifiesUnfinishedTraceSeparately()
    {
        FakeSimulator simulator = new(State(80), RunResult.Incomplete);

        ReplayExecutionResult result = await new ReplayEngine().ExecuteAsync(
            TestRecordFactory.CreateValid(),
            simulator);

        Assert.Equal(ReplayFailureKind.IncompleteTrace, result.Failure!.Kind);
        Assert.False(simulator.WasInitialized);
    }

    private static RunRecord CreateObservedRecord(int expectedHp)
    {
        JsonElement state = State(expectedHp);
        string digest = CanonicalJson.ComputeSha256(state);
        return TestRecordFactory.CreateBuilder("DEFEAT")
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("map.choose", "{\"actor\":\"p0\",\"data\":{\"col\":1,\"row\":1}}")
            .AppendJson(
                "state.observe",
                $"{{\"after\":2,\"context\":\"action.settled\",\"digest\":\"{digest}\",\"projection_version\":1,\"state\":{state.GetRawText()}}}")
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

    private sealed class FakeSimulator(JsonElement state, RunResult result) : IReplaySimulatorAdapter
    {
        private readonly JsonElement _state = state.Clone();
        private readonly RunResult _result = result;

        public ReplayCompatibilityResult Compatibility { get; init; } = ReplayCompatibilityResult.Supported;
        public string? RejectedKind { get; init; }
        public bool WasInitialized { get; private set; }
        public List<ReplayStep> Consumed { get; } = [];
        public List<ReplayObservationRequest> ObservationRequests { get; } = [];

        public ValueTask<ReplayCompatibilityResult> CheckCompatibilityAsync(
            RunMetadata metadata,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Compatibility);

        public ValueTask<ReplayConsumeResult> InitializeAsync(
            RunMetadata metadata,
            ReplayStep runStart,
            CancellationToken cancellationToken = default)
        {
            WasInitialized = true;
            return ValueTask.FromResult(ReplayConsumeResult.Success);
        }

        public ValueTask<ReplayConsumeResult> ConsumeAsync(
            ReplayStep step,
            CancellationToken cancellationToken = default)
        {
            Consumed.Add(step);
            return ValueTask.FromResult(step.Kind == RejectedKind
                ? ReplayConsumeResult.Rejected("rejected for test")
                : ReplayConsumeResult.Success);
        }

        public ValueTask<JsonElement> CaptureStateAsync(
            ReplayObservationRequest request,
            CancellationToken cancellationToken = default)
        {
            ObservationRequests.Add(request);
            return ValueTask.FromResult(_state.Clone());
        }

        public ValueTask<RunResult> GetResultAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_result);
    }
}
