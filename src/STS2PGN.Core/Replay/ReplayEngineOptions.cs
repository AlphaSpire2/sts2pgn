namespace STS2PGN.Replay;

public sealed class ReplayEngineOptions
{
    public StateComparisonRules ComparisonRules { get; init; } = StateComparisonRules.Default;
    public int MaximumDifferences { get; init; } = 100;
}
