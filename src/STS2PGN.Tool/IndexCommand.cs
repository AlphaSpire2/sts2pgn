using System.Text.Json;
using STS2PGN.Parsing;
using STS2PGN.Serialization;

namespace STS2PGN.Tool;

internal static class IndexCommand
{
    public static int Run(string[] args)
    {
        bool overwrite = false;
        List<string> paths = [];
        foreach (string arg in args)
        {
            if (arg == "--overwrite")
            {
                overwrite = true;
            }
            else if (arg.StartsWith('-'))
            {
                throw new ToolUsageException($"unknown option '{arg}'");
            }
            else
            {
                paths.Add(arg);
            }
        }
        if (paths.Count == 0)
        {
            throw new ToolUsageException("index expects at least one .sts2pgn or .sts2pgn.gz file");
        }
        bool failed = false;
        foreach (string path in paths)
        {
            try
            {
                Console.WriteLine(Sts2PgnSidecar.Write(path, overwrite));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Sts2PgnParseException or JsonException)
            {
                Console.Error.WriteLine($"{path}: {exception.Message}");
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }
}
