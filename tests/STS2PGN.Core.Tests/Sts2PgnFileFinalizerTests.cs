using System.Text;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class Sts2PgnFileFinalizerTests
{
    [Fact]
    public void FinalizeResultHeaderPreservesTraceAndUpdatesResult()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sts2pgn-{Guid.NewGuid():N}.sts2pgn");
        try
        {
            using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (StreamWriter text = new(file, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true))
            using (Sts2PgnStreamWriter writer = new(
                text,
                TestRecordFactory.CreateBuilder().Build().Metadata))
            {
                writer.AppendJson("run.start", "{\"data\":{},\"extensions\":{}}");
                writer.Complete(RunResult.Victory);
            }

            Sts2PgnFileFinalizer.FinalizeResultHeader(path, RunResult.Victory);

            string finalized = File.ReadAllText(path);
            Assert.Contains("[Result \"VICTORY\"]", finalized, StringComparison.Ordinal);
            Assert.DoesNotContain("[Result \"*\"]", finalized, StringComparison.Ordinal);
            Assert.EndsWith("VICTORY\n", finalized, StringComparison.Ordinal);
            Assert.False(finalized.EndsWith("\n\n", StringComparison.Ordinal));
            Assert.Equal(RunResult.Victory, Sts2PgnParser.Parse(finalized).Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FinalizeResultHeaderRejectsIncompleteResult()
    {
        Assert.Throws<ArgumentException>(() =>
            Sts2PgnFileFinalizer.FinalizeResultHeader("unused.sts2pgn", RunResult.Incomplete));
    }
}
