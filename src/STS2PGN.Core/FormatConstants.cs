namespace STS2PGN;

/// <summary>
/// Stable identifiers shared by STS2PGN producers and consumers.
/// </summary>
public static class FormatConstants
{
    public const string Name = "STS2PGN";
    public const int CurrentVersion = 2;
    public const int DeltaEncodingVersion = 2;
    public const int CurrentStateProjectionVersion = 1;
    public const string FileExtension = ".sts2pgn";

    public static bool IsSupportedVersion(int version) => version is 1 or 2;
}
