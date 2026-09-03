using STS2PGN.Model;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class Sts2PgnValidatorScriptModeTests
{
    private const string PlayCard =
        "{\"actor\":\"p0\",\"data\":{\"combat_card_index\":4,\"card_model\":\"STRIKE\",\"card\":{\"model_id\":\"STRIKE\"},"
        + "\"target_id\":7,\"target\":{\"combat_id\":7,\"model_id\":\"JAW_WORM\"}}}";

    private const string CardChoice =
        "{\"actor\":\"p0\",\"data\":{\"choice_type\":\"deck_card\",\"cards\":[{\"deck_index\":1,\"fingerprint\":{\"model_id\":\"DEFEND\"}}]}}";

    private const string RewardChoice =
        "{\"actor\":\"p0\",\"data\":{\"indexes\":[1],\"offered\":{\"context\":\"card_reward\",\"options\":{"
        + "\"cards\":[{\"index\":0},{\"index\":1}],\"alternatives\":[{\"index\":2,\"option_id\":\"skip\"}]}}}}";

    private static Sts2PgnValidationResult ValidateScript(RunRecord record) =>
        Sts2PgnValidator.Validate(record, Sts2PgnValidationMode.Script);

    [Fact]
    public void ValidScriptHasNoErrorsInScriptMode()
    {
        Sts2PgnValidationResult result = ValidateScript(TestRecordFactory.CreateValidScript());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(issue => issue.Code)));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void DefaultValidateIsRecordingModeAndRejectsScripts()
    {
        Sts2PgnValidationResult result = Sts2PgnValidator.Validate(TestRecordFactory.CreateValidScript());

        Assert.Contains(result.Errors, issue => issue.Code == "recording_carries_producer");
        Assert.Contains(result.Errors, issue => issue.Code == "missing_tag" && issue.Location == "tag:RecorderVersion");
    }

    [Fact]
    public void RecordingModeRejectsProducerOnAnOtherwiseValidRecording()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AddTag("Producer", "spirefish/0.1.0")
            .AppendJson("run.start", "{\"data\":{}}")
            .Build();

        Assert.Contains(Sts2PgnValidator.Validate(record).Errors, issue => issue.Code == "recording_carries_producer");
    }

    [Fact]
    public void ScriptModeRejectsRecordings()
    {
        Sts2PgnValidationResult result = ValidateScript(TestRecordFactory.CreateValid());

        Assert.Contains(result.Errors, issue => issue.Code == "script_missing_producer");
        Assert.Contains(result.Errors, issue => issue.Code == "script_carries_recorder_version");
        Assert.Contains(result.Errors, issue => issue.Code == "script_mode_not_custom");
    }

    [Theory]
    [InlineData("spirefish")]
    [InlineData("/1.0")]
    [InlineData("Spirefish/1.0")]
    [InlineData("spirefish/1 0")]
    public void ScriptModeRejectsInvalidProducerShape(string producer)
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .SetTag("Producer", producer)
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .Build();

        Assert.Contains(ValidateScript(record).Errors, issue => issue.Code == "script_invalid_producer");
    }

    [Fact]
    public void ScriptModeRejectsFinalizedResultTagAndTerminalResult()
    {
        RunRecord taggedRecord = TestRecordFactory.CreateScriptBuilder()
            .AddTag("Result", "VICTORY")
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .Build();
        RunRecord terminatedRecord = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .Build(RunResult.Victory);

        Assert.Contains(ValidateScript(taggedRecord).Errors, issue => issue.Code == "script_result_not_incomplete");
        Assert.Contains(ValidateScript(terminatedRecord).Errors, issue => issue.Code == "script_result_not_incomplete");
    }

    [Fact]
    public void ScriptModeAcceptsOptionalRecordingTagsWhenWellFormed()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AddTag("RunId", "00000000-0000-0000-0000-000000000001")
            .AddTag("StartedAt", "2026-01-01T00:00:00Z")
            .AddTag("Result", "*")
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .Build();

        Assert.True(ValidateScript(record).IsValid);
    }

    [Fact]
    public void ScriptModeRequiresRunStartUnlocksAndActs()
    {
        RunRecord noState = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .Build();
        RunRecord badUnlocks = TestRecordFactory.CreateScriptBuilder()
            .AppendJson(
                "run.start",
                "{\"data\":{\"state\":{\"acts\":[{\"index\":0,\"model_id\":\"OVERGROWTH\"}],"
                + "\"players\":[{\"slot\":\"p0\",\"unlocks\":{\"epochs\":[1],\"encounters_seen\":[],\"number_of_runs\":0}}]}}}")
            .Build();

        Sts2PgnValidationResult noStateResult = ValidateScript(noState);
        Assert.Contains(noStateResult.Errors, issue => issue.Code == "script_missing_unlocks");
        Assert.Contains(noStateResult.Errors, issue => issue.Code == "script_missing_acts");
        Assert.Contains(ValidateScript(badUnlocks).Errors, issue => issue.Code == "script_invalid_unlocks");
    }

    [Fact]
    public void ScriptModeWarnsOnIgnoredRecordsAndRejectsForbiddenOnes()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("room.enter", "{\"data\":{}}")
            .AppendJson("run.finish", "{\"data\":{\"result\":\"1-0\"}}")
            .AppendJson("game.unknown_action", "{\"actor\":\"p0\",\"data\":{}}")
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);
        Assert.Contains(result.Warnings, issue => issue.Code == "script_ignored_record" && issue.Location == "sequence:2");
        Assert.Contains(result.Errors, issue => issue.Code == "script_unsupported_record" && issue.Location == "sequence:3");
        Assert.Contains(result.Errors, issue => issue.Code == "script_unsupported_record" && issue.Location == "sequence:4");
    }

    [Fact]
    public void ScriptModeExplainsThatRunResumeCannotBeDriven()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("run.resume", "{\"actor\":null,\"data\":{}}")
            .Build();

        ValidationIssue issue = Assert.Single(
            ValidateScript(record).Errors,
            issue => issue.Code == "script_resume_not_supported");
        Assert.Equal("sequence:2", issue.Location);
        Assert.Contains("cannot restore the save checkpoint", issue.Message);
    }

    [Fact]
    public void ScriptModeRequiresDecisionIdentity()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("map.choose", "{\"actor\":\"p0\",\"data\":{\"col\":1}}")
            .AppendJson("reward.claim", "{\"actor\":\"p0\",\"data\":{\"set_id\":0}}")
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);
        Assert.Contains(result.Errors, issue => issue.Code == "script_missing_identity" && issue.Message.Contains("data.row"));
        Assert.Contains(result.Errors, issue => issue.Code == "script_missing_identity" && issue.Message.Contains("data.reward_index"));
    }

    [Fact]
    public void ScriptModeAcceptsCrystalSphereDecisionsAndIgnoresStart()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("event.crystal_sphere.start", "{\"actor\":\"p0\",\"data\":{\"grid\":{}}}")
            .AppendJson("event.crystal_sphere.set_tool", "{\"actor\":\"p0\",\"data\":{\"tool\":\"small\"}}")
            .AppendJson("event.crystal_sphere.click", "{\"actor\":\"p0\",\"data\":{\"x\":3,\"y\":7,\"tool\":\"small\",\"remaining_before\":4}}")
            .AppendJson("event.crystal_sphere.proceed", "{\"actor\":\"p0\",\"data\":{}}")
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(issue => issue.Code)));
        Assert.Contains(result.Warnings, issue => issue.Code == "script_ignored_record" && issue.Location == "sequence:2");
    }

    [Fact]
    public void ScriptModeRequiresCompleteCrystalSphereClickIdentity()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("event.crystal_sphere.set_tool", "{\"actor\":\"p0\",\"data\":{\"tool\":\"none\"}}")
            .AppendJson("event.crystal_sphere.click", "{\"actor\":\"p0\",\"data\":{\"x\":3,\"y\":7,\"tool\":\"small\"}}")
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);

        Assert.Contains(result.Errors, issue => issue.Code == "script_missing_identity"
            && issue.Location == "sequence:2" && issue.Message.Contains("data.tool"));
        Assert.Contains(result.Errors, issue => issue.Code == "script_missing_identity"
            && issue.Location == "sequence:3" && issue.Message.Contains("data.remaining_before"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", true)]
    [InlineData("\"outside\"", false)]
    public void ScriptModeTreatsPotionCombatContextAsAnOptionalBoolean(string? context, bool valid)
    {
        string suffix = context is null ? string.Empty : $",\"in_combat\":{context}";
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson(
                "combat.discard_potion",
                $"{{\"actor\":\"p0\",\"data\":{{\"potion_slot\":0,\"potion_model\":\"POTION.FIRE_POTION\"{suffix}}}}}")
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);

        Assert.Equal(valid, result.IsValid);
        Assert.Equal(!valid, result.Errors.Any(issue => issue.Code == "script_invalid_identity"));
    }

    [Fact]
    public void ScriptModeRequiresChoiceOfferAndFingerprints()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("reward.claim", "{\"actor\":\"p0\",\"data\":{\"set_id\":0,\"reward_index\":0,\"reward\":{}}}")
            .AppendJson("choice.index", "{\"actor\":\"p0\",\"data\":{\"indexes\":[0]}}")
            .AppendJson("shop.remove_card", "{\"actor\":\"p0\",\"data\":{\"cost\":75}}")
            .AppendJson("choice.cards", "{\"actor\":\"p0\",\"data\":{\"choice_type\":\"deck_card\",\"cards\":[{\"deck_index\":1}]}}")
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);
        Assert.Contains(result.Errors, issue => issue.Code == "script_choice_missing_offer" && issue.Location == "sequence:3");
        Assert.Contains(result.Errors, issue => issue.Code == "script_choice_missing_fingerprint" && issue.Location == "sequence:5");
    }

    [Fact]
    public void ScriptModeRejectsOrphanChoice()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("choice.cards", CardChoice)
            .Build();

        Assert.Contains(ValidateScript(record).Errors, issue => issue.Code == "script_orphan_choice" && issue.Location == "sequence:2");
    }

    [Fact]
    public void ScriptModeAllowsChoicesAfterSuspendableOperationsAndOtherChoices()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("map.choose", TestRecordFactory.ScriptMapChoose)
            .AppendJson("choice.cards", CardChoice)
            .AppendJson("combat.play_card", PlayCard)
            .AppendJson("choice.cards", CardChoice)
            .AppendJson("choice.cards", CardChoice)
            .AppendJson("combat.end_turn", "{\"actor\":\"p0\",\"data\":{\"turn\":1}}")
            .AppendJson("choice.cards", CardChoice)
            .AppendJson("reward.claim", "{\"actor\":\"p0\",\"data\":{\"set_id\":0,\"reward_index\":1,\"reward\":{\"type\":\"card\"}}}")
            .AppendJson("choice.index", RewardChoice)
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(issue => $"{issue.Code}@{issue.Location}")));
    }

    [Fact]
    public void ScriptModeKeepsSharedContractChecks()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("combat.play_card", "{\"data\":{\"combat_card_index\":0,\"card_model\":\"STRIKE\",\"card\":{},\"target_id\":3,\"target\":{\"combat_id\":4,\"model_id\":\"X\"}}}")
            .AppendJson("reward.claim", "{\"actor\":\"p0\",\"data\":{\"set_id\":0,\"reward_index\":0,\"reward\":{}}}")
            .AppendJson("choice.index", "{\"actor\":\"p0\",\"data\":{\"indexes\":[5],\"offered\":{\"context\":\"card_reward\",\"options\":{\"cards\":[{\"index\":0}],\"alternatives\":[]}}}}")
            .Build();

        Sts2PgnValidationResult result = ValidateScript(record);
        Assert.Contains(result.Errors, issue => issue.Code == "missing_action_actor");
        Assert.Contains(result.Errors, issue => issue.Code == "invalid_combat_target");
        Assert.Contains(result.Errors, issue => issue.Code == "unmapped_choice_index");
    }
}
