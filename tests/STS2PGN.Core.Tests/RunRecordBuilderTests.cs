using System.Text.Json;
using STS2PGN.Model;

namespace STS2PGN.Tests;

public sealed class RunRecordBuilderTests
{
    [Fact]
    public void BuilderAssignsMonotonicSequenceNumbers()
    {
        RunRecord record = new RunRecordBuilder()
            .AddTag("Format", "STS2PGN")
            .AppendJson("run.start", "{}")
            .AppendJson("future.action", "{\"value\":1}")
            .Build(RunResult.Victory);

        Assert.Equal([1L, 2L], record.Entries.Select(entry => entry.Sequence));
        Assert.Equal("future.action", record.Entries[1].Kind);
        Assert.Equal(1, record.Entries[1].Payload.GetProperty("value").GetInt32());
        Assert.Equal(RunResult.Victory, record.Result);
    }

    [Fact]
    public void SetTagReplacesDuplicatesWithoutChangingFirstPosition()
    {
        RunRecord record = new RunRecordBuilder()
            .AddTag("Format", "old")
            .AddTag("Unknown", "preserved")
            .AddTag("Format", "duplicate")
            .SetTag("Format", "STS2PGN")
            .AppendJson("run.start", "{}")
            .Build();

        Assert.Equal(
            [new TagPair("Format", "STS2PGN"), new TagPair("Unknown", "preserved")],
            record.Metadata.Tags);
    }

    [Fact]
    public void EntriesOwnTheirJsonAfterSourceDocumentIsDisposed()
    {
        RunRecordEntry entry;
        using (JsonDocument source = JsonDocument.Parse("{\"nested\":{\"value\":7}}"))
        {
            entry = new RunRecordEntry(1, "run.start", source.RootElement);
        }

        Assert.Equal(7, entry.Payload.GetProperty("nested").GetProperty("value").GetInt32());
    }

    [Fact]
    public void EntryRejectsNonObjectPayload()
    {
        using JsonDocument source = JsonDocument.Parse("[1,2,3]");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new RunRecordEntry(1, "future.action", source.RootElement));

        Assert.Contains("JSON object", exception.Message);
    }

    [Theory]
    [InlineData(RunResult.Incomplete, "*")]
    [InlineData(RunResult.Victory, "VICTORY")]
    [InlineData(RunResult.Defeat, "DEFEAT")]
    [InlineData(RunResult.Abandoned, "ABANDONED")]
    [InlineData(RunResult.Diverged, "DIVERGED")]
    public void ResultTokensRoundTrip(RunResult result, string token)
    {
        Assert.Equal(token, result.ToToken());
        Assert.True(RunResultExtensions.TryParseToken(token, out RunResult parsed));
        Assert.Equal(result, parsed);
    }
}
