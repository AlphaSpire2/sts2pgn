namespace STS2PGN.Tests;

public sealed class FormatConstantsTests
{
    [Fact]
    public void VersionOneUsesSts2PgnExtension()
    {
        Assert.Equal("STS2PGN", FormatConstants.Name);
        Assert.Equal(1, FormatConstants.CurrentVersion);
        Assert.Equal(".sts2pgn", FormatConstants.FileExtension);
    }
}
