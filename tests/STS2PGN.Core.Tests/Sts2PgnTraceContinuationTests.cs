using System.Text;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class Sts2PgnTraceContinuationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sts2pgn-continuation-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ContinuationAppendsWithContiguousSequencesAndFinalizes()
    {
        string path = WriteIncompleteTrace("1 run.start {\"data\":{}}\n2 combat.end_turn {\"actor\":\"p0\",\"data\":{\"turn\":1}}\n");

        using (Sts2PgnTraceContinuation continuation = Sts2PgnTraceContinuation.Open(path))
        {
            Assert.False(continuation.RemovedTruncatedFinalLine);
            Assert.Equal(2, continuation.Prefix.Entries.Count);
            Assert.Equal(3, continuation.Writer.NextSequence);
            continuation.Writer.AppendJson("run.resume", "{\"data\":{\"reloads\":1}}");
            continuation.Writer.Complete(RunResult.Defeat);
        }

        RunRecord record = Sts2PgnParser.Parse(
            File.ReadAllText(path),
            new Sts2PgnParseOptions { RecoverTruncatedFinalLine = false });
        Assert.Equal(RunResult.Defeat, record.Result);
        Assert.Equal([1L, 2L, 3L], record.Entries.Select(entry => entry.Sequence));
        Assert.Equal("run.resume", record.Entries[^1].Kind);
    }

    [Fact]
    public void ContinuationRemovesCrashTruncatedFinalLine()
    {
        string path = WriteIncompleteTrace(
            "1 run.start {\"data\":{}}\n2 combat.end_turn {\"actor\":\"p0\",\"data\":{\"turn\":1}}\n3 combat.play_card {\"data\":{\"tru");

        using (Sts2PgnTraceContinuation continuation = Sts2PgnTraceContinuation.Open(path))
        {
            Assert.True(continuation.RemovedTruncatedFinalLine);
            Assert.Equal(2, continuation.Prefix.Entries.Count);
            Assert.Equal(3, continuation.Writer.NextSequence);
            continuation.Writer.AppendJson("run.resume", "{\"data\":{}}");
        }

        RunRecord record = Sts2PgnParser.Parse(
            File.ReadAllText(path),
            new Sts2PgnParseOptions { RecoverTruncatedFinalLine = false });
        Assert.Equal([1L, 2L, 3L], record.Entries.Select(entry => entry.Sequence));
        Assert.Equal("run.resume", record.Entries[^1].Kind);
    }

    [Fact]
    public void ContinuationTerminatesCompleteFinalRecordThatLostItsNewline()
    {
        string path = WriteIncompleteTrace(
            "1 run.start {\"data\":{}}\n2 combat.end_turn {\"actor\":\"p0\",\"data\":{\"turn\":1}}");

        using (Sts2PgnTraceContinuation continuation = Sts2PgnTraceContinuation.Open(path))
        {
            Assert.False(continuation.RemovedTruncatedFinalLine);
            Assert.Equal(2, continuation.Prefix.Entries.Count);
            continuation.Writer.AppendJson("run.resume", "{\"data\":{}}");
        }

        RunRecord record = Sts2PgnParser.Parse(
            File.ReadAllText(path),
            new Sts2PgnParseOptions { RecoverTruncatedFinalLine = false });
        Assert.Equal([1L, 2L, 3L], record.Entries.Select(entry => entry.Sequence));
    }

    [Fact]
    public void ContinuationRejectsFinalizedTrace()
    {
        string path = WriteIncompleteTrace("1 run.start {\"data\":{}}\nDEFEAT\n");

        Assert.Throws<InvalidDataException>(() => Sts2PgnTraceContinuation.Open(path));
    }

    [Fact]
    public void ContinuationRejectsTraceWithoutCompleteRunStart()
    {
        string path = WriteIncompleteTrace("1 run.start {\"data\":{\"tru");

        Assert.Throws<InvalidDataException>(() => Sts2PgnTraceContinuation.Open(path));
    }

    [Fact]
    public void ContinuationPropagatesMalformedMidFileRecords()
    {
        string path = WriteIncompleteTrace("1 run.start {\"data\":}\n2 combat.end_turn {\"actor\":\"p0\",\"data\":{}}\n");

        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnTraceContinuation.Open(path));
    }

    private string WriteIncompleteTrace(string recordLines)
    {
        StringWriter header = new();
        using (Sts2PgnStreamWriter headerWriter = new(header, TestRecordFactory.CreateBuilder().Build().Metadata, leaveOpen: true))
        {
        }

        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.sts2pgn");
        File.WriteAllText(path, header.ToString() + recordLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
