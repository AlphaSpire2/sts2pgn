using STS2PGN.Model;

namespace STS2PGN.Replay;

public sealed class ReplayExecutionResult
{
    public bool Succeeded => Failure is null;
    public RunResult ExpectedResult { get; }
    public RunResult? ActualResult { get; }
    public long? LastMatchingObservationSequence { get; }
    public ReplayFailure? Failure { get; }

    private ReplayExecutionResult(
        RunResult expectedResult,
        RunResult? actualResult,
        long? lastMatchingObservationSequence,
        ReplayFailure? failure)
    {
        ExpectedResult = expectedResult;
        ActualResult = actualResult;
        LastMatchingObservationSequence = lastMatchingObservationSequence;
        Failure = failure;
    }

    internal static ReplayExecutionResult Success(
        RunResult expectedResult,
        RunResult actualResult,
        long? lastMatchingObservationSequence) =>
        new(expectedResult, actualResult, lastMatchingObservationSequence, null);

    internal static ReplayExecutionResult Failed(
        RunResult expectedResult,
        RunResult? actualResult,
        long? lastMatchingObservationSequence,
        ReplayFailure failure) =>
        new(expectedResult, actualResult, lastMatchingObservationSequence, failure);
}
