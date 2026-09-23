using System.Text.Json;
using System.Text.Json.Nodes;
using STS2PGN.Model;

namespace STS2PGN.Serialization;

/// <summary>
/// Version-two wire codec. Baselines always come from recorded observations, never
/// simulator state. Public models continue to contain complete state.observe records.
/// </summary>
internal sealed class StateDeltaCodec(int maxDepth = 256)
{
    internal const int MaxConsecutiveDeltas = 64;
    private readonly JsonSerializerOptions _jsonOptions = new() { MaxDepth = maxDepth };
    private JsonElement? _state;
    private long _sequence;
    private int _deltaCount;

    internal RunRecordEntry Encode(RunRecordEntry entry)
    {
        ResetAtBoundary(entry);
        if (entry.Kind == "state.delta")
        {
            throw new InvalidDataException("Writers require decoded state.observe records, not wire deltas.");
        }
        if (entry.Kind != "state.observe")
        {
            return entry;
        }

        JsonElement state = VerifiedState(entry.Payload);
        RunRecordEntry encoded = entry;
        if (_state is { } previous && _sequence < entry.Sequence
            && _deltaCount < MaxConsecutiveDeltas && !IsCheckpoint(entry.Payload))
        {
            JsonArray patch = new();
            Diff(previous, state, [], patch);
            JsonObject observation = Node(entry.Payload)!.AsObject();
            observation.Remove("state");
            JsonObject payload = new()
            {
                ["base"] = _sequence,
                ["observation"] = observation,
                ["patch"] = patch,
            };
            RunRecordEntry candidate = new(entry.Sequence, "state.delta", Element(payload));
            if (CanonicalJson.SerializeToUtf8Bytes(candidate.Payload).Length + candidate.Kind.Length
                < CanonicalJson.SerializeToUtf8Bytes(entry.Payload).Length + entry.Kind.Length)
            {
                encoded = candidate;
            }
        }

        Remember(entry.Sequence, state, encoded.Kind == "state.delta");
        return encoded;
    }

    internal RunRecordEntry Decode(RunRecordEntry entry)
    {
        ResetAtBoundary(entry);
        if (entry.Kind == "state.observe")
        {
            Remember(entry.Sequence, VerifiedState(entry.Payload), delta: false);
            return entry;
        }
        if (entry.Kind != "state.delta")
        {
            return entry;
        }

        JsonElement payload = entry.Payload;
        // Reject duplicate properties and unsupported transport fields instead of losing them.
        _ = CanonicalJson.Serialize(payload);
        if (payload.EnumerateObject().Count() != 3
            || !payload.TryGetProperty("base", out JsonElement baseValue)
            || !baseValue.TryGetInt64(out long baseSequence)
            || _state is not { } previous || baseSequence != _sequence || baseSequence >= entry.Sequence
            || !payload.TryGetProperty("observation", out JsonElement observation)
            || observation.ValueKind != JsonValueKind.Object || observation.TryGetProperty("state", out _)
            || IsCheckpoint(observation)
            || !payload.TryGetProperty("patch", out JsonElement patch) || patch.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Invalid state.delta envelope or missing/mismatched observation base.");
        }

        JsonObject state = Node(previous)!.AsObject();
        foreach (JsonElement operation in patch.EnumerateArray())
        {
            Apply(state, operation);
        }
        JsonObject expanded = Node(observation)!.AsObject();
        expanded["state"] = state;
        JsonElement expandedPayload = Element(expanded);
        JsonElement verified = VerifiedState(expandedPayload);
        Remember(entry.Sequence, verified, delta: true);
        return new RunRecordEntry(entry.Sequence, "state.observe", expandedPayload);
    }

    private void ResetAtBoundary(RunRecordEntry entry)
    {
        if (entry.Kind is "run.start" or "run.resume" or "room.enter" or "act.enter")
        {
            _state = null;
            _deltaCount = 0;
        }
    }

    private static bool IsCheckpoint(JsonElement observation) =>
        observation.TryGetProperty("context", out JsonElement context)
        && context.ValueKind == JsonValueKind.String
        && context.GetString() is "run.start" or "run.resume" or "room.enter" or "act.enter";

    private void Remember(long sequence, JsonElement state, bool delta)
    {
        _state = state.Clone();
        _sequence = sequence;
        _deltaCount = delta ? _deltaCount + 1 : 0;
    }

    private static JsonElement VerifiedState(JsonElement observation)
    {
        if (!observation.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.Object
            || !observation.TryGetProperty("digest", out JsonElement digest) || digest.ValueKind != JsonValueKind.String
            || digest.GetString() != CanonicalJson.ComputeSha256(state))
        {
            throw new InvalidDataException("Observation state is missing or its canonical digest does not match.");
        }
        return state;
    }

    private void Diff(JsonElement before, JsonElement after, List<string> path, JsonArray patch)
    {
        if (CanonicalJson.Serialize(before) == CanonicalJson.Serialize(after))
        {
            return;
        }
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in before.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!after.TryGetProperty(property.Name, out _))
                {
                    patch.Add(new JsonArray(JsonValue.Create("remove"), PathNode([.. path, property.Name])));
                }
            }
            foreach (JsonProperty property in after.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                List<string> childPath = [.. path, property.Name];
                if (before.TryGetProperty(property.Name, out JsonElement prior))
                {
                    Diff(prior, property.Value, childPath, patch);
                }
                else
                {
                    patch.Add(new JsonArray(JsonValue.Create("set"), PathNode(childPath), Node(property.Value)));
                }
            }
        }
        else
        {
            // Arrays are replaced whole: their order and every card mutation are preserved.
            patch.Add(new JsonArray(JsonValue.Create("set"), PathNode(path), Node(after)));
        }
    }

    private void Apply(JsonObject state, JsonElement operation)
    {
        if (operation.ValueKind != JsonValueKind.Array || operation.GetArrayLength() is < 2 or > 3
            || operation[0].ValueKind != JsonValueKind.String || operation[1].ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Invalid delta patch operation.");
        }
        string? kind = operation[0].GetString();
        if ((kind != "set" || operation.GetArrayLength() != 3)
            && (kind != "remove" || operation.GetArrayLength() != 2))
        {
            throw new InvalidDataException("Expected a set or remove delta operation.");
        }
        JsonElement[] path = operation[1].EnumerateArray().ToArray();
        if (path.Length == 0 || path.Any(part => part.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException("A delta path must contain one or more object property names.");
        }
        JsonObject parent = state;
        foreach (JsonElement part in path[..^1])
        {
            if (parent[part.GetString()!] is not JsonObject child)
            {
                throw new InvalidDataException("A delta path traverses a missing property or non-object value.");
            }
            parent = child;
        }
        string key = path[^1].GetString()!;
        if (kind == "remove")
        {
            if (!parent.Remove(key))
            {
                throw new InvalidDataException("A delta removes a property that does not exist.");
            }
        }
        else
        {
            parent[key] = Node(operation[2]);
        }
    }

    private JsonNode? Node(JsonElement element) =>
        JsonNode.Parse(element.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = maxDepth });

    private JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, _jsonOptions);

    private static JsonArray PathNode(IEnumerable<string> path) =>
        new(path.Select(part => (JsonNode?)JsonValue.Create(part)).ToArray());
}
