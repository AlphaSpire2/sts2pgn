using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Scripting;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class DecisionScriptTests
{
    [Fact]
    public void FromRecordExtractsIdentityUnlocksActsAndDecisions()
    {
        DecisionScript script = DecisionScript.FromRecord(TestRecordFactory.CreateValidScript());

        Assert.Equal("spirefish/0.1.0", script.Identity.Producer);
        Assert.Equal("TESTSEED", script.Identity.Seed);
        Assert.Equal("CUSTOM", script.Identity.Mode);
        Assert.Equal(0, script.Identity.Ascension);
        Assert.Equal("IRONCLAD", script.Identity.CharacterModel);
        Assert.Equal("TEST", script.Identity.GameVersion);
        Assert.Equal("UNKNOWN", script.Identity.GameCommit);
        Assert.Equal("0", script.Identity.ModelIdHash);

        Assert.Equal(3, script.Unlocks.NumberOfRuns);
        Assert.Equal(["EPOCH_B", "EPOCH_A"], script.Unlocks.Epochs);
        Assert.Equal(["JAW_WORM"], script.Unlocks.EncountersSeen);
        Assert.Equal(["OVERGROWTH", "ACT_2", "ACT_3"], script.ExpectedActModelIds);

        ScriptDecision decision = Assert.Single(script.Decisions);
        Assert.Equal(0, decision.Ordinal);
        Assert.Equal(2, decision.Sequence);
        Assert.Equal("map.choose", decision.Kind);
        Assert.Equal("p0", decision.Actor);
        Assert.False(decision.IsChoice);
        Assert.True(ScriptDecisionReader.TryGetInt(decision.Data, "col", out int col));
        Assert.Equal(2, col);
    }

    [Fact]
    public void FromRecordSkipsIgnoredKindsAndKeepsOrdinals()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("map.choose", TestRecordFactory.ScriptMapChoose)
            .AppendJson("room.enter", "{\"data\":{}}")
            .AppendJson("rest.choose", "{\"actor\":\"p0\",\"data\":{\"option_id\":\"rest\"}}")
            .AppendJson("rest.resolve", "{\"actor\":\"p0\",\"data\":{\"action_sequence\":4,\"success\":true}}")
            .Build();

        DecisionScript script = DecisionScript.FromRecord(record);

        Assert.Equal(["map.choose", "rest.choose"], script.Decisions.Select(decision => decision.Kind));
        Assert.Equal([0, 1], script.Decisions.Select(decision => decision.Ordinal));
        Assert.Equal([2L, 4L], script.Decisions.Select(decision => decision.Sequence));
    }

    [Fact]
    public void FromRecordRejectsRecordings()
    {
        Sts2PgnValidationException exception =
            Assert.Throws<Sts2PgnValidationException>(() => DecisionScript.FromRecord(TestRecordFactory.CreateValid()));

        Assert.Contains(exception.ValidationResult.Errors, issue => issue.Code == "script_missing_producer");
    }

    [Fact]
    public void LoadRoundTripsThroughTheWriter()
    {
        string text = Sts2PgnWriter.Write(TestRecordFactory.CreateValidScript());

        DecisionScript script = DecisionScript.Load(new StringReader(text));

        Assert.Single(script.Decisions);
        Assert.True(ScriptProfile.IsScript(script.Source.Metadata));
    }

    [Fact]
    public void UnlocksMatchIsOrderInsensitiveAndExplainsDifferences()
    {
        const string Vantom = "ENCOUNTER.VANTOM_BOSS";
        const string Queen = "ENCOUNTER.QUEEN_BOSS";
        ScriptUnlocks expected = new(["B", "A"], [Vantom], 2);

        Assert.True(expected.Matches(new ScriptUnlocks(["A", "B"], [Vantom], 2), out string difference, out IReadOnlyList<string> warnings));
        Assert.Equal(string.Empty, difference);
        Assert.Empty(warnings);

        Assert.False(expected.Matches(new ScriptUnlocks(["A"], [Vantom], 2), out difference));
        Assert.StartsWith("epochs:", difference);
        Assert.Contains("missing live [B]", difference);

        Assert.False(expected.Matches(new ScriptUnlocks(["A", "B"], [Vantom, Queen], 2), out difference));
        Assert.StartsWith("encounters_seen (discovery bosses):", difference);
        Assert.Contains($"extra live [{Queen}]", difference);
        Assert.False(expected.Matches(new ScriptUnlocks(["A", "B"], [], 2), out difference));
        Assert.Contains($"missing live [{Vantom}]", difference);

        Assert.False(expected.Matches(new ScriptUnlocks(["A", "B"], [Vantom], 0), out difference));
        Assert.StartsWith("number_of_runs:", difference);
        Assert.False(new ScriptUnlocks([], [], 0).Matches(new ScriptUnlocks([], [], 1), out _));
    }

    [Fact]
    public void UnlocksMatchTreatsGenerationInertDifferencesAsWarnings()
    {
        const string Vantom = "ENCOUNTER.VANTOM_BOSS";
        ScriptUnlocks expected = new(["A"], [Vantom, "ENCOUNTER.JAW_WORM"], 2);

        Assert.True(
            expected.Matches(new ScriptUnlocks(["A"], [Vantom, "ENCOUNTER.CULTIST"], 57), out string difference, out IReadOnlyList<string> warnings),
            "run-count drift past the first run and non-discovery encounters are not read during generation");
        Assert.Equal(string.Empty, difference);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.StartsWith("number_of_runs: script 2, live 57", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.StartsWith("encounters_seen:", StringComparison.Ordinal)
            && warning.Contains("missing live [ENCOUNTER.JAW_WORM], extra live [ENCOUNTER.CULTIST]", StringComparison.Ordinal));

        Assert.Equal(12, ScriptUnlocks.DiscoveryBosses.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ScriptUnlocks.DiscoveryBosses, id => Assert.EndsWith("_BOSS", id));
    }

    [Fact]
    public void ReaderExtractsOffersSelectionsAndTargets()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"indexes\":[2,0],\"offered\":{\"context\":\"card_grid\",\"options\":[{\"index\":0}]},"
            + "\"cards\":[{\"deck_index\":3,\"fingerprint\":{\"model_id\":\"STRIKE\"}},{\"combat_card_index\":9,\"fingerprint\":{\"model_id\":\"DEFEND\"}}],"
            + "\"target_id\":7,\"target\":{\"combat_id\":7,\"model_id\":\"JAW_WORM\"}}");
        JsonElement data = document.RootElement;

        Assert.True(ScriptDecisionReader.TryGetOffered(data, out string context, out JsonElement options));
        Assert.Equal("card_grid", context);
        Assert.Equal(1, options.GetArrayLength());
        Assert.Equal([2, 0], ScriptDecisionReader.GetSelectedIndexes(data));

        IReadOnlyList<ScriptCardSelection> cards = ScriptDecisionReader.GetCardSelections(data);
        Assert.Equal(2, cards.Count);
        Assert.Equal(3, cards[0].DeckIndex);
        Assert.Null(cards[0].CombatCardIndex);
        Assert.Equal(9, cards[1].CombatCardIndex);
        Assert.Equal("DEFEND", cards[1].Fingerprint.GetProperty("model_id").GetString());

        ScriptTarget? target = ScriptDecisionReader.GetTarget(data);
        Assert.NotNull(target);
        Assert.Equal(7u, target.CombatId);
        Assert.Equal("JAW_WORM", target.ModelId);
        Assert.Null(ScriptDecisionReader.GetTarget(default));
    }

    [Theory]
    [InlineData("spirefish/0.1.0", true, "spirefish", "0.1.0")]
    [InlineData("sts2-sim_2/2026.08.20+abc", true, "sts2-sim_2", "2026.08.20+abc")]
    [InlineData("spirefish", false, "", "")]
    [InlineData("Spirefish/1", false, "", "")]
    public void ProducerParsing(string value, bool expected, string name, string version)
    {
        Assert.Equal(expected, ScriptProfile.TryParseProducer(value, out string parsedName, out string parsedVersion));
        Assert.Equal(name, parsedName);
        Assert.Equal(version, parsedVersion);
    }
}
