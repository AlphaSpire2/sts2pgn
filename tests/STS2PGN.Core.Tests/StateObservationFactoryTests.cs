using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class StateObservationFactoryTests
{
    [Fact]
    public void FactoryCreatesCanonicalVersionedObservation()
    {
        using JsonDocument document = JsonDocument.Parse("{\"z\":2,\"a\":1}");

        StateObservationPayload observation = StateObservationFactory.Create(
            7,
            "run.start",
            document.RootElement);
        JsonElement json = JsonSerializer.SerializeToElement(observation);

        Assert.Equal(7, json.GetProperty("after").GetInt64());
        Assert.Equal("run.start", json.GetProperty("context").GetString());
        Assert.Equal(FormatConstants.CurrentStateProjectionVersion, json.GetProperty("projection_version").GetInt32());
        Assert.Equal(CanonicalJson.ComputeSha256(document.RootElement), json.GetProperty("digest").GetString());
        Assert.Equal(1, json.GetProperty("state").GetProperty("a").GetInt32());
    }

    [Fact]
    public void EquivalentPropertyOrderProducesSameObservationDigest()
    {
        using JsonDocument first = JsonDocument.Parse("{\"run\":{\"seed\":\"A\",\"act\":0},\"players\":[]}");
        using JsonDocument second = JsonDocument.Parse("{\"players\":[],\"run\":{\"act\":0,\"seed\":\"A\"}}");

        StateObservationPayload firstObservation = StateObservationFactory.Create(1, "run.start", first.RootElement);
        StateObservationPayload secondObservation = StateObservationFactory.Create(1, "run.start", second.RootElement);

        Assert.Equal(firstObservation.Digest, secondObservation.Digest);
    }

    [Fact]
    public void FactoryRejectsNonObjectState()
    {
        using JsonDocument document = JsonDocument.Parse("[]");

        Assert.Throws<ArgumentException>(() => StateObservationFactory.Create(1, "run.start", document.RootElement));
    }
}
