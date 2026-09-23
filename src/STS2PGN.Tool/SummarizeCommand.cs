using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;
using STS2PGN.Validation;

namespace STS2PGN.Tool;

internal static class SummarizeCommand
{
    public static int Run(string[] args)
    {
        ToolArguments arguments = TraceLoader.ParseArguments(args);
        IReadOnlyList<string> paths = arguments.Paths;
        Sts2PgnParseOptions parseOptions = arguments.ParseOptions;
        bool anyError = false;
        bool first = true;
        foreach (string path in paths)
        {
            if (!first)
            {
                Console.WriteLine();
            }

            first = false;
            try
            {
                Summarize(TraceLoader.Load(path, parseOptions));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Sts2PgnParseException)
            {
                Console.Error.WriteLine($"{path}: {exception.Message}");
                anyError = true;
            }
        }

        return anyError ? 1 : 0;
    }

    private static void Summarize(LoadedTrace trace)
    {
        RunRecord record = trace.Record;
        RunMetadata tags = record.Metadata;
        Console.WriteLine(trace.Path);
        Write("Format", $"{tags.GetValue("Format")} v{tags.GetValue("FormatVersion")} (recorder {tags.GetValue("RecorderVersion")})");
        Write("RunId", tags.GetValue("RunId"));
        Write("Game", $"{tags.GetValue("GameVersion")} commit {tags.GetValue("GameCommit")}, model hash {tags.GetValue("ModelIdHash")}");
        Write("Run", $"{tags.GetValue("Mode")} seed {tags.GetValue("Seed")} ascension {tags.GetValue("Ascension")}");
        Write("Players", DescribePlayers(tags.GetValue("Players")));
        Write("Started", tags.GetValue("StartedAt"));
        Write("Mods", DescribeMods(tags.GetValue("Mods")));
        string headerResult = tags.GetValue("Result") ?? "*";
        string terminalResult = record.Result.ToToken();
        Write("Result", headerResult == terminalResult ? terminalResult : $"{terminalResult} (header says {headerResult})");
        if (record.RecoveredTruncatedFinalLine)
        {
            Write("Recovery", "truncated unterminated final line was discarded (crash prefix)");
        }

        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(
            record,
            ScriptProfile.IsScript(tags) ? Sts2PgnValidationMode.Script : Sts2PgnValidationMode.Recording);
        Write("Validation", validation.IsValid
            ? $"valid, {validation.Warnings.Count()} warning(s)"
            : $"INVALID, {validation.Errors.Count()} error(s) (run 'sts2pgn validate' for details)");

        List<RunRecordEntry> observations = [.. record.Entries.Where(entry => entry.Kind == "state.observe")];
        int decisions = record.Entries.Count - observations.Count;
        Console.WriteLine();
        Write("Records", $"{record.Entries.Count} total: {decisions} actions, {observations.Count} observations");
        Write("Progress", DescribeProgress(record));
        Write("Rooms", DescribeCounts(RoomTypeCounts(record)));
        Write("Contexts", DescribeCounts(ContextCounts(observations)));
        Console.WriteLine("  Actions");
        foreach ((string kind, int count) in KindCounts(record))
        {
            Console.WriteLine($"    {count,5} {kind}");
        }
    }

    private static void Write(string label, string? value) =>
        Console.WriteLine($"  {label,-11} {value ?? "-"}");

    private static string DescribePlayers(string? json) =>
        DescribeJsonArray(json, element =>
            $"{element.GetStringOrNull("slot")} {element.GetStringOrNull("character")}");

    private static string DescribeMods(string? json) =>
        DescribeJsonArray(json, element =>
        {
            string text = $"{element.GetStringOrNull("id")} {element.GetStringOrNull("version")}";
            return element.TryGetProperty("affects_gameplay", out JsonElement affects) && affects.ValueKind == JsonValueKind.True
                ? text + " [affects gameplay]"
                : text;
        });

    private static string DescribeJsonArray(string? json, Func<JsonElement, string> describe)
    {
        if (string.IsNullOrEmpty(json))
        {
            return "-";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return json;
            }

            return string.Join(", ", document.RootElement.EnumerateArray().Select(describe));
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string DescribeProgress(RunRecord record)
    {
        int? maxAct = null;
        int? maxFloor = null;
        foreach (RunRecordEntry entry in record.Entries)
        {
            if (!entry.Payload.TryGetProperty("location", out JsonElement location) ||
                location.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            maxAct = Max(maxAct, location.GetInt32OrNull("act"));
            maxFloor = Max(maxFloor, location.GetInt32OrNull("floor"));
        }

        int mapMoves = record.Entries.Count(entry => entry.Kind == "map.choose");
        int roomsEntered = record.Entries.Count(entry => entry.Kind == "room.enter");
        string acts = maxAct.HasValue ? $"reached act {maxAct}" : "act unknown";
        string floors = maxFloor.HasValue ? $"floor {maxFloor}" : "floor unknown";
        return $"{acts}, {floors}, {mapMoves} map moves, {roomsEntered} rooms entered";
    }

    private static int? Max(int? current, int? candidate) =>
        candidate.HasValue ? Math.Max(current ?? int.MinValue, candidate.Value) : current;

    private static IEnumerable<(string Key, int Count)> RoomTypeCounts(RunRecord record) =>
        CountBy(record.Entries.Where(entry => entry.Kind == "room.enter"), entry =>
            entry.Payload.TryGetProperty("location", out JsonElement location) &&
            location.ValueKind == JsonValueKind.Object &&
            location.TryGetProperty("room", out JsonElement room) &&
            room.ValueKind == JsonValueKind.Object
                ? room.GetStringOrNull("type")
                : null);

    private static IEnumerable<(string Key, int Count)> ContextCounts(IEnumerable<RunRecordEntry> observations) =>
        CountBy(observations, entry => entry.Payload.GetStringOrNull("context"));

    private static IEnumerable<(string Key, int Count)> KindCounts(RunRecord record) =>
        CountBy(record.Entries.Where(entry => entry.Kind != "state.observe"), entry => entry.Kind);

    private static IEnumerable<(string Key, int Count)> CountBy(
        IEnumerable<RunRecordEntry> entries,
        Func<RunRecordEntry, string?> selector) =>
        entries
            .GroupBy(entry => selector(entry) ?? "(unknown)", StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(pair => pair.Item2)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal);

    private static string DescribeCounts(IEnumerable<(string Key, int Count)> counts)
    {
        string text = string.Join(", ", counts.Select(pair => $"{pair.Key} x{pair.Count}"));
        return text.Length == 0 ? "-" : text;
    }

    private static string? GetStringOrNull(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32OrNull(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : null;
}
