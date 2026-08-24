using System.Text.Json;
using STS2PGN.Scripting;

namespace STS2PGN.Tests;

public sealed class ScriptDecisionReaderTests
{
    [Theory]
    [InlineData("[]", "[-1]", true)]
    [InlineData("[-1]", "[]", true)]
    [InlineData("[1,2]", "[1,2]", true)]
    [InlineData("[]", "[0]", false)]
    [InlineData("[-1]", "[0]", false)]
    [InlineData("[]", "[\"not-an-index\"]", false)]
    public void RecordedCancelSentinelMatchesTheScriptsEmptySelection(
        string expectedJson,
        string actualJson,
        bool matches)
    {
        using JsonDocument expected = JsonDocument.Parse(expectedJson);
        using JsonDocument actual = JsonDocument.Parse(actualJson);

        Assert.Equal(
            matches,
            ScriptDecisionReader.SelectedIndexesMatch(expected.RootElement, actual.RootElement));
    }
}
