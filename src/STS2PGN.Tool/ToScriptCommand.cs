using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tool;

/// <summary>
/// <c>to-script &lt;recording&gt; --producer name/version [--out path]</c>: turns a recording into a
/// decision script that replays the same decisions through the playback driver.
/// </summary>
internal static class ToScriptCommand
{
    public static int Run(string[] args)
    {
        string? producer = null;
        string? output = null;
        List<string> paths = [];
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--producer" when index + 1 < args.Length:
                    producer = args[++index];
                    break;
                case "--out" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                default:
                    if (args[index].StartsWith('-'))
                    {
                        throw new ToolUsageException($"unknown option '{args[index]}'");
                    }

                    paths.Add(args[index]);
                    break;
            }
        }

        if (paths.Count != 1)
        {
            throw new ToolUsageException("to-script expects exactly one recording");
        }

        if (producer is null || !ScriptProfile.TryParseProducer(producer, out _, out _))
        {
            throw new ToolUsageException("to-script requires --producer name/version (lowercase name)");
        }

        string path = paths[0];
        output ??= Path.ChangeExtension(path, null) + ".script" + FormatConstants.FileExtension;
        try
        {
            RunRecord recording = TraceLoader.Load(path, new Sts2PgnParseOptions { RecoverTruncatedFinalLine = false }).Record;
            RunRecord script = ScriptConverter.FromRecording(recording, producer);
            using StreamWriter writer = new(output);
            Sts2PgnWriter.Write(writer, script);
            int decisions = script.Entries.Count - 1;
            Console.WriteLine($"{output}: script with {decisions} decisions from {recording.Entries.Count} recorded records");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Sts2PgnParseException or Sts2PgnValidationException or ArgumentException)
        {
            Console.Error.WriteLine($"{path}: {exception.Message}");
            return 1;
        }
    }
}
