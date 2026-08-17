namespace STS2PGN.Replay;

public sealed class StateComparisonResult
{
    public bool IsMatch => Differences.Count == 0;
    public IReadOnlyList<StateDifference> Differences { get; }
    public bool DifferencesTruncated { get; }

    internal StateComparisonResult(IReadOnlyList<StateDifference> differences, bool differencesTruncated)
    {
        Differences = differences;
        DifferencesTruncated = differencesTruncated;
    }
}
