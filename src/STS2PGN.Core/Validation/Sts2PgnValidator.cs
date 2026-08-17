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

            if (entry.Kind == "state.observe")
            {
                ValidateObservation(entry, sequences, issues);
            }
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
