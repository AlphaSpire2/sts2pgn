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
}
