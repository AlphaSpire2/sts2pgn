using System.Text.Json;
using System.Text.Json.Serialization;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback;

/// <summary>Outcome of driving one decision script.</summary>
internal enum PlaybackStatus
{
    /// <summary>Every scripted decision was verified, submitted, and settled.</summary>
    Completed,

    /// <summary>A precondition (validation, build identity, character, unlocks, acts, live save) failed; no run was started.</summary>
    RefusedAtStart,

    /// <summary>The game offered something other than what the script expected at a specific record.</summary>
    StoppedAtRecord,

    /// <summary>The driver itself failed: timeout, exception, or an unmapped record kind.</summary>
    GameError,
}

/// <summary>Why a session stopped early. Reported verbatim in the JSON report.</summary>
internal static class PlaybackReasons
{
    public const string FingerprintMismatch = "fingerprint_mismatch";
    public const string RecordedMismatch = "recorded_mismatch";
    public const string UnsupportedRecord = "unsupported_record";
    public const string Timeout = "timeout";
    public const string Exception = "exception";
    public const string Precondition = "precondition";
    public const string Validation = "validation";
}

/// <summary>The unlock state of the live profile, in the same shape the script carries.</summary>
internal sealed record PlaybackUnlocks(
    [property: JsonPropertyName("epochs")] IReadOnlyList<string> Epochs,
    [property: JsonPropertyName("encounters_seen")] IReadOnlyList<string> EncountersSeen,
    [property: JsonPropertyName("number_of_runs")] int NumberOfRuns)
{
    public static PlaybackUnlocks From(ScriptUnlocks unlocks) =>
        new(unlocks.Epochs, unlocks.EncountersSeen, unlocks.NumberOfRuns);
}

/// <summary>
/// Per-script report. Written as <c>&lt;script-name&gt;.playback.json</c> next to the recordings so the
/// harness can pair the recording with the script that drove it.
/// </summary>
internal sealed class PlaybackReport
{
    [JsonPropertyName("script")]
    public required string Script { get; init; }

    [JsonPropertyName("status")]
    public PlaybackStatus Status { get; set; } = PlaybackStatus.GameError;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Script record sequence where driving stopped, when applicable.</summary>
    [JsonPropertyName("stopped_at_sequence")]
    public long? StoppedAtSequence { get; set; }

    /// <summary>Zero-based decision ordinal where driving stopped, when applicable.</summary>
    [JsonPropertyName("stopped_at_decision")]
    public int? StoppedAtDecision { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("expected")]
    public JsonElement? Expected { get; set; }

    [JsonPropertyName("offered")]
    public JsonElement? Offered { get; set; }

    /// <summary>Every precondition mismatch found before starting (refused_at_start only).</summary>
    [JsonPropertyName("precondition_failures")]
    public List<string> PreconditionFailures { get; } = [];

    /// <summary>The input was a recording, converted to a script at load time (same as `sts2pgn to-script`).</summary>
    [JsonPropertyName("converted_from_recording")]
    public bool ConvertedFromRecording { get; set; }

    [JsonPropertyName("decisions_total")]
    public int DecisionsTotal { get; set; }

    [JsonPropertyName("decisions_completed")]
    public int DecisionsCompleted { get; set; }

    /// <summary>Path of the authoritative recording the recorder wrote for this session.</summary>
    [JsonPropertyName("recording_path")]
    public string? RecordingPath { get; set; }

    /// <summary>True when successful playback detached without ending the live run or recorder.</summary>
    [JsonPropertyName("handed_off")]
    public bool HandedOff { get; set; }

    /// <summary>Live profile unlocks after the run, so the next script can be generated from them.</summary>
    [JsonPropertyName("unlocks_after_run")]
    public PlaybackUnlocks? UnlocksAfterRun { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    public void Stop(PlaybackStatus status, string reason, string message, ScriptDecision? decision = null)
    {
        Status = status;
        Reason = reason;
        Message = message;
        if (decision is not null)
        {
            StoppedAtSequence = decision.Sequence;
            StoppedAtDecision = decision.Ordinal;
            Kind = decision.Kind;
        }
    }

    public void StopWithMismatch(ScriptDecision decision, string message, JsonElement? expected, JsonElement? offered)
    {
        Stop(PlaybackStatus.StoppedAtRecord, PlaybackReasons.FingerprintMismatch, message, decision);
        Expected = expected;
        Offered = offered;
    }
}

/// <summary>Batch summary written as <c>playback-summary.json</c>.</summary>
internal sealed class PlaybackSummary
{
    public const int ExitCompleted = 0;
    public const int ExitStopped = 3;
    public const int ExitRefused = 4;
    public const int ExitGameError = 5;

    [JsonPropertyName("scripts")]
    public List<PlaybackSummaryRow> Scripts { get; } = [];

    [JsonPropertyName("exit_code")]
    public int ExitCode => Scripts.Count == 0 ? ExitGameError : Scripts.Max(row => ExitCodeFor(row.Status));

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    public static int ExitCodeFor(PlaybackStatus status) => status switch
    {
        PlaybackStatus.Completed => ExitCompleted,
        PlaybackStatus.StoppedAtRecord => ExitStopped,
        PlaybackStatus.RefusedAtStart => ExitRefused,
        _ => ExitGameError,
    };
}

internal sealed record PlaybackSummaryRow(
    [property: JsonPropertyName("script")] string Script,
    [property: JsonPropertyName("status")] PlaybackStatus Status,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("stopped_at_sequence")] long? StoppedAtSequence,
    [property: JsonPropertyName("recording_path")] string? RecordingPath,
    [property: JsonPropertyName("report_path")] string ReportPath,
    [property: JsonPropertyName("handed_off")] bool HandedOff);

internal static class PlaybackReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string ReportPathFor(string outputDirectory, string scriptPath) =>
        Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(scriptPath) + ".playback.json");

    public static string SummaryPathFor(string outputDirectory) =>
        Path.Combine(outputDirectory, "playback-summary.json");

    public static string Write(string outputDirectory, PlaybackReport report)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = ReportPathFor(outputDirectory, report.Script);
        WriteAtomically(path, JsonSerializer.Serialize(report, Options));
        return path;
    }

    public static string Write(string outputDirectory, PlaybackSummary summary)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = SummaryPathFor(outputDirectory);
        WriteAtomically(path, JsonSerializer.Serialize(summary, Options));
        return path;
    }

    private static void WriteAtomically(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
