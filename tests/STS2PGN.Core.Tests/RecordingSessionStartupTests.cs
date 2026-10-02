using System.Text.Json;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Tests;

public sealed class RecordingSessionStartupTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"record_partial_runs\":false}")]
    [InlineData("{\"record_partial_runs\":null}")]
    [InlineData("{\"record_partial_runs\":\"true\"}")]
    [InlineData("{\"record_partial_runs\":1}")]
    public void MissingOrDisabledOptionSkipsAnOrphanedResumeWithoutCreatingAFile(string json)
    {
        using JsonDocument config = JsonDocument.Parse(json);
        int searches = 0;
        object? session = RecordingSessionStartup.Open<object>(
            persist: true,
            continueRecording: () => { searches++; return null; },
            startRecording: () => throw new InvalidOperationException("Must not create a recording"),
            recordPartialRuns: RecordingSessionStartup.ReadRecordPartialRuns(config.RootElement));

        Assert.Null(session);
        Assert.Equal(1, searches);
    }

    [Fact]
    public void OptInCreatesPartialRecordingOnlyAfterLookingForItsPrefix()
    {
        using JsonDocument config = JsonDocument.Parse("{\"record_partial_runs\":true}");
        List<string> calls = [];
        object partial = new();
        object? session = RecordingSessionStartup.Open<object>(
            persist: true,
            continueRecording: () => { calls.Add("continue"); return null; },
            startRecording: () => { calls.Add("start"); return partial; },
            recordPartialRuns: RecordingSessionStartup.ReadRecordPartialRuns(config.RootElement));

        Assert.Same(partial, session);
        Assert.Equal(new[] { "continue", "start" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingTraceIsAlwaysContinuedInsteadOfCreatingAPartial(bool recordPartialRuns)
    {
        object existing = new();
        object? session = RecordingSessionStartup.Open<object>(
            persist: true,
            continueRecording: () => existing,
            startRecording: () => throw new InvalidOperationException("Must not replace the trace"),
            recordPartialRuns: recordPartialRuns);

        Assert.Same(existing, session);
    }

    [Fact]
    public void SkippedResumeDoesNotPreventRecordingTheNextNewRun()
    {
        int starts = 0;
        object NewSession() { starts++; return new object(); }
        Assert.Null(RecordingSessionStartup.Open<object>(true, () => null, NewSession));
        Assert.NotNull(RecordingSessionStartup.Open<object>(true, null, NewSession));
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransientPlaybackStartsWithoutOpeningAnExistingRecording(bool resumed)
    {
        object transient = new();
        object? session = RecordingSessionStartup.Open<object>(
            persist: false,
            continueRecording: resumed
                ? () => throw new InvalidOperationException("Must not open a recording on disk")
                : null,
            startRecording: () => transient);

        Assert.Same(transient, session);
    }
}
