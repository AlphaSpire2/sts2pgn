namespace STS2PGN.Tool;

/// <summary>
/// Consumer-side command line for .sts2pgn traces. This executable is a Core
/// consumer like a simulator would be; it must never ship with the mod.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(Console.Out);
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0];
        string[] rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "validate" => ValidateCommand.Run(rest),
                "summarize" => SummarizeCommand.Run(rest),
                "to-script" => ToScriptCommand.Run(rest),
                _ => UnknownCommand(command),
            };
        }
        catch (ToolUsageException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        PrintUsage(Console.Error);
        return 2;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: sts2pgn <command> [options] <file>...");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  validate <file>...   Parse and validate traces; non-zero exit on any error.");
        writer.WriteLine("  summarize <file>...  Print headers, record statistics, and run shape.");
        writer.WriteLine("  to-script <file> --producer name/version [--out path]");
        writer.WriteLine("                       Convert a recording into a decision script for the playback driver.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --no-recover   Treat a truncated unterminated final line as a parse error.");
        writer.WriteLine("  --quiet        validate: print only files with problems.");
        writer.WriteLine("  --script       validate: check the decision-script profile (Producer header) instead of a recording.");
    }
}

internal sealed class ToolUsageException(string message) : Exception(message);
