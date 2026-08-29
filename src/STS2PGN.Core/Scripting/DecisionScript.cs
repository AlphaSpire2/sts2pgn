using System.Globalization;
using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Serialization;
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
    /// <summary>
    /// The boss encounters whose seen-status changes run generation: each act's
    /// <c>BossDiscoveryOrder</c>, which <c>ActModel.ApplyDiscoveryOrderModifications</c> walks to
    /// override the rolled boss with the first entry the profile has not seen. No other encounter's
    /// seen-status is read during generation or play.
    /// </summary>
    public static IReadOnlyList<string> DiscoveryBosses { get; } =
    [
        "ENCOUNTER.VANTOM_BOSS", "ENCOUNTER.CEREMONIAL_BEAST_BOSS", "ENCOUNTER.THE_KIN_BOSS",
        "ENCOUNTER.WATERFALL_GIANT_BOSS", "ENCOUNTER.SOUL_FYSH_BOSS", "ENCOUNTER.LAGAVULIN_MATRIARCH_BOSS",
        "ENCOUNTER.THE_INSATIABLE_BOSS", "ENCOUNTER.KNOWLEDGE_DEMON_BOSS", "ENCOUNTER.KAISER_CRAB_BOSS",
        "ENCOUNTER.QUEEN_BOSS", "ENCOUNTER.TEST_SUBJECT_BOSS", "ENCOUNTER.AEONGLASS_BOSS",
    ];

    /// <summary>True for a profile that has never completed a run; the only distinction the game makes.</summary>
    public bool IsFirstRun => NumberOfRuns == 0;

    /// <summary>
    /// Generation-equivalence: true when a run generated on <paramref name="other"/> is identical to
    /// one generated on this state. Epochs are compared exactly (they filter card/potion/relic pools
    /// and gate alternate acts and ancients); the run count only as "first run or not" (every
    /// generation read is an <c>== 0</c> clause); and <c>encounters_seen</c> only through the
    /// seen-status of <see cref="DiscoveryBosses"/>. Inert differences are returned as warnings.
    /// </summary>
    public bool Matches(ScriptUnlocks other, out string difference) => Matches(other, out difference, out _);

    /// <inheritdoc cref="Matches(ScriptUnlocks, out string)"/>
    public bool Matches(ScriptUnlocks other, out string difference, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(other);
        List<string> inert = [];
        warnings = inert;
        if (IsFirstRun != other.IsFirstRun)
        {
            difference = $"number_of_runs: expected {(IsFirstRun ? "a first run (0)" : $"a later run ({NumberOfRuns})")}, live {other.NumberOfRuns}";
            return false;
        }

        if (NumberOfRuns != other.NumberOfRuns)
        {
            inert.Add($"number_of_runs: script {NumberOfRuns}, live {other.NumberOfRuns} (both past the first run; no effect on generation)");
        }

        if (!SetEquals(Epochs, other.Epochs, out string epochDifference))
        {
            difference = $"epochs: {epochDifference}";
            return false;
        }

        HashSet<string> bosses = new(DiscoveryBosses, StringComparer.Ordinal);
        if (!SetEquals(EncountersSeen.Where(bosses.Contains), other.EncountersSeen.Where(bosses.Contains), out string bossDifference))
        {
            difference = $"encounters_seen (discovery bosses): {bossDifference}";
            return false;
        }

        if (!SetEquals(EncountersSeen.Where(id => !bosses.Contains(id)), other.EncountersSeen.Where(id => !bosses.Contains(id)), out string otherDifference))
        {
            inert.Add($"encounters_seen: {otherDifference} (not read during generation; no effect)");
        }

        difference = string.Empty;
        return true;
    }

    private static bool SetEquals(IEnumerable<string> expected, IEnumerable<string> live, out string difference)
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

    /// <summary>
    /// True when the next decision continues this one on the same selection screen, so that
    /// answering this one on its own would be answering the screen early.
    /// </summary>
    /// <remarks>
    /// A screen that wants several cards out of a large pile is authored one card at a time: each
    /// decision names the selection so far plus one more, and every one of them carries the same
    /// <c>choice_id</c>. The game takes the whole selection as one interaction and records it as one
    /// answer, so a driver presses the cards the last decision of the run names and confirms once.
    /// </remarks>
    public bool ContinuedByNext { get; private set; }

    internal void MarkContinuedByNext() => ContinuedByNext = true;

    /// <summary>
    /// Whether <paramref name="next"/> is the same selection screen carried one card further:
    /// the same kind and choice, and a card list that keeps this one's cards, in order, and adds to
    /// them. A decision that drops or reorders a card is a different answer, not a continuation.
    /// </summary>
    internal static bool IsContinuedBy(ScriptDecision current, ScriptDecision next)
    {
        if (current.Kind != "choice.cards" || next.Kind != "choice.cards")
        {
            return false;
        }

        if (!ScriptDecisionReader.TryGetInt(current.Data, "choice_id", out int currentChoice)
            || !ScriptDecisionReader.TryGetInt(next.Data, "choice_id", out int nextChoice)
            || currentChoice != nextChoice)
        {
            return false;
        }

        if (!TryGetCards(current.Data, out JsonElement currentCards)
            || !TryGetCards(next.Data, out JsonElement nextCards)
            || nextCards.GetArrayLength() <= currentCards.GetArrayLength())
        {
            return false;
        }

        JsonElement.ArrayEnumerator ahead = nextCards.EnumerateArray();
        foreach (JsonElement card in currentCards.EnumerateArray())
        {
            if (!ahead.MoveNext() || !SameCard(card, ahead.Current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetCards(JsonElement data, out JsonElement cards)
    {
        cards = default;
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("cards", out cards)
            && cards.ValueKind == JsonValueKind.Array;
    }

    private static bool SameCard(JsonElement left, JsonElement right)
    {
        try
        {
            return string.Equals(
                CanonicalJson.Serialize(left),
                CanonicalJson.Serialize(right),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
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

        for (int index = 0; index + 1 < decisions.Count; index++)
        {
            if (ScriptDecision.IsContinuedBy(decisions[index], decisions[index + 1]))
            {
                decisions[index].MarkContinuedByNext();
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
