using System.Text.Json;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void CanonicalizationSortsObjectsNormalizesNumbersAndPreservesArrayOrder()
    {
        using JsonDocument document = JsonDocument.Parse("{\"z\":[3,2,1],\"b\":1.00,\"a\":-2,\"nested\":{\"y\":true,\"x\":null}}");

        string canonical = CanonicalJson.Serialize(document.RootElement);

        Assert.Equal("{\"a\":-2,\"b\":1,\"nested\":{\"x\":null,\"y\":true},\"z\":[3,2,1]}", canonical);
    }

    [Fact]
    public void EquivalentPropertyOrderProducesSameDigest()
    {
        using JsonDocument first = JsonDocument.Parse("{\"b\":2,\"a\":1.0}");
        using JsonDocument second = JsonDocument.Parse("{\"a\":1,\"b\":2}");

        string firstDigest = CanonicalJson.ComputeSha256(first.RootElement);
        string secondDigest = CanonicalJson.ComputeSha256(second.RootElement);

        Assert.Equal(firstDigest, secondDigest);
        Assert.Matches("^sha256:[0-9a-f]{64}$", firstDigest);
    }

    [Fact]
    public void DuplicatePropertiesCannotBeCanonicalized()
    {
        using JsonDocument document = JsonDocument.Parse("{\"same\":1,\"same\":2}");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => CanonicalJson.Serialize(document.RootElement));

        Assert.Contains("Duplicate JSON property", exception.Message);
    }
}
