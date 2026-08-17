namespace STS2PGN.Parsing;

public sealed class Sts2PgnParseException : FormatException
{
    public int LineNumber { get; }

    public Sts2PgnParseException(string message, int lineNumber)
        : base($"Line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }

    public Sts2PgnParseException(string message, int lineNumber, Exception innerException)
        : base($"Line {lineNumber}: {message}", innerException)
    {
        LineNumber = lineNumber;
    }
}
