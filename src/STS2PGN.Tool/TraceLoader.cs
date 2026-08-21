using STS2PGN.Model;
using STS2PGN.Parsing;

namespace STS2PGN.Tool;

internal sealed record LoadedTrace(string Path, RunRecord Record);

internal static class TraceLoader
{
    /// <summary>
    /// Splits shared file/option arguments. Every non-option argument is a trace path.
    /// </summary>
    public static (IReadOnlyList<string> Paths, Sts2PgnParseOptions ParseOptions, bool Quiet) ParseArguments(string[] args)
    {
        List<string> paths = [];
        bool recover = true;
        bool quiet = false;
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--no-recover":
                    recover = false;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ToolUsageException($"unknown option '{arg}'");
                    }

                    paths.Add(arg);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            throw new ToolUsageException("expected at least one .sts2pgn file");
        }

        return (paths, new Sts2PgnParseOptions { RecoverTruncatedFinalLine = recover }, quiet);
    }

    public static LoadedTrace Load(string path, Sts2PgnParseOptions options)
    {
        using StreamReader reader = new(path);
        return new LoadedTrace(path, Sts2PgnParser.Parse(reader, options));
    }
}
