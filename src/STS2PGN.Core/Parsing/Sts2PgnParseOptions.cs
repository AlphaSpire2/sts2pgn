namespace STS2PGN.Parsing;

public sealed record Sts2PgnParseOptions
{
    /// <summary>
    /// Recover a valid prefix when the unterminated final physical line contains truncated JSON.
    /// </summary>
    public bool RecoverTruncatedFinalLine { get; init; } = true;

    public int MaxJsonDepth { get; init; } = 256;
}
