using MegaCrit.Sts2.Core.Helpers;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// What to drive and how the session was launched. A path may be one script, a directory
/// (every <c>*.sts2pgn</c> in ordinal filename order), or a <c>.txt</c> list with one path per
/// line (<c>#</c> starts a comment; relative entries resolve against the list file).
/// </summary>
internal sealed record PlaybackLaunchArgs(IReadOnlyList<string> ScriptPaths, bool QuitWhenDone)
{
    public const string CommandLineKey = "sts2pgn-script";

    /// <summary>Reads <c>--sts2pgn-script=&lt;path&gt;</c>; null when the game was not launched for playback.</summary>
    public static PlaybackLaunchArgs? FromCommandLine()
    {
        if (!CommandLineHelper.HasArg(CommandLineKey))
        {
            return null;
        }

        string? value = CommandLineHelper.GetValue(CommandLineKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"--{CommandLineKey} requires a script file, directory, or list path.");
        }

        return new PlaybackLaunchArgs(Expand(value), QuitWhenDone: true);
    }

    public static PlaybackLaunchArgs FromConsole(string path) => new(Expand(path), QuitWhenDone: false);

    public static IReadOnlyList<string> Expand(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        if (Directory.Exists(fullPath))
        {
            string[] scripts = Directory.GetFiles(fullPath, "*" + FormatConstants.FileExtension, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
            if (scripts.Length == 0)
            {
                throw new FileNotFoundException($"No {FormatConstants.FileExtension} scripts in '{fullPath}'.");
            }

            return scripts;
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Script path '{fullPath}' does not exist.");
        }

        if (!fullPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return [fullPath];
        }

        string listDirectory = Path.GetDirectoryName(fullPath) ?? fullPath;
        List<string> listed = [];
        foreach (string rawLine in File.ReadAllLines(fullPath))
        {
            string line = rawLine;
            int comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string entry = Path.IsPathFullyQualified(line) ? line : Path.Combine(listDirectory, line);
            if (!File.Exists(entry))
            {
                throw new FileNotFoundException($"Script '{entry}' listed in '{fullPath}' does not exist.");
            }

            listed.Add(Path.GetFullPath(entry));
        }

        if (listed.Count == 0)
        {
            throw new FileNotFoundException($"List '{fullPath}' names no scripts.");
        }

        return listed;
    }
}
