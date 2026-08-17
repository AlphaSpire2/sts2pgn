using System.Text.Json;
using STS2PGN.Model;

namespace STS2PGN.Replay;

public sealed class ReplayStep
{
    public long Sequence { get; }
    public string Kind { get; }
    public ReplayRecordRole Role { get; }
    public JsonElement Payload { get; }

    internal ReplayStep(RunRecordEntry entry)
    {
        Sequence = entry.Sequence;
        Kind = entry.Kind;
        Role = ReplayTimeline.Classify(entry.Kind);
        Payload = entry.Payload.Clone();
    }
}
