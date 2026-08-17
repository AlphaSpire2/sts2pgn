namespace STS2PGN.Replay;

public sealed record ReplayConsumeResult(bool Accepted, string? Message)
{
    public static ReplayConsumeResult Success { get; } = new(true, null);

    public static ReplayConsumeResult Rejected(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ReplayConsumeResult(false, message);
    }
}
