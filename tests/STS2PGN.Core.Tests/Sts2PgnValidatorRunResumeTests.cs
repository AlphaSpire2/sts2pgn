using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class Sts2PgnValidatorRunResumeTests
{
    private const string ResumeState = "{\"scope\":\"run.core\",\"run\":{\"seed\":\"TEST\",\"act\":0,\"floor\":3},\"acts\":[{\"index\":0,\"model_id\":\"ACT.OVERGROWTH\"}],\"players\":[{\"slot\":\"p0\",\"character_model\":\"CHARACTER.IRONCLAD\"}],\"rooms\":[],\"choices\":{\"player_choices\":[],\"rewards\":[]},\"special_event\":null,\"combat\":null}";

    [Fact]
    public void ValidatorAcceptsMatchingRunResumeProjection()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("run.resume", $"{{\"data\":{{\"reloads\":1,\"state\":{ResumeState}}}}}")
            .Append("state.observe", ResumeObservation(after: 2))
            .Build();

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void ValidatorRejectsMismatchedRunResumeProjection()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("run.resume", "{\"data\":{\"state\":{\"scope\":\"run.core\",\"different\":true}}}")
            .Append("state.observe", ResumeObservation(after: 2))
            .Build();

        Assert.Contains(
            Sts2PgnValidator.Validate(record).Errors,
            issue => issue.Code == "run_resume_projection_mismatch");
    }

    [Fact]
    public void ValidatorRejectsRunResumeWithoutEmbeddedState()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("run.resume", "{\"data\":{\"reloads\":1}}")
            .Append("state.observe", ResumeObservation(after: 2))
            .Build();

        Assert.Contains(
            Sts2PgnValidator.Validate(record).Errors,
            issue => issue.Code == "missing_run_resume_projection");
    }

    [Fact]
    public void ValidatorRejectsResumeObservationThatDoesNotFollowItsRecord()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("run.resume", $"{{\"data\":{{\"state\":{ResumeState}}}}}")
            .AppendJson("room.enter", "{\"data\":{}}")
            .Append("state.observe", ResumeObservation(after: 2))
            .Build();

        Assert.Contains(
            Sts2PgnValidator.Validate(record).Errors,
            issue => issue.Code == "invalid_run_resume_observation");
    }

    [Fact]
    public void ValidatorRejectsResumeObservationReferencingAnotherKind()
    {
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("room.enter", "{\"data\":{}}")
            .Append("state.observe", ResumeObservation(after: 2))
            .Build();

        Assert.Contains(
            Sts2PgnValidator.Validate(record).Errors,
            issue => issue.Code == "invalid_run_resume_observation");
    }

    [Fact]
    public void ValidatorRejectsResumeProjectionOutsideRunCoreScope()
    {
        const string wrongScope = "{\"scope\":\"run_start.core\",\"run\":{\"seed\":\"TEST\",\"mode\":\"standard\",\"ascension\":0,\"act\":0,\"floor\":0},\"acts\":[{\"index\":0,\"model_id\":\"ACT.OVERGROWTH\"}],\"players\":[{\"slot\":\"p0\",\"character_model\":\"CHARACTER.IRONCLAD\"}],\"rooms\":[],\"choices\":{\"player_choices\":[],\"rewards\":[]},\"special_event\":null,\"combat\":null}";
        using JsonDocument state = JsonDocument.Parse(wrongScope);
        StateObservationPayload observation = StateObservationFactory.Create(2, "run.resume", state.RootElement);
        RunRecord record = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", "{\"data\":{}}")
            .AppendJson("run.resume", $"{{\"data\":{{\"state\":{wrongScope}}}}}")
            .Append("state.observe", JsonSerializer.SerializeToElement(observation))
            .Build();

        Assert.Contains(
            Sts2PgnValidator.Validate(record).Errors,
            issue => issue.Code == "invalid_run_resume_projection");
    }

    private static JsonElement ResumeObservation(long after)
    {
        using JsonDocument state = JsonDocument.Parse(ResumeState);
        StateObservationPayload observation = StateObservationFactory.Create(after, "run.resume", state.RootElement);
        return JsonSerializer.SerializeToElement(observation);
    }
}
