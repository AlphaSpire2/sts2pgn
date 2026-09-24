using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tool;

internal sealed record LoadedTrace(string Path, RunRecord Record);

/// <summary>Shared command-line arguments.</summary>
internal sealed record ToolArguments(
    IReadOnlyList<string> Paths,
    Sts2PgnParseOptions ParseOptions,
    bool Quiet,
    Sts2PgnValidationMode? ValidationMode);

internal static class TraceLoader
{
    /// <summary>
    /// Splits shared file/option arguments. Every non-option argument is a trace path.
    /// </summary>
    public static ToolArguments ParseArguments(string[] args)
    {
        List<string> paths = [];
        bool recover = true;
        bool quiet = false;
        Sts2PgnValidationMode? mode = null;
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
                case "--script":
                    mode = Sts2PgnValidationMode.Script;
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
            throw new ToolUsageException("expected at least one .sts2pgn or .sts2pgn.gz file");
        }

        return new ToolArguments(paths, new Sts2PgnParseOptions { RecoverTruncatedFinalLine = recover }, quiet, mode);
    }

    public static LoadedTrace Load(string path, Sts2PgnParseOptions options)
    {
        using StreamReader reader = Sts2PgnFile.OpenText(path);
        return new LoadedTrace(path, Sts2PgnParser.Parse(reader, options));
    }
}
