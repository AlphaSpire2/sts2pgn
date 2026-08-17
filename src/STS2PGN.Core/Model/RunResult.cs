namespace STS2PGN.Model;

public enum RunResult
{
    Incomplete,
    Victory,
    Defeat,
    Abandoned,
    Diverged,
}

public static class RunResultExtensions
{
    public static string ToToken(this RunResult result) => result switch
    {
        RunResult.Incomplete => "*",
        RunResult.Victory => "VICTORY",
        RunResult.Defeat => "DEFEAT",
        RunResult.Abandoned => "ABANDONED",
        RunResult.Diverged => "DIVERGED",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    public static bool TryParseToken(string value, out RunResult result)
    {
        result = value switch
        {
            "*" => RunResult.Incomplete,
            "VICTORY" => RunResult.Victory,
            "DEFEAT" => RunResult.Defeat,
            "ABANDONED" => RunResult.Abandoned,
            "DIVERGED" => RunResult.Diverged,
            _ => default,
        };

        return value is "*" or "VICTORY" or "DEFEAT" or "ABANDONED" or "DIVERGED";
    }
}
