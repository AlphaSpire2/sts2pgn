using System.Text.Json;
using System.Text.Json.Nodes;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class ScriptConverterTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("[\"ACT.OVERGROWTH\",\"ACT.UNDERDOCKS\"]")]
    public void ProfileGenerationInputsSurviveTransportAndScriptConversion(string discoveredActs)
    {
        JsonNode payload = JsonNode.Parse(RecordedRunStart)!;
        payload["data"]!["profile"] = new JsonObject
        {
            ["discovered_acts"] = JsonNode.Parse(discoveredActs),
        };
        RunRecord recording = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", payload.ToJsonString())
            .Build();
        RunRecord roundTrip = Sts2PgnParser.Parse(Sts2PgnWriter.Write(recording));
        RunRecord script = ScriptConverter.FromRecording(roundTrip, "sts2pgn-tool/0.10.1");
        foreach (RunRecord record in new[] { roundTrip, script })
        {
            JsonElement acts = record.Entries[0].Payload.GetProperty("data")
                .GetProperty("profile").GetProperty("discovered_acts");
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(discoveredActs), acts));
        }
        Assert.False(script.Entries[0].Payload.GetProperty("data").TryGetProperty("source_snapshot", out _));
    }

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
            .AppendJson("event.crystal_sphere.start", "{\"actor\":\"p0\",\"data\":{\"grid\":{}}}")
            .AppendJson("event.crystal_sphere.set_tool", "{\"actor\":\"p0\",\"data\":{\"tool\":\"small\"}}")
            .AppendJson("event.crystal_sphere.click", "{\"actor\":\"p0\",\"data\":{\"x\":3,\"y\":7,\"tool\":\"small\",\"remaining_before\":4}}")
            .AppendJson("event.crystal_sphere.proceed", "{\"actor\":\"p0\",\"data\":{}}")
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
        Assert.Equal(
            ["run.start", "map.choose", "rest.choose", "event.crystal_sphere.set_tool", "event.crystal_sphere.click", "event.crystal_sphere.proceed"],
            script.Entries.Select(entry => entry.Kind));
        Assert.Equal(RunResult.Incomplete, script.Result);
        Assert.False(script.Entries[0].Payload.GetProperty("data").TryGetProperty("source_snapshot", out _));

        DecisionScript parsed = DecisionScript.FromRecord(script);
        Assert.Equal(5, parsed.Decisions.Count);
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
    public void ResumedRecordingIsRefusedInsteadOfConcatenatingBothTimelines()
    {
        RunRecord recording = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", RecordedRunStart)
            .AppendJson("map.choose", TestRecordFactory.ScriptMapChoose)
            .AppendJson("run.resume", "{\"actor\":null,\"data\":{}}")
            .AppendJson("map.choose", TestRecordFactory.ScriptMapChoose)
            .Build();

        Sts2PgnValidationException exception =
            Assert.Throws<Sts2PgnValidationException>(() => ScriptConverter.FromRecording(recording, "x/1"));
        ValidationIssue issue = Assert.Single(
            exception.ValidationResult.Errors,
            issue => issue.Code == "script_resume_not_supported");
        Assert.Equal("sequence:3", issue.Location);
        Assert.Contains("cannot restore the save checkpoint", issue.Message);
    }

    [Fact]
    public void InvalidProducerIsRejected() =>
        Assert.Throws<ArgumentException>(() => ScriptConverter.FromRecording(TestRecordFactory.CreateValid(), "Bad Producer"));
}
