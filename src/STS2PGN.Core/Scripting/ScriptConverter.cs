using System.Text.Json;
using System.Text.Json.Nodes;
using STS2PGN.Model;
using STS2PGN.Validation;

namespace STS2PGN.Scripting;

/// <summary>
/// Turns an authoritative recording into a decision script that replays the same decisions:
/// the recorded run becomes a proposal the playback driver can execute again on a matching
/// build and profile. Observations, resolutions, lifecycle records, and <c>run.finish</c> are
/// dropped; <c>RecorderVersion</c> becomes <c>Producer</c>; <c>Mode</c> becomes <c>CUSTOM</c>
/// because that is the only mode in which the game accepts a chosen seed. A recording containing
/// <c>run.resume</c> is refused because the playback driver cannot restore its save checkpoint or
/// distinguish the abandoned tail from the resumed timeline.
/// </summary>
public static class ScriptConverter
{
    /// <summary>Tags a script does not carry (provenance of the original recording).</summary>
    private static readonly HashSet<string> DroppedTags = new(StringComparer.Ordinal)
    {
        ScriptProfile.RecorderVersionTag,
        "RunId",
        "StartedAt",
        "Result",
        "GameStartTime",
        "Platform",
        "GameSaveSchema",
        "ParentRunId",
        "DailyTime",
    };

    /// <summary>
    /// Converts a recording. The result is validated in script mode; a recording that carries
    /// records the profile forbids (unknown actions or undo end turn) fails with
    /// <see cref="Sts2PgnValidationException"/>.
    /// </summary>
    public static RunRecord FromRecording(RunRecord recording, string producer)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (!ScriptProfile.TryParseProducer(producer, out _, out _))
        {
            throw new ArgumentException("Producer must have the form 'name/version' with a lowercase name.", nameof(producer));
        }

        List<TagPair> tags = [];
        foreach (TagPair tag in recording.Metadata.Tags)
        {
            if (tag.Name == ScriptProfile.ProducerTag || DroppedTags.Contains(tag.Name))
            {
                continue;
            }

            tags.Add(tag.Name == "Mode" ? new TagPair("Mode", ScriptProfile.RequiredMode) : tag);
        }

        if (!tags.Any(tag => tag.Name == "Mode"))
        {
            tags.Add(new TagPair("Mode", ScriptProfile.RequiredMode));
        }

        // Keep the header readable: Producer right after FormatVersion, where RecorderVersion was.
        int formatVersionIndex = tags.FindIndex(tag => tag.Name == "FormatVersion");
        tags.Insert(formatVersionIndex < 0 ? 0 : formatVersionIndex + 1, new TagPair(ScriptProfile.ProducerTag, producer));

        RunRecordBuilder builder = new(tags);
        foreach (RunRecordEntry entry in recording.Entries)
        {
            if (entry.Kind == "run.start")
            {
                builder.Append(entry.Kind, StripRunStart(entry.Payload));
            }
            else if (ScriptProfile.DriveableKinds.Contains(entry.Kind)
                     || (ScriptProfile.ForbiddenKinds.Contains(entry.Kind) && entry.Kind != "run.finish"))
            {
                // Undrivable *decisions* (unknown actions and undo end turn) are kept so
                // validation names them instead of silently dropping a decision. run.resume is
                // also kept so conversion refuses a resumed recording instead of concatenating
                // its abandoned and resumed decisions. Only run.finish is safely dropped.
                builder.Append(entry.Kind, entry.Payload);
            }
        }

        RunRecord script = builder.Build();
        Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(script, Sts2PgnValidationMode.Script);
        if (!validation.IsValid)
        {
            throw new Sts2PgnValidationException(validation);
        }

        return script;
    }

    /// <summary>Keeps portable state and generation inputs, dropping the raw save snapshot.</summary>
    private static JsonElement StripRunStart(JsonElement payload)
    {
        JsonObject result = new();
        if (payload.TryGetProperty("actor", out JsonElement actor))
        {
            result["actor"] = JsonNode.Parse(actor.GetRawText());
        }

        JsonObject data = new();
        if (payload.TryGetProperty("data", out JsonElement dataElement)
            && dataElement.ValueKind == JsonValueKind.Object)
        {
            foreach (string field in new[] { "state", "profile" })
            {
                if (dataElement.TryGetProperty(field, out JsonElement value))
                {
                    data[field] = JsonNode.Parse(value.GetRawText());
                }
            }
        }

        result["data"] = data;
        return JsonSerializer.SerializeToElement(result);
    }
}
