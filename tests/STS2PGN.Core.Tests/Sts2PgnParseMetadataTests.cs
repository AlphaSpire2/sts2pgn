using STS2PGN.Model;
using STS2PGN.Parsing;

namespace STS2PGN.Tests;

public sealed class Sts2PgnParseMetadataTests
{
    [Fact]
    public void ParseMetadataReadsOnlyTheTagSection()
    {
        using StringReader reader = new(
            "[Format \"STS2PGN\"]\n[Seed \"ABC\\\"123\"]\n\n1 run.start {\"data\":{}}\n");

        RunMetadata metadata = Sts2PgnParser.ParseMetadata(reader);

        Assert.Equal("STS2PGN", metadata.GetValue("Format"));
        Assert.Equal("ABC\"123", metadata.GetValue("Seed"));
        Assert.Equal('1', (char)reader.Read());
    }

    [Fact]
    public void ParseMetadataRejectsMissingBlankSeparator()
    {
        using StringReader missingSeparator = new("[Format \"STS2PGN\"]\n1 run.start {}\n");
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.ParseMetadata(missingSeparator));

        using StringReader headerOnly = new("[Format \"STS2PGN\"]\n");
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.ParseMetadata(headerOnly));
    }

    [Fact]
    public void ParseMetadataRejectsEmptyInput()
    {
        using StringReader reader = new("");
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.ParseMetadata(reader));
    }
}
