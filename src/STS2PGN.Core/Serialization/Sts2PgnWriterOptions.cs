namespace STS2PGN.Serialization;

public sealed record Sts2PgnWriterOptions
{
    public bool ValidateBeforeWriting { get; init; } = true;
    public bool IncludeTerminalResult { get; init; } = true;
}
