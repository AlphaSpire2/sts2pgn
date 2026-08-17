using STS2PGN.Model;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class Sts2PgnValidatorTests
{
    [Fact]
    public void ValidRecordHasNoErrors()
    {
        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(TestRecordFactory.CreateValid());

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Errors);
    }

    [Fact]
    public void ValidatorFindsMissingAndDuplicateTags()
    {
        RunRecord invalid = TestRecordFactory.CreateBuilder()
            .AddTag("Seed", "DUPLICATE")
            .SetTag("Players", "not-json")
            .AppendJson("run.start", "{}")
            .Build();

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, issue => issue.Code == "duplicate_tag");
        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_players");
    }

    [Fact]
    public void ValidatorFindsSequenceAndRunStartErrors()
    {
        RunRecord invalid = new(
            TestRecordFactory.CreateValid().Metadata,
            [
                CreateEntry(2, "future.action", "{}"),
                CreateEntry(3, "run.start", "{}"),
            ]);

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(invalid);

        Assert.Contains(validation.Errors, issue => issue.Code == "missing_run_start");
        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_sequence");
    }

    [Fact]
    public void ValidatorChecksObservationReferenceDigestAndState()
    {
        RunRecord invalid = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{}")
            .AppendJson(
                "state.observe",
                "{\"after\":2,\"context\":\"\",\"digest\":\"sha256:nope\",\"projection_version\":0,\"state\":[]}")
            .Build();

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(invalid);

        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_observation_after");
        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_observation_context");
        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_projection_version");
        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_digest");
        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_observation_state");
    }

    [Fact]
    public void FinalResultCanLeaveAppendOnlyHeaderIncompleteWithWarning()
    {
        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(TestRecordFactory.CreateValid(RunResult.Victory));

        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, issue => issue.Code == "result_header_not_finalized");
    }

    [Fact]
    public void ObservationWithComputedDigestIsValid()
    {
        using System.Text.Json.JsonDocument state = System.Text.Json.JsonDocument.Parse("{\"hp\":80}");
        string digest = CanonicalJson.ComputeSha256(state.RootElement);
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{}")
            .AppendJson(
                "state.observe",
                $"{{\"after\":1,\"context\":\"run.start\",\"digest\":\"{digest}\",\"projection_version\":1,\"state\":{{\"hp\":80}}}}")
            .Build();

        Assert.True(Sts2PgnValidator.Validate(record).IsValid);
    }

    [Fact]
    public void ValidatorRejectsDigestThatDoesNotMatchObservationState()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{}")
            .AppendJson(
                "state.observe",
                "{\"after\":1,\"context\":\"run.start\",\"digest\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\",\"projection_version\":1,\"state\":{\"hp\":80}}")
            .Build();

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);

        Assert.Contains(validation.Errors, issue => issue.Code == "digest_mismatch");
    }

    [Fact]
    public void ValidatorAcceptsInitiatorNestedChoiceResolveOrdering()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("reward.claim", "{\"actor\":\"p0\",\"data\":{\"set_id\":0,\"reward_index\":1}}")
            .AppendJson("choice.index", "{\"actor\":\"p0\",\"data\":{\"choice_id\":0,\"indexes\":[2]}}")
            .AppendJson("reward.resolve", "{\"actor\":\"p0\",\"data\":{\"action_sequence\":2,\"success\":true}}")
            .Build();

        Assert.True(Sts2PgnValidator.Validate(record).IsValid);
    }

    [Fact]
    public void ValidatorRejectsResolveThatPointsAtWrongOrAlreadyResolvedAction()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("rest.choose", "{\"data\":{\"option_id\":\"SMITH\"}}")
            .AppendJson("reward.resolve", "{\"data\":{\"action_sequence\":2,\"success\":true}}")
            .AppendJson("rest.resolve", "{\"data\":{\"action_sequence\":2,\"success\":true}}")
            .Build();

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);

        Assert.Contains(validation.Errors, issue => issue.Code == "resolve_kind_mismatch");
        Assert.Contains(validation.Errors, issue => issue.Code == "duplicate_resolve");
    }

    [Fact]
    public void ValidatorRequiresBooleanResolveOutcome()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("shop.remove_card", "{\"data\":{\"cost\":75}}")
            .AppendJson("shop.remove_card.resolve", "{\"data\":{\"action_sequence\":2}}")
            .Build();

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);

        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_resolve_success");
    }

    [Fact]
    public void ValidatorAcceptsMatchingCombatTargetFingerprint()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson(
                "combat.play_card",
                "{\"actor\":\"p0\",\"data\":{\"target_id\":7,\"target\":{\"combat_id\":7,\"model_id\":\"CULTIST\"}}}")
            .AppendJson(
                "combat.play_card",
                "{\"actor\":\"p0\",\"data\":{\"target_id\":null,\"target\":null}}")
            .Build();

        Assert.True(Sts2PgnValidator.Validate(record).IsValid);
    }

    [Fact]
    public void ValidatorRejectsMissingOrMismatchedCombatTargetFingerprint()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson(
                "combat.play_card",
                "{\"actor\":\"p0\",\"data\":{\"target_id\":7,\"target\":{\"combat_id\":8}}}")
            .Build();

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);

        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_combat_target");
    }

    [Fact]
    public void ValidatorRequiresStableShopInventoryIdentity()
    {
        RunRecord valid = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson(
                "shop.purchase",
                "{\"actor\":\"p0\",\"data\":{\"inventory_index\":6,\"entry_group\":\"relic\",\"entry_index\":0,\"entry_type\":\"relic\",\"model_id\":\"BAG_OF_PREPARATION\"}}")
            .Build();
        RunRecord invalid = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson(
                "shop.purchase",
                "{\"actor\":\"p0\",\"data\":{\"entry_type\":\"relic\",\"model_id\":\"BAG_OF_PREPARATION\"}}")
            .Build();

        Assert.True(Sts2PgnValidator.Validate(valid).IsValid);
        Assert.Contains(
            Sts2PgnValidator.Validate(invalid).Errors,
            issue => issue.Code == "invalid_shop_identity");
    }

    [Fact]
    public void ValidatorMapsCardRewardAlternativesIntoCombinedIndexSpace()
    {
        RunRecord valid = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson(
                "choice.index",
                "{\"actor\":\"p0\",\"data\":{\"indexes\":[3],\"offered\":{\"context\":\"card_reward\",\"options\":{\"cards\":[{\"index\":0},{\"index\":1},{\"index\":2}],\"alternatives\":[{\"index\":3,\"option_id\":\"skip\"},{\"index\":4,\"option_id\":\"reroll\"}]}}}}")
            .Build();
        RunRecord invalid = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson(
                "choice.index",
                "{\"actor\":\"p0\",\"data\":{\"indexes\":[3],\"offered\":{\"context\":\"card_reward\",\"options\":{\"cards\":[{\"index\":0},{\"index\":1},{\"index\":2}],\"alternatives\":[\"skip\"]}}}}")
            .Build();

        Assert.True(Sts2PgnValidator.Validate(valid).IsValid);
        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(invalid);
        Assert.Contains(validation.Errors, issue => issue.Code == "invalid_card_reward_offer");
        Assert.Contains(validation.Errors, issue => issue.Code == "unmapped_choice_index");
    }

    [Fact]
    public void ValidatorRequiresActorOnPlayerDecisions()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("map.choose", "{\"data\":{\"col\":1,\"row\":2}}")
            .Build();

        Assert.Contains(
            Sts2PgnValidator.Validate(record).Errors,
            issue => issue.Code == "missing_action_actor");
    }

    private static RunRecordEntry CreateEntry(long sequence, string kind, string json)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        return new RunRecordEntry(sequence, kind, document.RootElement);
    }
}
