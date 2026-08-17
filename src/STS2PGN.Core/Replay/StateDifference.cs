using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2PGN.Replay;

public sealed class StateDifference
{
    [JsonPropertyName("path")]
    public string Path { get; }

    [JsonPropertyName("expected")]
    public JsonElement? Expected { get; }

    [JsonPropertyName("actual")]
    public JsonElement? Actual { get; }

    [JsonPropertyName("expected_present")]
    public bool ExpectedPresent { get; }

    [JsonPropertyName("actual_present")]
    public bool ActualPresent { get; }

    public StateDifference(string path, JsonElement? expected, JsonElement? actual)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
        ExpectedPresent = expected.HasValue;
        ActualPresent = actual.HasValue;
        Expected = expected?.Clone();
        Actual = actual?.Clone();
    }
}
