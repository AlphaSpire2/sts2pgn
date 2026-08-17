using System.Globalization;
using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Serialization;

namespace STS2PGN.Validation;

public static class Sts2PgnValidator
{
    private static readonly string[] RequiredTags =
    [
        "Format",
        "FormatVersion",
        "RecorderVersion",
        "RunId",
        "GameVersion",
        "GameCommit",
        "ModelIdHash",
        "Seed",
        "Mode",
        "Ascension",
        "Players",
        "StartedAt",
        "Result",
    ];

    private static readonly IReadOnlyDictionary<string, string> ResolveKinds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reward.resolve"] = "reward.claim",
            ["rest.resolve"] = "rest.choose",
            ["shop.purchase.resolve"] = "shop.purchase",
            ["shop.remove_card.resolve"] = "shop.remove_card",
        };

    private static readonly IReadOnlySet<string> PlayerDecisionKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "act.next",
        "choice.cards",
        "choice.index",
        "choice.player",
        "choice.unknown",
        "combat.discard_potion",
        "combat.end_turn",
        "combat.play_card",
        "combat.undo_end_turn",
        "combat.use_potion",
        "event.advance",
        "event.choose",
        "event.crystal_sphere.click",
        "event.crystal_sphere.proceed",
        "event.crystal_sphere.set_tool",
        "game.unknown_action",
        "map.choose",
        "rest.choose",
        "rest.resolve",
        "reward.claim",
        "reward.resolve",
        "reward.skip",
        "room.proceed",
        "shop.purchase",
        "shop.purchase.resolve",
        "shop.remove_card",
        "shop.remove_card.resolve",
        "treasure.choose_relic",
        "treasure.skip_relic",
    };

    public static Sts2PgnValidationResult Validate(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<ValidationIssue> issues = [];

        ValidateTags(record, issues);
        ValidateEntries(record, issues);

        if (record.RecoveredTruncatedFinalLine)
        {
            AddWarning(issues, "recovered_truncated_line", "The unterminated final line was discarded during recovery.");
        }

        return new Sts2PgnValidationResult(issues);
    }

    private static void ValidateTags(RunRecord record, List<ValidationIssue> issues)
    {
        foreach (IGrouping<string, TagPair> duplicate in record.Metadata.Tags.GroupBy(tag => tag.Name, StringComparer.Ordinal))
        {
            if (!FormatSyntax.IsTagName(duplicate.Key))
            {
                AddError(issues, "invalid_tag_name", $"'{duplicate.Key}' is not a valid tag name.", $"tag:{duplicate.Key}");
            }

            if (duplicate.Count() > 1)
            {
                AddError(issues, "duplicate_tag", $"Tag '{duplicate.Key}' occurs more than once.", $"tag:{duplicate.Key}");
            }

            foreach (TagPair tag in duplicate)
            {
                foreach (char character in tag.Value)
                {
                    if (character >= ' ' || character is '\n' or '\r' or '\t')
                    {
                        continue;
                    }

                    AddError(issues, "invalid_tag_value", $"Tag '{tag.Name}' contains unsupported control character U+{(int)character:X4}.", $"tag:{tag.Name}");
                    break;
                }
            }
        }

        foreach (string required in RequiredTags)
        {
            if (record.Metadata.GetValue(required) is null)
            {
                AddError(issues, "missing_tag", $"Required tag '{required}' is missing.", $"tag:{required}");
            }
        }

        if (record.Metadata.GetValue("Format") is { } format && format != FormatConstants.Name)
        {
            AddError(issues, "invalid_format", $"Format must be '{FormatConstants.Name}'.", "tag:Format");
        }

        if (record.Metadata.GetValue("FormatVersion") is { } versionText
            && (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
                || version != FormatConstants.CurrentVersion))
        {
            AddError(
                issues,
                "unsupported_format_version",
                $"FormatVersion must be '{FormatConstants.CurrentVersion}'.",
                "tag:FormatVersion");
        }

        if (record.Metadata.GetValue("ModelIdHash") is { } modelHash
            && !uint.TryParse(modelHash, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            AddError(issues, "invalid_model_hash", "ModelIdHash must be an unsigned decimal integer.", "tag:ModelIdHash");
        }

        if (record.Metadata.GetValue("Mode") is { } mode && mode is not ("STANDARD" or "DAILY" or "CUSTOM"))
        {
            AddError(issues, "invalid_mode", "Mode must be STANDARD, DAILY, or CUSTOM.", "tag:Mode");
        }

        if (record.Metadata.GetValue("Ascension") is { } ascension
            && (!int.TryParse(ascension, NumberStyles.None, CultureInfo.InvariantCulture, out int level) || level < 0))
        {
            AddError(issues, "invalid_ascension", "Ascension must be a non-negative decimal integer.", "tag:Ascension");
        }

        if (record.Metadata.GetValue("StartedAt") is { } startedAt
            && !DateTimeOffset.TryParse(
                startedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            AddError(issues, "invalid_started_at", "StartedAt must use the round-trip ISO-8601 format.", "tag:StartedAt");
        }

        if (record.Metadata.GetValue("Players") is { } players)
        {
            ValidatePlayersTag(players, issues);
        }

        if (record.Metadata.GetValue("Result") is { } resultText)
        {
            if (!RunResultExtensions.TryParseToken(resultText, out RunResult headerResult))
            {
                AddError(issues, "invalid_result", $"Unknown Result token '{resultText}'.", "tag:Result");
            }
            else if (record.Result == RunResult.Incomplete && headerResult != RunResult.Incomplete)
            {
                AddError(issues, "missing_terminal_result", "A finalized Result tag requires a matching terminal result.", "result");
            }
            else if (record.Result != RunResult.Incomplete
                     && headerResult != RunResult.Incomplete
                     && headerResult != record.Result)
            {
                AddError(issues, "result_mismatch", "The Result tag and terminal result disagree.", "result");
            }
            else if (record.Result != RunResult.Incomplete && headerResult == RunResult.Incomplete)
            {
                AddWarning(issues, "result_header_not_finalized", "The terminal result is authoritative, but the Result tag is still '*'.", "tag:Result");
            }
        }
    }

    private static void ValidatePlayersTag(string players, List<ValidationIssue> issues)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(players);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                AddError(issues, "invalid_players", "Players must contain a JSON array.", "tag:Players");
                return;
            }

            HashSet<string> slots = new(StringComparer.Ordinal);
            foreach (JsonElement player in document.RootElement.EnumerateArray())
            {
                if (player.ValueKind != JsonValueKind.Object
                    || !player.TryGetProperty("slot", out JsonElement slotElement)
                    || slotElement.ValueKind != JsonValueKind.String
                    || !player.TryGetProperty("character", out JsonElement characterElement)
                    || characterElement.ValueKind != JsonValueKind.String)
                {
                    AddError(issues, "invalid_players", "Every player needs string 'slot' and 'character' properties.", "tag:Players");
                    return;
                }

                string slot = slotElement.GetString()!;
                if (!slots.Add(slot))
                {
                    AddError(issues, "duplicate_player_slot", $"Player slot '{slot}' occurs more than once.", "tag:Players");
                }
            }

            if (slots.Count == 0)
            {
                AddError(issues, "invalid_players", "Players must contain at least one player.", "tag:Players");
            }
        }
        catch (JsonException exception)
        {
            AddError(issues, "invalid_players", $"Players contains invalid JSON: {exception.Message}", "tag:Players");
        }
    }

    private static void ValidateEntries(RunRecord record, List<ValidationIssue> issues)
    {
        if (record.Entries.Count == 0)
        {
            AddError(issues, "missing_run_start", "The record must begin with run.start.", "records");
            return;
        }

        if (record.Entries[0].Kind != "run.start")
        {
            AddError(issues, "missing_run_start", "The first record must be run.start.", $"sequence:{record.Entries[0].Sequence}");
        }

        int runStartCount = record.Entries.Count(entry => entry.Kind == "run.start");
        if (runStartCount != 1)
        {
            AddError(issues, "run_start_count", $"Expected exactly one run.start record, found {runStartCount}.", "records");
        }

        HashSet<long> sequences = record.Entries.Select(entry => entry.Sequence).ToHashSet();
        Dictionary<long, RunRecordEntry> entriesBySequence = record.Entries
            .GroupBy(entry => entry.Sequence)
            .ToDictionary(group => group.Key, group => group.First());
        HashSet<long> resolvedActions = [];
        for (int index = 0; index < record.Entries.Count; index++)
        {
            RunRecordEntry entry = record.Entries[index];
            long expectedSequence = index + 1L;
            if (entry.Sequence != expectedSequence)
            {
                AddError(
                    issues,
                    "invalid_sequence",
                    $"Expected sequence {expectedSequence}, found {entry.Sequence}.",
                    $"sequence:{entry.Sequence}");
            }

            if (!FormatSyntax.IsRecordKind(entry.Kind))
            {
                AddError(issues, "invalid_record_kind", $"Invalid record kind '{entry.Kind}'.", $"sequence:{entry.Sequence}");
            }

            ValidateActionContract(entry, issues);

            if (entry.Kind == "state.observe")
            {
                ValidateObservation(entry, sequences, issues);
            }

            if (ResolveKinds.TryGetValue(entry.Kind, out string? expectedActionKind))
            {
                ValidateResolve(entry, expectedActionKind, entriesBySequence, resolvedActions, issues);
            }
        }
    }

    private static void ValidateActionContract(RunRecordEntry entry, List<ValidationIssue> issues)
    {
        if (PlayerDecisionKinds.Contains(entry.Kind))
        {
            ValidateActor(entry, issues);
        }

        if (entry.Kind is "combat.play_card" or "combat.use_potion")
        {
            ValidateCombatTarget(entry, issues);
        }

        if (entry.Kind == "shop.purchase")
        {
            ValidateShopPurchaseIdentity(entry, issues);
        }

        if (entry.Kind == "choice.index")
        {
            ValidateCardRewardOffer(entry, issues);
        }
    }

    private static void ValidateActor(RunRecordEntry entry, List<ValidationIssue> issues)
    {
        if (!entry.Payload.TryGetProperty("actor", out JsonElement actor)
            || actor.ValueKind != JsonValueKind.String
            || !IsPlayerSlot(actor.GetString()!))
        {
            AddError(
                issues,
                "missing_action_actor",
                $"{entry.Kind} requires an actor player slot such as 'p0'.",
                $"sequence:{entry.Sequence}");
        }
    }

    private static void ValidateCombatTarget(RunRecordEntry entry, List<ValidationIssue> issues)
    {
        string location = $"sequence:{entry.Sequence}";
        if (!TryGetData(entry, out JsonElement data)
            || !data.TryGetProperty("target_id", out JsonElement targetId))
        {
            AddError(issues, "invalid_combat_target", $"{entry.Kind} requires data.target_id.", location);
            return;
        }

        if (targetId.ValueKind == JsonValueKind.Null)
        {
            if (!data.TryGetProperty("target", out JsonElement nullTarget)
                || nullTarget.ValueKind != JsonValueKind.Null)
            {
                AddError(
                    issues,
                    "invalid_combat_target",
                    $"{entry.Kind} must use a null target when target_id is null.",
                    location);
            }

            return;
        }

        if (targetId.ValueKind != JsonValueKind.Number
            || !targetId.TryGetUInt32(out uint combatId)
            || !data.TryGetProperty("target", out JsonElement target)
            || target.ValueKind != JsonValueKind.Object
            || !target.TryGetProperty("combat_id", out JsonElement targetCombatId)
            || targetCombatId.ValueKind != JsonValueKind.Number
            || !targetCombatId.TryGetUInt32(out uint fingerprintCombatId)
            || combatId != fingerprintCombatId
            || !target.TryGetProperty("model_id", out JsonElement modelId)
            || modelId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(modelId.GetString()))
        {
            AddError(
                issues,
                "invalid_combat_target",
                $"{entry.Kind} requires a target fingerprint with matching combat_id and non-empty model_id.",
                location);
        }
    }

    private static void ValidateShopPurchaseIdentity(RunRecordEntry entry, List<ValidationIssue> issues)
    {
        string location = $"sequence:{entry.Sequence}";
        if (!TryGetData(entry, out JsonElement data)
            || !HasNonNegativeInteger(data, "inventory_index")
            || !HasNonNegativeInteger(data, "entry_index")
            || !HasNonEmptyString(data, "entry_group")
            || !HasNonEmptyString(data, "entry_type")
            || !HasNonEmptyString(data, "model_id")
            || !HasValidShopEntryTypeAndGroup(data))
        {
            AddError(
                issues,
                "invalid_shop_identity",
                "shop.purchase requires inventory/category indexes, a matching card/relic/potion type and group, and model_id.",
                location);
        }
    }

    private static bool HasValidShopEntryTypeAndGroup(JsonElement data)
    {
        string? entryType = data.GetProperty("entry_type").GetString();
        string? entryGroup = data.GetProperty("entry_group").GetString();
        return (entryType, entryGroup) switch
        {
            ("card", "character_card" or "colorless_card") => true,
            ("relic", "relic") => true,
            ("potion", "potion") => true,
            _ => false,
        };
    }

    private static void ValidateCardRewardOffer(RunRecordEntry entry, List<ValidationIssue> issues)
    {
        if (!TryGetData(entry, out JsonElement data)
            || !data.TryGetProperty("offered", out JsonElement offered)
            || offered.ValueKind != JsonValueKind.Object
            || !offered.TryGetProperty("context", out JsonElement context)
            || context.ValueKind != JsonValueKind.String
            || context.GetString() != "card_reward")
        {
            return;
        }

        string location = $"sequence:{entry.Sequence}";
        if (!offered.TryGetProperty("options", out JsonElement options)
            || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("cards", out JsonElement cards)
            || cards.ValueKind != JsonValueKind.Array
            || !options.TryGetProperty("alternatives", out JsonElement alternatives)
            || alternatives.ValueKind != JsonValueKind.Array)
        {
            AddError(
                issues,
                "invalid_card_reward_offer",
                "A card_reward offer requires card and alternative arrays with explicit indexes.",
                location);
            return;
        }

        HashSet<int> offeredIndexes = [];
        bool offerIsValid = true;
        int expectedIndex = 0;
        foreach (JsonElement card in cards.EnumerateArray())
        {
            offerIsValid &= TryAddOfferIndex(card, expectedIndex, offeredIndexes);
            expectedIndex++;
        }

        foreach (JsonElement alternative in alternatives.EnumerateArray())
        {
            offerIsValid &= TryAddOfferIndex(alternative, expectedIndex, offeredIndexes)
                && HasNonEmptyString(alternative, "option_id");
            expectedIndex++;
        }

        if (!offerIsValid)
        {
            AddError(
                issues,
                "invalid_card_reward_offer",
                "Card-reward cards and alternatives require contiguous indexes in presentation order; alternatives also require option_id.",
                location);
        }

        if (!data.TryGetProperty("indexes", out JsonElement selectedIndexes)
            || selectedIndexes.ValueKind != JsonValueKind.Array)
        {
            AddError(issues, "invalid_choice_indexes", "choice.index requires an indexes array.", location);
            return;
        }

        foreach (JsonElement selected in selectedIndexes.EnumerateArray())
        {
            if (selected.ValueKind != JsonValueKind.Number
                || !selected.TryGetInt32(out int selectedIndex)
                || !offeredIndexes.Contains(selectedIndex))
            {
                AddError(
                    issues,
                    "unmapped_choice_index",
                    "Every selected card-reward index must identify an explicitly recorded card or alternative.",
                    location);
                break;
            }
        }
    }

    private static bool TryGetData(RunRecordEntry entry, out JsonElement data) =>
        entry.Payload.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;

    private static bool TryAddOfferIndex(JsonElement option, int expectedIndex, HashSet<int> indexes) =>
        option.ValueKind == JsonValueKind.Object
        && option.TryGetProperty("index", out JsonElement indexElement)
        && indexElement.ValueKind == JsonValueKind.Number
        && indexElement.TryGetInt32(out int index)
        && index == expectedIndex
        && indexes.Add(index);

    private static bool HasNonNegativeInteger(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
        && number >= 0;

    private static bool HasNonEmptyString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool IsPlayerSlot(string value) =>
        value.Length >= 2
        && value[0] == 'p'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789") < 0;

    private static void ValidateResolve(
        RunRecordEntry entry,
        string expectedActionKind,
        IReadOnlyDictionary<long, RunRecordEntry> entriesBySequence,
        HashSet<long> resolvedActions,
        List<ValidationIssue> issues)
    {
        string location = $"sequence:{entry.Sequence}";
        if (!entry.Payload.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("action_sequence", out JsonElement actionElement)
            || !actionElement.TryGetInt64(out long actionSequence)
            || actionSequence <= 0
            || actionSequence >= entry.Sequence
            || !entriesBySequence.TryGetValue(actionSequence, out RunRecordEntry? action))
        {
            AddError(
                issues,
                "invalid_resolve_reference",
                $"{entry.Kind} must reference an earlier action in data.action_sequence.",
                location);
            return;
        }

        if (!string.Equals(action.Kind, expectedActionKind, StringComparison.Ordinal))
        {
            AddError(
                issues,
                "resolve_kind_mismatch",
                $"{entry.Kind} references {action.Kind}; expected {expectedActionKind}.",
                location);
        }

        if (!data.TryGetProperty("success", out JsonElement success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddError(
                issues,
                "invalid_resolve_success",
                $"{entry.Kind} requires a boolean data.success value.",
                location);
        }

        if (!resolvedActions.Add(actionSequence))
        {
            AddError(
                issues,
                "duplicate_resolve",
                $"Action sequence {actionSequence} has more than one resolve record.",
                location);
        }
    }

    private static void ValidateObservation(
        RunRecordEntry entry,
        IReadOnlySet<long> sequences,
        List<ValidationIssue> issues)
    {
        string location = $"sequence:{entry.Sequence}";
        if (!entry.Payload.TryGetProperty("after", out JsonElement afterElement)
            || !afterElement.TryGetInt64(out long after)
            || after <= 0
            || after >= entry.Sequence
            || !sequences.Contains(after))
        {
            AddError(issues, "invalid_observation_after", "state.observe must reference an earlier sequence in 'after'.", location);
        }

        if (!entry.Payload.TryGetProperty("context", out JsonElement context)
            || context.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(context.GetString()))
        {
            AddError(issues, "invalid_observation_context", "state.observe requires a non-empty string context.", location);
        }

        if (!entry.Payload.TryGetProperty("projection_version", out JsonElement projectionVersion)
            || !projectionVersion.TryGetInt32(out int version)
            || version <= 0)
        {
            AddError(issues, "invalid_projection_version", "state.observe requires a positive projection_version.", location);
        }

        string? recordedDigest = null;
        if (!entry.Payload.TryGetProperty("digest", out JsonElement digestElement)
            || digestElement.ValueKind != JsonValueKind.String
            || !IsSha256Digest(digestElement.GetString()!))
        {
            AddError(issues, "invalid_digest", "state.observe requires a lowercase sha256: digest.", location);
        }
        else
        {
            recordedDigest = digestElement.GetString();
        }

        bool hasValidState = true;
        if (!entry.Payload.TryGetProperty("state", out JsonElement state)
            || state.ValueKind != JsonValueKind.Object)
        {
            hasValidState = false;
            AddError(issues, "invalid_observation_state", "state.observe requires an object state projection.", location);
        }

        if (recordedDigest is not null && hasValidState)
        {
            try
            {
                string computedDigest = CanonicalJson.ComputeSha256(state);
                if (!string.Equals(recordedDigest, computedDigest, StringComparison.Ordinal))
                {
                    AddError(issues, "digest_mismatch", "state.observe digest does not match its canonical state.", location);
                }
            }
            catch (ArgumentException exception)
            {
                AddError(issues, "uncanonicalizable_state", exception.Message, location);
            }
        }
    }

    private static bool IsSha256Digest(string value) =>
        value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void AddError(List<ValidationIssue> issues, string code, string message, string? location = null) =>
        issues.Add(new ValidationIssue(code, message, ValidationSeverity.Error, location));

    private static void AddWarning(List<ValidationIssue> issues, string code, string message, string? location = null) =>
        issues.Add(new ValidationIssue(code, message, ValidationSeverity.Warning, location));
}
