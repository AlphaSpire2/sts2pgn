namespace STS2PGN.Validation;

internal static class FormatSyntax
{
    public static bool IsTagName(string value)
    {
        if (value.Length == 0 || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        return value.Skip(1).All(character => IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '_');
    }

    public static bool IsRecordKind(string value)
    {
        if (value.Length < 3 || value[0] == '.' || value[^1] == '.')
        {
            return false;
        }

        string[] segments = value.Split('.');
        return segments.Length >= 2 && segments.All(IsKindSegment);
    }

    private static bool IsKindSegment(string segment) =>
        segment.Length > 0
        && segment[0] is >= 'a' and <= 'z'
        && segment.Skip(1).All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '_');

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
