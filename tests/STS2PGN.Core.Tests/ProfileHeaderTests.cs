using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class ProfileHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyAndExplicitProfilesRoundTripThroughBothWriters(bool script)
    {
        RunRecord legacy = script ? TestRecordFactory.CreateValidScript() : TestRecordFactory.CreateValid();
        string expected = script ? "script" : "recording";
        Assert.Null(legacy.Metadata.GetValue("Profile"));
        Assert.Equal(script, ScriptProfile.IsScript(legacy.Metadata));
        string text = Sts2PgnWriter.Write(legacy);
        RunRecord parsed = Sts2PgnParser.Parse(text);
        Assert.Equal(expected, parsed.Metadata.GetValue("Profile"));
        Assert.Equal(script, ScriptProfile.IsScript(parsed.Metadata));
        Assert.Equal(text, Sts2PgnWriter.Write(parsed));
        using StringWriter target = new();
        using (Sts2PgnStreamWriter stream = new(target, legacy.Metadata, leaveOpen: true)) { }
        Assert.Contains($"[Profile \"{expected}\"]\n", target.ToString());
        Assert.True(Sts2PgnValidator.Validate(parsed,
            script ? Sts2PgnValidationMode.Script : Sts2PgnValidationMode.Recording).IsValid);
    }

    [Theory]
    [InlineData("script", false)]
    [InlineData("recording", true)]
    [InlineData("observation", false)]
    [InlineData("Script", true)]
    [InlineData("", false)]
    public void ConflictingOrUnknownProfilesFailValidationAndConversion(string profile, bool script)
    {
        RunRecord source = script ? TestRecordFactory.CreateValidScript() : TestRecordFactory.CreateValid();
        RunRecord invalid = new(new RunMetadata([.. source.Metadata.Tags, new("Profile", profile)]), source.Entries);
        Assert.Contains(Sts2PgnValidator.Validate(invalid,
            script ? Sts2PgnValidationMode.Script : Sts2PgnValidationMode.Recording).Errors,
            issue => issue.Code == "invalid_profile");
        Assert.Throws<InvalidDataException>(() => ScriptProfile.IsScript(invalid.Metadata));
        Assert.Throws<InvalidDataException>(() => ScriptConverter.FromRecording(invalid, "test/1"));
    }

    [Fact]
    public void ExplicitProfileDoesNotRelaxRequiredProducerTagsOrRequestedMode()
    {
        RunRecord record = TestRecordFactory.CreateScriptBuilder()
            .AddTag("Profile", "script").AppendJson("run.start", TestRecordFactory.ScriptRunStart).Build();
        Assert.Contains(Sts2PgnValidator.Validate(record).Errors, issue => issue.Code == "profile_mode_mismatch");
        RunRecord missingProducer = new(new RunMetadata(record.Metadata.Tags.Where(t => t.Name != "Producer")), record.Entries);
        Assert.True(ScriptProfile.IsScript(missingProducer.Metadata));
        Assert.Contains(Sts2PgnValidator.Validate(missingProducer, Sts2PgnValidationMode.Script).Errors,
            issue => issue.Code == "script_missing_producer");
    }

    [Fact]
    public void DuplicateProfileIsRejected()
    {
        RunRecord record = TestRecordFactory.CreateBuilder().AddTag("Profile", "recording")
            .AddTag("Profile", "recording").AppendJson("run.start", "{\"data\":{}}").Build();
        Assert.Contains(Sts2PgnValidator.Validate(record).Errors, issue => issue.Code == "duplicate_tag");
        Assert.Throws<InvalidDataException>(() => ScriptProfile.IsScript(record.Metadata));
    }

    [Fact]
    public void ConversionReplacesRecordingProfileWithScriptProfile()
    {
        RunRecord recording = TestRecordFactory.CreateBuilder().AddTag("Profile", "recording")
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart).Build();
        RunRecord script = ScriptConverter.FromRecording(recording, "test/1");
        Assert.Equal("script", script.Metadata.GetValue("Profile"));
        Assert.Single(script.Metadata.GetValues("Profile"));
        Assert.Null(script.Metadata.GetValue("RecorderVersion"));
    }
}
