using System.Text.Json;

namespace STS2PGN.Mod.Recording;

/// <summary>Chooses continuation or a new session before any recording file is created.</summary>
internal static class RecordingSessionStartup
{
    public const bool DefaultRecordPartialRuns = false;

    public static bool ReadRecordPartialRuns(JsonElement configuration) =>
        configuration.TryGetProperty("record_partial_runs", out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : DefaultRecordPartialRuns;

    /// <summary>
    /// A null continuation factory means a new run. Transient playback sessions
    /// never append to disk and must remain available even when partial recording is off.
    /// </summary>
    public static TSession? Open<TSession>(
        bool persist,
        Func<TSession?>? continueRecording,
        Func<TSession> startRecording,
        bool recordPartialRuns = DefaultRecordPartialRuns)
        where TSession : class
    {
        if (persist && continueRecording is not null)
        {
            TSession? continuation = continueRecording();
            if (continuation is not null)
            {
                return continuation;
            }

            if (!recordPartialRuns)
            {
                return null;
            }
        }

        return startRecording();
    }
}
