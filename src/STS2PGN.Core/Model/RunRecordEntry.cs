using System.Text.Json;

namespace STS2PGN.Model;

/// <summary>
/// One action or observation in deterministic trace order.
/// </summary>
public sealed class RunRecordEntry
{
    public long Sequence { get; }
    public string Kind { get; }
    public JsonElement Payload { get; }

    public RunRecordEntry(long sequence, string kind, JsonElement payload)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence numbers must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A record payload must be a JSON object.", nameof(payload));
        }

        Sequence = sequence;
        Kind = kind;
        Payload = payload.Clone();
    }
}
