namespace STS2PGN.Replay;

public sealed record ReplayCompatibilityResult(bool IsSupported, string? Message)
{
    public static ReplayCompatibilityResult Supported { get; } = new(true, null);

    public static ReplayCompatibilityResult Unsupported(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ReplayCompatibilityResult(false, message);
    }
}
