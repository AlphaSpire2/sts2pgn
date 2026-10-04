using MegaCrit.Sts2.Core.Helpers;
using STS2PGN.Serialization;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// What to drive and how the session was launched. A path may be one script, a directory
/// (every <c>*.sts2pgn</c> and <c>*.sts2pgn.gz</c> in ordinal filename order), or a <c>.txt</c> list with one path per
/// line (<c>#</c> starts a comment; relative entries resolve against the list file).
/// </summary>
internal sealed record PlaybackLaunchArgs(
    IReadOnlyList<string> ScriptPaths,
    bool QuitWhenDone,
    bool HandoffWhenDone,
    bool ManualSteps = false)
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

        return new PlaybackLaunchArgs(Expand(value), QuitWhenDone: true, HandoffWhenDone: false);
    }

    public static PlaybackLaunchArgs FromConsole(string path, bool handoffWhenDone = false, bool manualSteps = false)
    {
        if (manualSteps && (!File.Exists(Path.GetFullPath(path.Trim().Trim('"')))
            || !Sts2PgnFile.IsTracePath(path.Trim().Trim('"'))))
        {
            throw new ArgumentException("step requires one .sts2pgn or .sts2pgn.gz file");
        }
        IReadOnlyList<string> scripts = Expand(path);
        if (handoffWhenDone && scripts.Count != 1)
        {
            throw new ArgumentException("handoff requires exactly one script file");
        }

        return new PlaybackLaunchArgs(scripts, QuitWhenDone: false, HandoffWhenDone: handoffWhenDone,
            ManualSteps: manualSteps);
    }

    public static IReadOnlyList<string> Expand(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        if (Directory.Exists(fullPath))
        {
            string[] scripts = Directory.GetFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(Sts2PgnFile.IsTracePath)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
            if (scripts.Length == 0)
            {
                throw new FileNotFoundException($"No .sts2pgn or .sts2pgn.gz scripts in '{fullPath}'.");
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
