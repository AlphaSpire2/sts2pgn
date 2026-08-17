using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2PGN.Model;

/// <summary>
/// Authoritative portable state recorded after a settled trace entry.
/// </summary>
public sealed class StateObservationPayload
{
    [JsonPropertyName("after")]
    public long After { get; }

    [JsonPropertyName("context")]
    public string Context { get; }

    [JsonPropertyName("digest")]
    public string Digest { get; }

    [JsonPropertyName("projection_version")]
    public int ProjectionVersion { get; }

    [JsonPropertyName("state")]
    public JsonElement State { get; }

    public StateObservationPayload(
        long after,
        string context,
        string digest,
        int projectionVersion,
        JsonElement state)
    {
        if (after <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(after), "An observation must follow a positive sequence number.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        if (projectionVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectionVersion));
        }

        if (state.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Portable state must be a JSON object.", nameof(state));
        }

        After = after;
        Context = context;
        Digest = digest;
        ProjectionVersion = projectionVersion;
        State = state.Clone();
    }
}
