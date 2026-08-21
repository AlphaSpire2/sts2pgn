using STS2PGN.Model;
using STS2PGN.Scripting;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class ScriptConverterTests
{
    private const string RecordedRunStart =
        "{\"actor\":null,\"data\":{\"source_snapshot\":{\"schema_version\":1,\"data\":{}},\"state\":"
        + "{\"scope\":\"run_start.core\",\"acts\":[{\"index\":0,\"model_id\":\"ACT.OVERGROWTH\"}],"
        + "\"players\":[{\"slot\":\"p0\",\"character_model\":\"CHARACTER.IRONCLAD\","
        + "\"unlocks\":{\"epochs\":[],\"encounters_seen\":[],\"number_of_runs\":0}}]}},\"extensions\":{}}";

    [Fact]
    public void RecordingBecomesAValidScriptWithOnlyDecisions()
    {
        RunRecord recording = TestRecordFactory.CreateBuilder("VICTORY")
            .AddTag("GameStartTime", "123")
            .AppendJson("run.start", RecordedRunStart)
            .AppendJson("state.observe", "{\"after\":1,\"context\":\"run.start\",\"digest\":\"x\",\"projection_version\":1,\"state\":{}}")
            .AppendJson("map.choose", TestRecordFactory.ScriptMapChoose)
            .AppendJson("room.enter", "{\"data\":{}}")
            .AppendJson("rest.choose", "{\"actor\":\"p0\",\"data\":{\"option_id\":\"rest\"}}")
            .AppendJson("rest.resolve", "{\"actor\":\"p0\",\"data\":{\"action_sequence\":5,\"success\":true}}")
            .AppendJson("run.finish", "{\"data\":{\"result\":\"VICTORY\"}}")
            .Build(RunResult.Victory);

        RunRecord script = ScriptConverter.FromRecording(recording, "sts2pgn-tool/0.8.0");

        Assert.True(Sts2PgnValidator.Validate(script, Sts2PgnValidationMode.Script).IsValid);
        Assert.Equal("sts2pgn-tool/0.8.0", script.Metadata.GetValue("Producer"));
        Assert.Null(script.Metadata.GetValue("RecorderVersion"));
        Assert.Null(script.Metadata.GetValue("RunId"));
        Assert.Null(script.Metadata.GetValue("Result"));
        Assert.Null(script.Metadata.GetValue("GameStartTime"));
        Assert.Equal("CUSTOM", script.Metadata.GetValue("Mode"));
        Assert.Equal("TESTSEED", script.Metadata.GetValue("Seed"));
        Assert.Equal(["Format", "FormatVersion", "Producer"], script.Metadata.Tags.Take(3).Select(tag => tag.Name));
        Assert.Equal(["run.start", "map.choose", "rest.choose"], script.Entries.Select(entry => entry.Kind));
        Assert.Equal(RunResult.Incomplete, script.Result);
        Assert.False(script.Entries[0].Payload.GetProperty("data").TryGetProperty("source_snapshot", out _));

        DecisionScript parsed = DecisionScript.FromRecord(script);
        Assert.Equal(2, parsed.Decisions.Count);
    }

    [Fact]
    public void ForbiddenRecordsAreReportedNotDropped()
    {
        RunRecord recording = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", RecordedRunStart)
            .AppendJson("game.unknown_action", "{\"actor\":\"p0\",\"data\":{}}")
            .Build();

        Sts2PgnValidationException exception =
            Assert.Throws<Sts2PgnValidationException>(() => ScriptConverter.FromRecording(recording, "x/1"));
        Assert.Contains(exception.ValidationResult.Errors, issue => issue.Code == "script_unsupported_record");
    }

    [Fact]
    public void InvalidProducerIsRejected() =>
        Assert.Throws<ArgumentException>(() => ScriptConverter.FromRecording(TestRecordFactory.CreateValid(), "Bad Producer"));
}
