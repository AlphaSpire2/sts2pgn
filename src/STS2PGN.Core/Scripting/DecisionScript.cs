using System.Globalization;
using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Validation;

namespace STS2PGN.Scripting;

/// <summary>Header identity a playback driver must match against the live game before starting a run.</summary>
public sealed record ScriptIdentity(
    string Producer,
    string GameVersion,
    string GameCommit,
    string ModelIdHash,
    string Seed,
    string Mode,
    int Ascension,
    string CharacterModel);

/// <summary>The profile unlock state a script expects, mirroring <c>players[0].unlocks</c>.</summary>
public sealed record ScriptUnlocks(
    IReadOnlyList<string> Epochs,
    IReadOnlyList<string> EncountersSeen,
    int NumberOfRuns)
{
    /// <summary>Compares as sets (epochs, encounters) plus an exact run count; explains the first difference.</summary>
    public bool Matches(ScriptUnlocks other, out string difference)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (NumberOfRuns != other.NumberOfRuns)
        {
            difference = $"number_of_runs: expected {NumberOfRuns}, live {other.NumberOfRuns}";
            return false;
        }

        if (!SetEquals(Epochs, other.Epochs, out string epochDifference))
        {
            difference = $"epochs: {epochDifference}";
            return false;
        }

        if (!SetEquals(EncountersSeen, other.EncountersSeen, out string encounterDifference))
        {
            difference = $"encounters_seen: {encounterDifference}";
            return false;
        }

        difference = string.Empty;
        return true;
    }

    private static bool SetEquals(IReadOnlyList<string> expected, IReadOnlyList<string> live, out string difference)
    {
        SortedSet<string> expectedSet = new(expected, StringComparer.Ordinal);
        SortedSet<string> liveSet = new(live, StringComparer.Ordinal);
        string[] missing = expectedSet.Except(liveSet, StringComparer.Ordinal).ToArray();
        string[] extra = liveSet.Except(expectedSet, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && extra.Length == 0)
        {
            difference = string.Empty;
            return true;
        }

        difference = $"missing live [{string.Join(", ", missing)}], extra live [{string.Join(", ", extra)}]";
        return false;
    }
}

/// <summary>One drivable decision, in script order.</summary>
public sealed class ScriptDecision
{
    /// <summary>Zero-based position among drivable decisions.</summary>
    public int Ordinal { get; }

    /// <summary>The record sequence number in the script file.</summary>
    public long Sequence { get; }

    public string Kind { get; }
    public string? Actor { get; }
    public JsonElement Payload { get; }

    /// <summary>The <c>data</c> object, or an undefined element when absent.</summary>
    public JsonElement Data { get; }

    internal ScriptDecision(int ordinal, RunRecordEntry entry)
    {
        Ordinal = ordinal;
        Sequence = entry.Sequence;
        Kind = entry.Kind;
        Payload = entry.Payload;
        Actor = entry.Payload.TryGetProperty("actor", out JsonElement actor) && actor.ValueKind == JsonValueKind.String
            ? actor.GetString()
            : null;
        Data = entry.Payload.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
            ? data
            : default;
    }

    public bool IsChoice => Kind.StartsWith("choice.", StringComparison.Ordinal);
}

/// <summary>
/// A parsed decision script: header identity, expected unlock state and acts, and the ordered
/// decisions a playback driver executes. Construction requires a script-profile trace.
/// </summary>
public sealed class DecisionScript
{
    public RunRecord Source { get; }
    public ScriptIdentity Identity { get; }
    public ScriptUnlocks Unlocks { get; }
    public IReadOnlyList<string> ExpectedActModelIds { get; }
    public IReadOnlyList<ScriptDecision> Decisions { get; }

    private DecisionScript(
        RunRecord source,
        ScriptIdentity identity,
        ScriptUnlocks unlocks,
        IReadOnlyList<string> expectedActModelIds,
        IReadOnlyList<ScriptDecision> decisions)
    {
        Source = source;
        Identity = identity;
        Unlocks = unlocks;
        ExpectedActModelIds = expectedActModelIds;
        Decisions = decisions;
    }

    /// <summary>Parses and validates a script from text.</summary>
    /// <exception cref="Sts2PgnParseException">The text is not a well-formed trace.</exception>
    /// <exception cref="Sts2PgnValidationException">The trace is not a valid decision script.</exception>
    public static DecisionScript Load(TextReader reader, Sts2PgnParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        RunRecord record = Sts2PgnParser.Parse(
            reader,
            options ?? new Sts2PgnParseOptions { RecoverTruncatedFinalLine = false });
        return FromRecord(record);
    }

    /// <summary>Validates the record in script mode and extracts its drivable content.</summary>
    /// <exception cref="Sts2PgnValidationException">The record is not a valid decision script.</exception>
    public static DecisionScript FromRecord(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record, Sts2PgnValidationMode.Script);
        if (!validation.IsValid)
        {
            throw new Sts2PgnValidationException(validation);
        }

        RunMetadata metadata = record.Metadata;
        ScriptIdentity identity = new(
            Producer: metadata.GetValue(ScriptProfile.ProducerTag)!,
            GameVersion: metadata.GetValue("GameVersion")!,
            GameCommit: metadata.GetValue("GameCommit")!,
            ModelIdHash: metadata.GetValue("ModelIdHash")!,
            Seed: metadata.GetValue("Seed")!,
            Mode: metadata.GetValue("Mode")!,
            Ascension: int.Parse(metadata.GetValue("Ascension")!, NumberStyles.None, CultureInfo.InvariantCulture),
            CharacterModel: FirstCharacter(metadata.GetValue("Players")!));

        RunRecordEntry runStart = record.Entries[0];
        JsonElement state = runStart.Payload.GetProperty("data").GetProperty("state");
        JsonElement unlocks = state.GetProperty("players")[0].GetProperty("unlocks");
        ScriptUnlocks scriptUnlocks = new(
            Epochs: ReadStrings(unlocks.GetProperty("epochs")),
            EncountersSeen: ReadStrings(unlocks.GetProperty("encounters_seen")),
            NumberOfRuns: unlocks.GetProperty("number_of_runs").GetInt32());

        string[] acts = state.GetProperty("acts")
            .EnumerateArray()
            .OrderBy(act => act.GetProperty("index").GetInt32())
            .Select(act => act.GetProperty("model_id").GetString()!)
            .ToArray();

        List<ScriptDecision> decisions = [];
        foreach (RunRecordEntry entry in record.Entries.Skip(1))
        {
            if (ScriptProfile.DriveableKinds.Contains(entry.Kind))
            {
                decisions.Add(new ScriptDecision(decisions.Count, entry));
            }
        }

        return new DecisionScript(record, identity, scriptUnlocks, acts, decisions.AsReadOnly());
    }

    private static string FirstCharacter(string playersJson)
    {
        using JsonDocument document = JsonDocument.Parse(playersJson);
        return document.RootElement[0].GetProperty("character").GetString()!;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(element => element.GetString()!).ToArray();
}
