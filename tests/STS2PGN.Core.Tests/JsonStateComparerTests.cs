using System.Text.Json;
using STS2PGN.Replay;

namespace STS2PGN.Tests;

public sealed class JsonStateComparerTests
{
    [Fact]
    public void EquivalentObjectsAndNumbersMatchCanonically()
    {
        using JsonDocument expected = JsonDocument.Parse("{\"z\":1.0,\"a\":true}");
        using JsonDocument actual = JsonDocument.Parse("{\"a\":true,\"z\":1}");

        StateComparisonResult result = JsonStateComparer.Compare(expected.RootElement, actual.RootElement);

        Assert.True(result.IsMatch);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void PlayersAreComparedByStableSlot()
    {
        using JsonDocument expected = JsonDocument.Parse(
            "{\"players\":[{\"slot\":\"p0\",\"current_hp\":80},{\"slot\":\"p1\",\"current_hp\":70}]}");
        using JsonDocument actual = JsonDocument.Parse(
            "{\"players\":[{\"slot\":\"p1\",\"current_hp\":70},{\"slot\":\"p0\",\"current_hp\":79}]}");

        StateComparisonResult result = JsonStateComparer.Compare(expected.RootElement, actual.RootElement);

        StateDifference difference = Assert.Single(result.Differences);
        Assert.Equal("/players/p0/current_hp", difference.Path);
        Assert.Equal(80, difference.Expected!.Value.GetInt32());
        Assert.Equal(79, difference.Actual!.Value.GetInt32());
    }

    [Fact]
    public void MissingAndNullAreDistinct()
    {
        using JsonDocument expected = JsonDocument.Parse("{\"value\":null}");
        using JsonDocument actual = JsonDocument.Parse("{}");

        StateDifference difference = Assert.Single(
            JsonStateComparer.Compare(expected.RootElement, actual.RootElement).Differences);

        Assert.Equal("/value", difference.Path);
        Assert.Equal(JsonValueKind.Null, difference.Expected!.Value.ValueKind);
        Assert.Null(difference.Actual);
        Assert.True(difference.ExpectedPresent);
        Assert.False(difference.ActualPresent);
    }

    [Fact]
    public void DifferencePathsEscapeJsonPointerSegments()
    {
        using JsonDocument expected = JsonDocument.Parse("{\"a/b~c\":1}");
        using JsonDocument actual = JsonDocument.Parse("{\"a/b~c\":2}");

        StateDifference difference = Assert.Single(
            JsonStateComparer.Compare(expected.RootElement, actual.RootElement).Differences);

        Assert.Equal("/a~1b~0c", difference.Path);
    }

    [Fact]
    public void OrderedArraysReportNumericIndexes()
    {
        using JsonDocument expected = JsonDocument.Parse("{\"deck\":[\"A\",\"B\"]}");
        using JsonDocument actual = JsonDocument.Parse("{\"deck\":[\"B\",\"A\"]}");

        StateComparisonResult result = JsonStateComparer.Compare(expected.RootElement, actual.RootElement);

        Assert.Equal(["/deck/0", "/deck/1"], result.Differences.Select(difference => difference.Path));
    }

    [Fact]
    public void DifferenceLimitReportsTruncation()
    {
        using JsonDocument expected = JsonDocument.Parse("{\"a\":1,\"b\":2,\"c\":3}");
        using JsonDocument actual = JsonDocument.Parse("{\"a\":4,\"b\":5,\"c\":6}");

        StateComparisonResult result = JsonStateComparer.Compare(
            expected.RootElement,
            actual.RootElement,
            maxDifferences: 2);

        Assert.Equal(2, result.Differences.Count);
        Assert.True(result.DifferencesTruncated);
    }
}
