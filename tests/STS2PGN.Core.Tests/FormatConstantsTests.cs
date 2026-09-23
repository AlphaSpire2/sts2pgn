namespace STS2PGN.Tests;

public sealed class FormatConstantsTests
{
    [Fact]
    public void VersionTwoRetainsProjectionAndExtensionAndSupportsVersionOne()
    {
        Assert.Equal("STS2PGN", FormatConstants.Name);
        Assert.Equal(2, FormatConstants.CurrentVersion);
        Assert.True(FormatConstants.IsSupportedVersion(1));
        Assert.True(FormatConstants.IsSupportedVersion(2));
        Assert.False(FormatConstants.IsSupportedVersion(3));
        Assert.Equal(1, FormatConstants.CurrentStateProjectionVersion);
        Assert.Equal(".sts2pgn", FormatConstants.FileExtension);
    }
}
