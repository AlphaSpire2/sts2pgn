using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class Sts2PgnParserWriterTests
{
    [Fact]
    public void WriterAndParserRoundTripUnknownDataAndEscapedTags()
    {
        const string note = "quoted \"value\" \\ path\nnext\t雪";
        RunRecord source = TestRecordFactory.CreateBuilder()
            .AddTag("FutureTag", note)
            .AppendJson("run.start", "{\"z\":2,\"a\":1.0}")
            .AppendJson("future.action", "{\"unknown\":{\"enabled\":true}}")
            .Build(RunResult.Victory);

        string text = Sts2PgnWriter.Write(source);
        RunRecord parsed = Sts2PgnParser.Parse(text);

        Assert.Equal(note, parsed.Metadata.GetValue("FutureTag"));
        Assert.Equal("future.action", parsed.Entries[1].Kind);
        Assert.True(parsed.Entries[1].Payload.GetProperty("unknown").GetProperty("enabled").GetBoolean());
        Assert.Equal(RunResult.Victory, parsed.Result);
        Assert.Contains("1 run.start {\"a\":1,\"z\":2}\n", text);
        Assert.True(Sts2PgnValidator.Validate(parsed).IsValid);
    }

    [Fact]
    public void ParserAcceptsCrLf()
    {
        string text = Sts2PgnWriter.Write(TestRecordFactory.CreateValid()).Replace("\n", "\r\n", StringComparison.Ordinal);

        RunRecord parsed = Sts2PgnParser.Parse(text);

        Assert.Single(parsed.Entries);
        Assert.Equal(RunResult.Incomplete, parsed.Result);
    }

    [Fact]
    public void ParserRecoversTruncatedUnterminatedFinalJsonLine()
    {
        string prefix = Sts2PgnWriter.Write(
            TestRecordFactory.CreateValid(),
            new Sts2PgnWriterOptions { IncludeTerminalResult = false });
        string truncated = prefix + "2 future.action {\"nested\":{";

        RunRecord parsed = Sts2PgnParser.Parse(truncated);

        Assert.Single(parsed.Entries);
        Assert.True(parsed.RecoveredTruncatedFinalLine);
        Assert.Equal(RunResult.Incomplete, parsed.Result);
        Assert.Contains(
            Sts2PgnValidator.Validate(parsed).Warnings,
            issue => issue.Code == "recovered_truncated_line");
    }

    [Fact]
    public void ParserDoesNotRecoverCompleteMalformedLine()
    {
        string prefix = Sts2PgnWriter.Write(
            TestRecordFactory.CreateValid(),
            new Sts2PgnWriterOptions { IncludeTerminalResult = false });
        string malformed = prefix + "2 future.action {\"nested\":}\n";

        Sts2PgnParseException exception = Assert.Throws<Sts2PgnParseException>(
            () => Sts2PgnParser.Parse(malformed));

        Assert.Contains("Invalid JSON payload", exception.Message);
    }

    [Fact]
    public void RecoveryCanBeDisabled()
    {
        string prefix = Sts2PgnWriter.Write(
            TestRecordFactory.CreateValid(),
            new Sts2PgnWriterOptions { IncludeTerminalResult = false });
        string truncated = prefix + "2 future.action {";

        Assert.Throws<Sts2PgnParseException>(
            () => Sts2PgnParser.Parse(
                truncated,
                new Sts2PgnParseOptions { RecoverTruncatedFinalLine = false }));
    }

    [Fact]
    public void ParserRecoversTruncatedTerminalToken()
    {
        string prefix = Sts2PgnWriter.Write(
            TestRecordFactory.CreateValid(),
            new Sts2PgnWriterOptions { IncludeTerminalResult = false });

        RunRecord parsed = Sts2PgnParser.Parse(prefix + "VICT");

        Assert.True(parsed.RecoveredTruncatedFinalLine);
        Assert.Equal(RunResult.Incomplete, parsed.Result);
    }

    [Fact]
    public void ParserRejectsContentAfterTerminalResult()
    {
        string valid = Sts2PgnWriter.Write(TestRecordFactory.CreateValid(RunResult.Victory));

        Sts2PgnParseException exception = Assert.Throws<Sts2PgnParseException>(
            () => Sts2PgnParser.Parse(valid + "2 future.action {}\n"));

        Assert.Contains("after the terminal result", exception.Message);
    }

    [Theory]
    [InlineData("[BrokenTag]\n\n1 run.start {}\n*\n", "Invalid tag name")]
    [InlineData("[Format \"STS2PGN\"]\n1 run.start {}\n*\n", "blank line")]
    [InlineData("[Format \"STS2PGN\"]\n\nnot-a-record\n", "sequence kind")]
    [InlineData("[Format \"STS2PGN\"]\n\n1 INVALID {}\n", "record kind")]
    public void ParserReportsStructuralErrorsWithLineNumber(string text, string expectedMessage)
    {
        Sts2PgnParseException exception = Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(text));

        Assert.True(exception.LineNumber > 0);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriterRejectsInvalidRecordByDefault()
    {
        RunRecord invalid = new RunRecordBuilder()
            .AddTag("Format", "STS2PGN")
            .AppendJson("run.start", "{}")
            .Build();

        Sts2PgnValidationException exception = Assert.Throws<Sts2PgnValidationException>(
            () => Sts2PgnWriter.Write(invalid));

        Assert.Contains(exception.ValidationResult.Errors, issue => issue.Code == "missing_tag");
    }
}
