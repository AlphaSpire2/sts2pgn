using STS2PGN.Model;

namespace STS2PGN.Tests;

internal static class TestRecordFactory
{
    public static RunRecordBuilder CreateBuilder(string resultTag = "*") =>
        new RunRecordBuilder()
            .AddTag("Format", "STS2PGN")
            .AddTag("FormatVersion", "1")
            .AddTag("RecorderVersion", "0.2.0")
            .AddTag("RunId", "00000000-0000-0000-0000-000000000001")
            .AddTag("GameVersion", "TEST")
            .AddTag("GameCommit", "UNKNOWN")
            .AddTag("ModelIdHash", "0")
            .AddTag("Seed", "TESTSEED")
            .AddTag("Mode", "STANDARD")
            .AddTag("Ascension", "0")
            .AddTag("Players", "[{\"slot\":\"p0\",\"character\":\"IRONCLAD\"}]")
            .AddTag("StartedAt", "2026-01-01T00:00:00Z")
            .AddTag("Result", resultTag);

    public static RunRecord CreateValid(RunResult result = RunResult.Incomplete) =>
        CreateBuilder()
            .AppendJson("run.start", "{\"data\":{},\"extensions\":{}}")
            .Build(result);

    /// <summary>Header for the decision-script profile: Producer instead of RecorderVersion, Mode CUSTOM.</summary>
    public static RunRecordBuilder CreateScriptBuilder() =>
        new RunRecordBuilder()
            .AddTag("Format", "STS2PGN")
            .AddTag("FormatVersion", "1")
            .AddTag("Producer", "spirefish/0.1.0")
            .AddTag("Seed", "TESTSEED")
            .AddTag("Mode", "CUSTOM")
            .AddTag("Ascension", "0")
            .AddTag("Players", "[{\"slot\":\"p0\",\"character\":\"IRONCLAD\"}]")
            .AddTag("GameVersion", "TEST")
            .AddTag("GameCommit", "UNKNOWN")
            .AddTag("ModelIdHash", "0");

    public const string ScriptRunStart =
        "{\"data\":{\"state\":{\"scope\":\"run_start.core\","
        + "\"acts\":[{\"index\":0,\"model_id\":\"OVERGROWTH\"},{\"index\":1,\"model_id\":\"ACT_2\"},{\"index\":2,\"model_id\":\"ACT_3\"}],"
        + "\"players\":[{\"slot\":\"p0\",\"character_model\":\"IRONCLAD\","
        + "\"unlocks\":{\"epochs\":[\"EPOCH_B\",\"EPOCH_A\"],\"encounters_seen\":[\"JAW_WORM\"],\"number_of_runs\":3}}]}}}";

    public const string ScriptMapChoose = "{\"actor\":\"p0\",\"data\":{\"col\":2,\"row\":0}}";

    /// <summary>A minimal valid script: run.start with unlocks and acts, then one map choice.</summary>
    public static RunRecord CreateValidScript() =>
        CreateScriptBuilder()
            .AppendJson("run.start", ScriptRunStart)
            .AppendJson("map.choose", ScriptMapChoose)
            .Build();
}
