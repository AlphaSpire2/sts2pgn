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

    /// <summary>A card offer as the simulator writes one into a decision script.</summary>
    private const string ScriptedOffer = @"
        {
          ""reward_type"": ""card"",
          ""model_id"": null,
          ""special_card"": null,
          ""gold_amount"": 0,
          ""was_gold_stolen_back"": false,
          ""source"": ""other"",
          ""rarity_odds"": ""uniform"",
          ""card_pools"": [],
          ""option_count"": 3,
          ""custom_description_encounter_source_id"": null,
          ""offered_cards"": [{ ""model_id"": ""CARD.INFERNO"", ""upgrade_level"": 1 }]
        }";

    /// <summary>The same offer, as the recorder writes one whose save schema threw.</summary>
    private const string RefusedOffer = @"
        {
          ""reward_type"": ""card"",
          ""model_id"": null,
          ""special_card"": null,
          ""gold_amount"": null,
          ""was_gold_stolen_back"": null,
          ""source"": null,
          ""rarity_odds"": null,
          ""card_pools"": null,
          ""option_count"": null,
          ""custom_description_encounter_source_id"": null,
          ""offered_cards"": [{ ""model_id"": ""CARD.INFERNO"", ""upgrade_level"": 1 }]
        }";

    private static bool RewardMatches(string expectedJson, string liveJson)
    {
        using JsonDocument expected = JsonDocument.Parse(expectedJson);
        using JsonDocument live = JsonDocument.Parse(liveJson);
        return ScriptDecisionReader.RewardFingerprintMatches(expected.RootElement, live.RootElement);
    }

    [Fact]
    public void ARewardWhoseSaveBlockTheGameRefusedIsNamedByItsCards()
    {
        Assert.True(RewardMatches(ScriptedOffer, RefusedOffer));
        Assert.True(RewardMatches(RefusedOffer, RefusedOffer));
    }

    [Fact]
    public void ARefusedRewardIsStillToldFromADifferentOffer()
    {
        string otherCards = RefusedOffer.Replace("CARD.INFERNO", "CARD.STAMPEDE", StringComparison.Ordinal);
        Assert.False(RewardMatches(ScriptedOffer, otherCards));

        string otherType = RefusedOffer.Replace("\"card\"", "\"relic\"", StringComparison.Ordinal);
        Assert.False(RewardMatches(ScriptedOffer, otherType));
    }

    [Fact]
    public void ARewardTheGameCouldWriteIsComparedWhole()
    {
        string oneFieldOff = ScriptedOffer.Replace("\"option_count\": 3", "\"option_count\": 4", StringComparison.Ordinal);
        Assert.False(RewardMatches(ScriptedOffer, oneFieldOff));

        // One field of the block standing alone as nothing is not the block being
        // unwritten, so the comparison stays whole and the difference stands.
        string oneFieldNull = ScriptedOffer.Replace("\"option_count\": 3", "\"option_count\": null", StringComparison.Ordinal);
        Assert.False(RewardMatches(ScriptedOffer, oneFieldNull));
    }
}
