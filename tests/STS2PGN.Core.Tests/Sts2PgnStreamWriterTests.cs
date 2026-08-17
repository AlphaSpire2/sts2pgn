using System.Text;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class Sts2PgnStreamWriterTests
{
    [Fact]
    public void AppendFlushesEveryCompleteRecordAndProducesAValidTrace()
    {
        TrackingWriter output = new();
        using (Sts2PgnStreamWriter writer = new(output, TestRecordFactory.CreateBuilder().Build().Metadata, leaveOpen: true))
        {
            Assert.Equal(1, output.FlushCount);
            writer.AppendJson("run.start", "{\"extensions\":{},\"data\":{}}");
            Assert.Equal(2, output.FlushCount);
            writer.AppendJson("combat.end_turn", "{\"data\":{\"turn\":1},\"extensions\":{}}");
            Assert.Equal(3, output.FlushCount);
            writer.Complete(RunResult.Victory);
            Assert.Equal(4, output.FlushCount);
        }

        RunRecord parsed = Sts2PgnParser.Parse(output.ToString());
        Assert.Equal(RunResult.Victory, parsed.Result);
        Assert.Equal([1L, 2L], parsed.Entries.Select(entry => entry.Sequence));
        Assert.EndsWith("\n", output.ToString(), StringComparison.Ordinal);
        Assert.False(output.ToString().EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposingIncompleteWriterLeavesRecoverablePrefixWithoutTerminalMarker()
    {
        TrackingWriter output = new();
        using (Sts2PgnStreamWriter writer = new(output, TestRecordFactory.CreateBuilder().Build().Metadata, leaveOpen: true))
        {
            writer.AppendJson("run.start", "{\"data\":{},\"extensions\":{}}");
        }

        RunRecord parsed = Sts2PgnParser.Parse(output.ToString());
        Assert.Equal(RunResult.Incomplete, parsed.Result);
        Assert.Single(parsed.Entries);
        Assert.EndsWith("\n", output.ToString(), StringComparison.Ordinal);
        Assert.False(output.ToString().EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletedWriterRejectsFurtherRecords()
    {
        using StringWriter output = new();
        using Sts2PgnStreamWriter writer = new(output, TestRecordFactory.CreateBuilder().Build().Metadata, leaveOpen: true);
        writer.AppendJson("run.start", "{\"data\":{},\"extensions\":{}}");
        writer.Complete(RunResult.Defeat);

        Assert.Throws<InvalidOperationException>(() => writer.AppendJson("room.exit", "{}"));
        Assert.Throws<InvalidOperationException>(() => writer.Complete(RunResult.Abandoned));
    }

    [Fact]
    public void CompleteRejectsIncompleteResult()
    {
        using StringWriter output = new();
        using Sts2PgnStreamWriter writer = new(output, TestRecordFactory.CreateBuilder().Build().Metadata, leaveOpen: true);

        Assert.Throws<ArgumentException>(() => writer.Complete(RunResult.Incomplete));
    }

    private sealed class TrackingWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public TrackingWriter()
            : base(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture)
        {
        }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }
}
