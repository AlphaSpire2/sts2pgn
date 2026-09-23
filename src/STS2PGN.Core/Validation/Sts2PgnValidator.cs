using System.Globalization;
using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Scripting;
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

    /// <summary>Validates an authoritative recording (equivalent to <see cref="Sts2PgnValidationMode.Recording"/>).</summary>
    public static Sts2PgnValidationResult Validate(RunRecord record) =>
        Validate(record, Sts2PgnValidationMode.Recording);

    /// <summary>Validates a decoded trace against the recording or decision-script profile.</summary>
    public static Sts2PgnValidationResult Validate(RunRecord record, Sts2PgnValidationMode mode)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<ValidationIssue> issues = [];

        ValidateTags(record, issues, mode);
        ValidateEntries(record, issues, mode);
        if (mode == Sts2PgnValidationMode.Script)
        {
            ValidateScriptEntries(record, issues);
        }

        if (record.RecoveredTruncatedFinalLine)
        {
            AddWarning(issues, "recovered_truncated_line", "The unterminated final line was discarded during recovery.");
        }

        return new Sts2PgnValidationResult(issues);
    }

    private static void ValidateTags(RunRecord record, List<ValidationIssue> issues, Sts2PgnValidationMode mode)
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

        // Producer gets its own script-profile code below instead of the generic missing_tag.
        IEnumerable<string> requiredTags = mode == Sts2PgnValidationMode.Script
            ? ScriptProfile.RequiredTags.Where(tag => tag != ScriptProfile.ProducerTag)
            : RequiredTags;
        foreach (string required in requiredTags)
        {
            if (record.Metadata.GetValue(required) is null)
            {
                AddError(issues, "missing_tag", $"Required tag '{required}' is missing.", $"tag:{required}");
            }
        }

        ValidateProfileTags(record, issues, mode);

        if (record.Metadata.GetValue("Format") is { } format && format != FormatConstants.Name)
        {
            AddError(issues, "invalid_format", $"Format must be '{FormatConstants.Name}'.", "tag:Format");
        }

        if (record.Metadata.GetValue("FormatVersion") is { } versionText
            && (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
                || !FormatConstants.IsSupportedVersion(version)))
        {
            AddError(
                issues,
                "unsupported_format_version",
                "FormatVersion must be '1' or '2'.",
                "tag:FormatVersion");
        }

        if (record.Metadata.GetValue("ModelIdHash") is { } modelHash
            && !uint.TryParse(modelHash, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            AddError(issues, "invalid_model_hash", "ModelIdHash must be an unsigned decimal integer.", "tag:ModelIdHash");
        }

        if (record.Metadata.GetValue("Mode") is { } modeText && modeText is not ("STANDARD" or "DAILY" or "CUSTOM"))
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

    private static void ValidateProfileTags(RunRecord record, List<ValidationIssue> issues, Sts2PgnValidationMode mode)
    {
        string? producer = record.Metadata.GetValue(ScriptProfile.ProducerTag);
        string? recorderVersion = record.Metadata.GetValue(ScriptProfile.RecorderVersionTag);
        if (mode == Sts2PgnValidationMode.Recording)
        {
            if (producer is not null)
            {
                AddError(
                    issues,
                    "recording_carries_producer",
                    "A recording must not carry a Producer tag; files with Producer are decision scripts.",
                    "tag:Producer");
            }

            return;
        }

        if (producer is null)
        {
            AddError(issues, "script_missing_producer", "A decision script requires a Producer tag.", "tag:Producer");
        }
        else if (!ScriptProfile.TryParseProducer(producer, out _, out _))
        {
            AddError(
                issues,
                "script_invalid_producer",
                "Producer must have the form 'name/version' with a lowercase name.",
                "tag:Producer");
        }

        if (recorderVersion is not null)
        {
            AddError(
                issues,
                "script_carries_recorder_version",
                "A decision script must not carry RecorderVersion; that tag marks an authoritative recording.",
                "tag:RecorderVersion");
        }

        if (record.Metadata.GetValue("Mode") is { } scriptMode && scriptMode != ScriptProfile.RequiredMode)
        {
            AddError(
                issues,
                "script_mode_not_custom",
                $"A decision script must use Mode {ScriptProfile.RequiredMode}; it is the only seeded mode the game starts.",
                "tag:Mode");
        }

        if (record.Metadata.GetValue("Result") is { } resultText
            && RunResultExtensions.TryParseToken(resultText, out RunResult headerResult)
            && headerResult != RunResult.Incomplete)
        {
            AddError(issues, "script_result_not_incomplete", "A decision script's Result tag must be '*'.", "tag:Result");
        }

        if (record.Result != RunResult.Incomplete)
        {
            AddError(issues, "script_result_not_incomplete", "A decision script must end with the '*' terminal token.", "result");
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

    private static void ValidateEntries(RunRecord record, List<ValidationIssue> issues, Sts2PgnValidationMode mode)
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

            if (entry.Kind == "state.delta")
            {
                AddError(issues, "undecoded_state_delta", "Wire deltas must be decoded by Sts2PgnParser before validation or replay.", $"sequence:{entry.Sequence}");
            }

            if (entry.Kind == "state.observe")
            {
                ValidateObservation(entry, entriesBySequence, issues);
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

    /// <summary>
    /// Decision-script profile checks layered on top of the shared grammar checks: the run.start must
    /// carry the expected unlock state and acts, every decision must carry complete semantic identity,
    /// and nested choices must follow an operation that can suspend for them.
    /// </summary>
    private static void ValidateScriptEntries(RunRecord record, List<ValidationIssue> issues)
    {
        if (record.Entries.Count > 0 && record.Entries[0].Kind == "run.start")
        {
            ValidateScriptRunStart(record.Entries[0], issues);
        }

        bool choiceAllowed = false;
        foreach (RunRecordEntry entry in record.Entries.Skip(1))
        {
            string location = $"sequence:{entry.Sequence}";
            if (ScriptProfile.ForbiddenKinds.Contains(entry.Kind))
            {
                if (entry.Kind == "run.resume")
                {
                    AddError(
                        issues,
                        "script_resume_not_supported",
                        "The playback driver starts a new seeded run and cannot restore the save checkpoint named by run.resume; resumed recordings cannot be converted to or driven as decision scripts.",
                        location);
                    continue;
                }

                AddError(
                    issues,
                    "script_unsupported_record",
                    $"{entry.Kind} carries no drivable identity and is not allowed in a decision script.",
                    location);
                continue;
            }

            if (ScriptProfile.IgnoredKinds.Contains(entry.Kind))
            {
                AddWarning(
                    issues,
                    "script_ignored_record",
                    $"{entry.Kind} is not a decision; a playback driver ignores it.",
                    location);
                continue;
            }

            if (entry.Kind == "run.start")
            {
                continue;
            }

            if (entry.Kind.StartsWith("choice.", StringComparison.Ordinal))
            {
                if (!choiceAllowed)
                {
                    AddError(
                        issues,
                        "script_orphan_choice",
                        $"{entry.Kind} must follow an operation that suspends for a nested choice.",
                        location);
                }
            }
            else
            {
                choiceAllowed = ScriptProfile.SuspendableKinds.Contains(entry.Kind);
            }

            ValidateScriptDecisionIdentity(entry, issues);
        }
    }

    private static void ValidateScriptRunStart(RunRecordEntry runStart, List<ValidationIssue> issues)
    {
        string location = $"sequence:{runStart.Sequence}";
        if (!TryGetData(runStart, out JsonElement data)
            || !data.TryGetProperty("state", out JsonElement state)
            || state.ValueKind != JsonValueKind.Object)
        {
            AddError(issues, "script_missing_unlocks", "A decision script's run.start requires data.state.", location);
            AddError(issues, "script_missing_acts", "A decision script's run.start requires data.state.acts.", location);
            return;
        }

        if (!state.TryGetProperty("acts", out JsonElement acts)
            || acts.ValueKind != JsonValueKind.Array
            || !HasValidActs(acts))
        {
            AddError(
                issues,
                "script_missing_acts",
                "run.start data.state.acts must list every act with index and model_id.",
                location);
        }

        if (!state.TryGetProperty("players", out JsonElement players)
            || players.ValueKind != JsonValueKind.Array
            || players.GetArrayLength() == 0
            || players[0].ValueKind != JsonValueKind.Object
            || !players[0].TryGetProperty("unlocks", out JsonElement unlocks)
            || unlocks.ValueKind != JsonValueKind.Object)
        {
            AddError(
                issues,
                "script_missing_unlocks",
                "run.start data.state.players[0].unlocks is required so the driver can verify the live profile.",
                location);
            return;
        }

        if (!IsStringArray(unlocks, "epochs")
            || !IsStringArray(unlocks, "encounters_seen")
            || !HasNonNegativeInteger(unlocks, "number_of_runs"))
        {
            AddError(
                issues,
                "script_invalid_unlocks",
                "unlocks requires string arrays epochs and encounters_seen plus a non-negative number_of_runs.",
                location);
        }
    }

    private static void ValidateScriptDecisionIdentity(RunRecordEntry entry, List<ValidationIssue> issues)
    {
        string location = $"sequence:{entry.Sequence}";
        if (!TryGetData(entry, out JsonElement data))
        {
            AddError(issues, "script_missing_identity", $"{entry.Kind} requires a data object.", location);
            return;
        }

        List<string> missing = [];
        void Require(bool present, string field)
        {
            if (!present)
            {
                missing.Add(field);
            }
        }

        switch (entry.Kind)
        {
            case "combat.play_card":
                Require(HasNonNegativeInteger(data, "combat_card_index"), "combat_card_index");
                Require(HasNonEmptyString(data, "card_model"), "card_model");
                Require(HasObject(data, "card"), "card");
                break;
            case "combat.use_potion":
            case "combat.discard_potion":
                Require(HasNonNegativeInteger(data, "potion_slot"), "potion_slot");
                Require(HasNonEmptyString(data, "potion_model"), "potion_model");
                if (data.TryGetProperty("in_combat", out JsonElement inCombat)
                    && inCombat.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    AddError(
                        issues,
                        "script_invalid_identity",
                        $"{entry.Kind} optional data.in_combat must be a boolean.",
                        location);
                }
                break;
            case "combat.end_turn":
                Require(HasInteger(data, "turn"), "turn");
                break;
            case "map.choose":
                Require(HasInteger(data, "col"), "col");
                Require(HasInteger(data, "row"), "row");
                break;
            case "treasure.choose_relic":
                Require(HasNonNegativeInteger(data, "relic_index"), "relic_index");
                Require(HasNonEmptyString(data, "relic_model"), "relic_model");
                Require(HasArray(data, "offered_relics"), "offered_relics");
                break;
            case "reward.claim":
                Require(HasInteger(data, "set_id"), "set_id");
                Require(HasNonNegativeInteger(data, "reward_index"), "reward_index");
                Require(HasObject(data, "reward"), "reward");
                break;
            case "reward.skip":
                Require(HasInteger(data, "set_id"), "set_id");
                break;
            case "event.choose":
                Require(HasNonNegativeInteger(data, "option_index"), "option_index");
                Require(HasNonEmptyString(data, "option_id"), "option_id");
                break;
            case "event.crystal_sphere.click":
                Require(HasNonNegativeInteger(data, "x"), "x");
                Require(HasNonNegativeInteger(data, "y"), "y");
                Require(HasCrystalSphereTool(data), "tool");
                Require(HasNonNegativeInteger(data, "remaining_before"), "remaining_before");
                break;
            case "event.crystal_sphere.set_tool":
                Require(HasCrystalSphereTool(data), "tool");
                break;
            case "rest.choose":
                Require(HasNonEmptyString(data, "option_id"), "option_id");
                break;
            case "shop.remove_card":
                Require(HasInteger(data, "cost"), "cost");
                break;
            case "choice.index":
                if (!ScriptDecisionReader.TryGetOffered(data, out _, out _))
                {
                    AddError(
                        issues,
                        "script_choice_missing_offer",
                        "choice.index requires data.offered with context and options so the driver can verify the live offer.",
                        location);
                }

                Require(HasArray(data, "indexes"), "indexes");
                break;
            case "choice.cards":
                ValidateScriptCardChoice(data, location, issues);
                break;
        }

        if (missing.Count > 0)
        {
            AddError(
                issues,
                "script_missing_identity",
                $"{entry.Kind} requires data.{string.Join(", data.", missing)}.",
                location);
        }
    }

    private static void ValidateScriptCardChoice(JsonElement data, string location, List<ValidationIssue> issues)
    {
        if (!data.TryGetProperty("cards", out JsonElement cards) || cards.ValueKind != JsonValueKind.Array)
        {
            AddError(issues, "script_choice_missing_fingerprint", "choice.cards requires a cards array.", location);
            return;
        }

        string? choiceType = HasNonEmptyString(data, "choice_type") ? data.GetProperty("choice_type").GetString() : null;
        foreach (JsonElement card in cards.EnumerateArray())
        {
            bool valid = card.ValueKind == JsonValueKind.Object && choiceType switch
            {
                "deck_card" => HasNonNegativeInteger(card, "deck_index") && HasObject(card, "fingerprint"),
                "combat_card" => HasNonNegativeInteger(card, "combat_card_index") && HasObject(card, "fingerprint"),
                _ => HasObject(card, "fingerprint") || HasNonEmptyString(card, "model_id"),
            };
            if (!valid)
            {
                AddError(
                    issues,
                    "script_choice_missing_fingerprint",
                    "Every choice.cards entry requires a fingerprint, plus deck_index or combat_card_index for deck and combat choices.",
                    location);
                return;
            }
        }
    }

    private static bool HasObject(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Object;

    private static bool HasArray(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Array;

    private static bool IsStringArray(JsonElement parent, string propertyName) =>
        HasArray(parent, propertyName)
        && parent.GetProperty(propertyName).EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);

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

    private static bool HasInteger(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out _);

    private static bool HasNonEmptyString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool HasCrystalSphereTool(JsonElement data) =>
        data.TryGetProperty("tool", out JsonElement tool)
        && tool.ValueKind == JsonValueKind.String
        && tool.GetString() is "small" or "big";

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
        IReadOnlyDictionary<long, RunRecordEntry> entriesBySequence,
        List<ValidationIssue> issues)
    {
        string location = $"sequence:{entry.Sequence}";
        long? afterSequence = null;
        if (!entry.Payload.TryGetProperty("after", out JsonElement afterElement)
            || afterElement.ValueKind != JsonValueKind.Number
            || !afterElement.TryGetInt64(out long after)
            || after <= 0
            || after >= entry.Sequence
            || !entriesBySequence.ContainsKey(after))
        {
            AddError(issues, "invalid_observation_after", "state.observe must reference an earlier sequence in 'after'.", location);
        }
        else
        {
            afterSequence = after;
        }

        string? observationContext = null;
        if (!entry.Payload.TryGetProperty("context", out JsonElement context)
            || context.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(context.GetString()))
        {
            AddError(issues, "invalid_observation_context", "state.observe requires a non-empty string context.", location);
        }
        else
        {
            observationContext = context.GetString();
        }

        if (!entry.Payload.TryGetProperty("projection_version", out JsonElement projectionVersion)
            || projectionVersion.ValueKind != JsonValueKind.Number
            || !projectionVersion.TryGetInt32(out int version)
            || version <= 0)
        {
            AddError(issues, "invalid_projection_version", "state.observe requires a positive projection_version.", location);
        }
        else if (version != FormatConstants.CurrentStateProjectionVersion)
        {
            AddError(
                issues,
                "unsupported_projection_version",
                $"State projection version {version} is not supported; expected {FormatConstants.CurrentStateProjectionVersion}.",
                location);
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

        if (observationContext == "run.start" && afterSequence.HasValue && hasValidState)
        {
            ValidateRunStartProjection(
                entry,
                entriesBySequence[afterSequence.Value],
                state,
                issues);
        }

        if (observationContext == "run.resume" && afterSequence.HasValue && hasValidState)
        {
            ValidateRunResumeProjection(
                entry,
                entriesBySequence[afterSequence.Value],
                state,
                issues);
        }


        if (hasValidState
            && state.TryGetProperty("scope", out JsonElement scope)
            && scope.ValueKind == JsonValueKind.String
            && scope.GetString() == "run.core")
        {
            ValidateLiveProjection(state, issues, location);
        }
    }

    private static void ValidateRunStartProjection(
        RunRecordEntry observation,
        RunRecordEntry observedEntry,
        JsonElement state,
        List<ValidationIssue> issues)
    {
        string location = $"sequence:{observation.Sequence}";
        if (observedEntry.Kind != "run.start")
        {
            AddError(
                issues,
                "invalid_run_start_observation",
                "A run.start observation must reference the run.start record.",
                location);
            return;
        }

        if (observation.Sequence != observedEntry.Sequence + 1)
        {
            AddError(
                issues,
                "invalid_run_start_observation",
                "The run.start observation must immediately follow run.start.",
                location);
        }

        if (!TryGetData(observedEntry, out JsonElement runStartData)
            || !runStartData.TryGetProperty("state", out JsonElement embeddedState)
            || embeddedState.ValueKind != JsonValueKind.Object)
        {
            AddError(
                issues,
                "missing_run_start_projection",
                "run.start requires data.state when followed by a run.start observation.",
                $"sequence:{observedEntry.Sequence}");
        }
        else
        {
            try
            {
                if (!string.Equals(
                    CanonicalJson.Serialize(embeddedState),
                    CanonicalJson.Serialize(state),
                    StringComparison.Ordinal))
                {
                    AddError(
                        issues,
                        "run_start_projection_mismatch",
                        "The run.start embedded state and its authoritative observation differ.",
                        location);
                }
            }
            catch (ArgumentException exception)
            {
                AddError(issues, "uncanonicalizable_run_start", exception.Message, location);
            }
        }

        if (!HasNonEmptyString(state, "scope")
            || state.GetProperty("scope").GetString() != "run_start.core"
            || !state.TryGetProperty("run", out JsonElement run)
            || run.ValueKind != JsonValueKind.Object
            || !HasNonEmptyString(run, "seed")
            || !HasNonEmptyString(run, "mode")
            || !HasInteger(run, "ascension")
            || !HasInteger(run, "act")
            || !HasInteger(run, "floor")
            || !state.TryGetProperty("acts", out JsonElement acts)
            || acts.ValueKind != JsonValueKind.Array
            || !HasValidActs(acts)
            || !state.TryGetProperty("players", out JsonElement players)
            || players.ValueKind != JsonValueKind.Array
            || !HasValidRunStartPlayers(players)
            || !state.TryGetProperty("rooms", out JsonElement rooms)
            || rooms.ValueKind != JsonValueKind.Array
            || !state.TryGetProperty("choices", out JsonElement choices)
            || !HasValidChoices(choices)
            || !state.TryGetProperty("special_event", out JsonElement specialEvent)
            || specialEvent.ValueKind != JsonValueKind.Null
            || !state.TryGetProperty("combat", out JsonElement combat)
            || combat.ValueKind != JsonValueKind.Null)
        {
            AddError(
                issues,
                "invalid_run_start_projection",
                "run.start state does not match projection version 1 scope run_start.core.",
                location);
        }
    }

    private static void ValidateRunResumeProjection(
        RunRecordEntry observation,
        RunRecordEntry observedEntry,
        JsonElement state,
        List<ValidationIssue> issues)
    {
        string location = $"sequence:{observation.Sequence}";
        if (observedEntry.Kind != "run.resume")
        {
            AddError(
                issues,
                "invalid_run_resume_observation",
                "A run.resume observation must reference the run.resume record.",
                location);
            return;
        }

        if (observation.Sequence != observedEntry.Sequence + 1)
        {
            AddError(
                issues,
                "invalid_run_resume_observation",
                "The run.resume observation must immediately follow run.resume.",
                location);
        }

        if (!TryGetData(observedEntry, out JsonElement resumeData)
            || !resumeData.TryGetProperty("state", out JsonElement embeddedState)
            || embeddedState.ValueKind != JsonValueKind.Object)
        {
            AddError(
                issues,
                "missing_run_resume_projection",
                "run.resume requires data.state when followed by a run.resume observation.",
                $"sequence:{observedEntry.Sequence}");
        }
        else
        {
            try
            {
                if (!string.Equals(
                    CanonicalJson.Serialize(embeddedState),
                    CanonicalJson.Serialize(state),
                    StringComparison.Ordinal))
                {
                    AddError(
                        issues,
                        "run_resume_projection_mismatch",
                        "The run.resume embedded state and its authoritative observation differ.",
                        location);
                }
            }
            catch (ArgumentException exception)
            {
                AddError(issues, "uncanonicalizable_run_resume", exception.Message, location);
            }
        }

        if (!HasNonEmptyString(state, "scope")
            || state.GetProperty("scope").GetString() != "run.core")
        {
            AddError(
                issues,
                "invalid_run_resume_projection",
                "run.resume state must use projection version 1 scope run.core.",
                location);
        }
    }

    private static void ValidateLiveProjection(
        JsonElement state,
        List<ValidationIssue> issues,
        string location)
    {
        if (!state.TryGetProperty("run", out JsonElement run)
            || run.ValueKind != JsonValueKind.Object
            || !HasNonEmptyString(run, "seed")
            || !HasInteger(run, "act")
            || !HasInteger(run, "floor")
            || !state.TryGetProperty("acts", out JsonElement acts)
            || acts.ValueKind != JsonValueKind.Array
            || !HasValidActs(acts)
            || !state.TryGetProperty("players", out JsonElement players)
            || players.ValueKind != JsonValueKind.Array
            || !HasValidRunStartPlayers(players)
            || !state.TryGetProperty("rooms", out JsonElement rooms)
            || rooms.ValueKind != JsonValueKind.Array
            || !state.TryGetProperty("choices", out JsonElement choices)
            || !HasValidChoices(choices)
            || !state.TryGetProperty("special_event", out JsonElement specialEvent)
            || specialEvent.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)
            || !state.TryGetProperty("combat", out JsonElement combat)
            || combat.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
        {
            AddError(
                issues,
                "invalid_live_projection",
                "state.observe does not match projection version 1 scope run.core.",
                location);
        }
    }

    private static bool HasValidActs(JsonElement acts)
    {
        HashSet<int> indexes = [];
        foreach (JsonElement act in acts.EnumerateArray())
        {
            if (act.ValueKind != JsonValueKind.Object
                || !HasInteger(act, "index")
                || !act.GetProperty("index").TryGetInt32(out int index)
                || !indexes.Add(index)
                || !HasNonEmptyString(act, "model_id"))
            {
                return false;
            }
        }

        return indexes.Count > 0;
    }

    private static bool HasValidChoices(JsonElement choices) =>
        choices.ValueKind == JsonValueKind.Object
        && choices.TryGetProperty("player_choices", out JsonElement playerChoices)
        && playerChoices.ValueKind == JsonValueKind.Array
        && choices.TryGetProperty("rewards", out JsonElement rewards)
        && rewards.ValueKind == JsonValueKind.Array;

    private static bool HasValidRunStartPlayers(JsonElement players)
    {
        HashSet<string> slots = new(StringComparer.Ordinal);
        foreach (JsonElement player in players.EnumerateArray())
        {
            if (player.ValueKind != JsonValueKind.Object
                || !HasNonEmptyString(player, "slot")
                || !HasNonEmptyString(player, "character_model"))
            {
                return false;
            }

            string slot = player.GetProperty("slot").GetString()!;
            if (!IsPlayerSlot(slot) || !slots.Add(slot))
            {
                return false;
            }
        }

        return slots.Count > 0;
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
