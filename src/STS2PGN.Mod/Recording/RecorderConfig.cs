using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Saves;
using STS2PGN.Mod.Playback;

namespace STS2PGN.Mod.Recording;

internal sealed record RecorderConfig(
    string OutputDirectory,
    int PlaybackTimeoutSeconds = RecorderConfig.DefaultPlaybackTimeoutSeconds,
    bool RecordPlaybackRuns = RecorderConfig.DefaultRecordPlaybackRuns,
    PlaybackSpeedLevers FastPlayback = RecorderConfig.DefaultFastPlayback,
    bool RecordPartialRuns = RecordingSessionStartup.DefaultRecordPartialRuns,
    Key PlaybackStepKey = Key.F8)
{
    public const string FileName = "STS2_PGN.config.json";

    /// <summary>
    /// <c>--sts2pgn-out=&lt;dir&gt;</c> overrides <c>output_directory</c> for one launch. Concurrent
    /// playback processes share the installed <see cref="FileName"/>, so this is how each one is
    /// given its own report and trace directory.
    /// </summary>
    public const string OutputDirectoryArg = "sts2pgn-out";
    public const string DocumentsPrefix = "documents://";
    public const string ProfileFallbackOutputDirectory = "sts2pgn";
    public const string DefaultOutputDirectory = DocumentsPrefix + ProfileFallbackOutputDirectory;
    public const int DefaultPlaybackTimeoutSeconds = 30;
    public const bool DefaultRecordPlaybackRuns = true;
    /// <summary>
    /// Only the lever measured to leave the trace byte-identical. The speed comes from launching
    /// with <c>--headless --fixed-fps 60</c>, which this lever is what enables.
    /// </summary>
    public const PlaybackSpeedLevers DefaultFastPlayback = PlaybackSpeedLevers.UncappedFrameRate;

    public static RecorderConfig Load()
    {
        RecorderConfig config = LoadFile();
        if (!CommandLineHelper.HasArg(OutputDirectoryArg))
        {
            return config;
        }

        string? overridden = CommandLineHelper.GetValue(OutputDirectoryArg);
        if (string.IsNullOrWhiteSpace(overridden))
        {
            GD.PrintErr($"[STS2PGN] --{OutputDirectoryArg} needs a directory; keeping '{config.OutputDirectory}'");
            return config;
        }

        GD.Print($"[STS2PGN] Output directory overridden on the command line: {overridden.Trim()}");
        return config with { OutputDirectory = overridden.Trim() };
    }

    private static RecorderConfig LoadFile()
    {
        string? modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(modDirectory))
        {
            GD.PrintErr($"[STS2PGN] Could not locate {FileName}; using the default output directory");
            return Default();
        }

        string configPath = Path.Combine(modDirectory, FileName);
        if (!File.Exists(configPath))
        {
            GD.Print($"[STS2PGN] No {FileName} found at {configPath}; using the default output directory");
            return Default();
        }

        try
        {
            using FileStream stream = File.OpenRead(configPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("output_directory", out JsonElement outputDirectory)
                || outputDirectory.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(outputDirectory.GetString()))
            {
                GD.PrintErr($"[STS2PGN] {configPath} needs a non-empty string 'output_directory'; using the default");
                return Default();
            }

            int timeout = DefaultPlaybackTimeoutSeconds;
            if (document.RootElement.TryGetProperty("playback_timeout_seconds", out JsonElement timeoutElement)
                && timeoutElement.ValueKind == JsonValueKind.Number
                && timeoutElement.TryGetInt32(out int configuredTimeout)
                && configuredTimeout > 0)
            {
                timeout = configuredTimeout;
            }

            bool recordPlaybackRuns = DefaultRecordPlaybackRuns;
            if (document.RootElement.TryGetProperty("record_playback_runs", out JsonElement recordPlaybackRunsElement)
                && recordPlaybackRunsElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                recordPlaybackRuns = recordPlaybackRunsElement.GetBoolean();
            }

            PlaybackSpeedLevers fastPlayback = DefaultFastPlayback;
            if (document.RootElement.TryGetProperty("fast_playback", out JsonElement fastPlaybackElement))
            {
                fastPlayback = ReadFastPlayback(fastPlaybackElement, configPath);
            }

            bool recordPartialRuns = RecordingSessionStartup.ReadRecordPartialRuns(document.RootElement);
            Key stepKey = Key.F8;
            if (document.RootElement.TryGetProperty("playback_step_key", out JsonElement stepKeyElement))
            {
                if (stepKeyElement.ValueKind != JsonValueKind.String
                    || !Enum.TryParse(stepKeyElement.GetString(), ignoreCase: true, out stepKey)
                    || !Enum.IsDefined(stepKey))
                {
                    GD.PrintErr($"[STS2PGN] {configPath}: invalid playback_step_key; using F8");
                    stepKey = Key.F8;
                }
            }
            return new RecorderConfig(outputDirectory.GetString()!.Trim(), timeout, recordPlaybackRuns, fastPlayback,
                recordPartialRuns, stepKey);
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[STS2PGN] Could not read {configPath}: {exception.Message}; using the default output directory");
            return Default();
        }
    }

    /// <summary>
    /// <c>true</c>/<c>false</c> for all-or-nothing, or an array naming individual levers
    /// (<c>non_interactive</c>, <c>instant_presentation</c>, <c>uncapped_frame_rate</c>,
    /// <c>frame_polling</c>) to run a batch with only some of them.
    /// </summary>
    private static PlaybackSpeedLevers ReadFastPlayback(JsonElement element, string configPath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return DefaultFastPlayback;
            case JsonValueKind.False:
                return PlaybackSpeedLevers.None;
            case JsonValueKind.Array:
                PlaybackSpeedLevers levers = PlaybackSpeedLevers.None;
                foreach (JsonElement entry in element.EnumerateArray())
                {
                    string name = entry.ValueKind == JsonValueKind.String ? entry.GetString()!.Trim() : string.Empty;
                    switch (name.ToLowerInvariant())
                    {
                        case "non_interactive": levers |= PlaybackSpeedLevers.NonInteractive; break;
                        case "instant_presentation": levers |= PlaybackSpeedLevers.InstantPresentation; break;
                        case "uncapped_frame_rate": levers |= PlaybackSpeedLevers.UncappedFrameRate; break;
                        case "frame_polling": levers |= PlaybackSpeedLevers.FramePolling; break;
                        default:
                            GD.PrintErr($"[STS2PGN] {configPath}: unknown fast_playback lever '{name}'; ignoring it");
                            break;
                    }
                }

                return levers;
            default:
                GD.PrintErr($"[STS2PGN] {configPath}: 'fast_playback' must be a boolean or an array of lever names; using the default");
                return DefaultFastPlayback;
        }
    }

    public string ResolveOutputDirectory()
    {
        if (OutputDirectory.StartsWith(DocumentsPrefix, StringComparison.Ordinal))
        {
            return ResolveDocumentsOutputDirectory();
        }

        if (OutputDirectory.StartsWith("user://", StringComparison.Ordinal)
            || OutputDirectory.StartsWith("res://", StringComparison.Ordinal))
        {
            return ProjectSettings.GlobalizePath(OutputDirectory);
        }

        if (Path.IsPathFullyQualified(OutputDirectory))
        {
            return Path.GetFullPath(OutputDirectory);
        }

        string profilePath = SaveManager.Instance.GetProfileScopedPath(OutputDirectory);
        return ProjectSettings.GlobalizePath(profilePath);
    }

    private string ResolveDocumentsOutputDirectory()
    {
        string documentsDirectory = Godot.OS.GetSystemDir(Godot.OS.SystemDir.Documents);
        if (string.IsNullOrWhiteSpace(documentsDirectory))
        {
            GD.PrintErr("[STS2PGN] Could not locate the system Documents directory; using the profile-scoped output directory");
            return ResolveProfileFallbackOutputDirectory();
        }

        string relativePath = OutputDirectory[DocumentsPrefix.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string documentsRoot = Path.GetFullPath(documentsDirectory);
        string resolvedPath = Path.GetFullPath(Path.Combine(documentsRoot, relativePath));
        string relativeToDocuments = Path.GetRelativePath(documentsRoot, resolvedPath);
        if (Path.IsPathFullyQualified(relativeToDocuments)
            || relativeToDocuments.Equals("..", StringComparison.Ordinal)
            || relativeToDocuments.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToDocuments.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            GD.PrintErr($"[STS2PGN] '{OutputDirectory}' escapes the system Documents directory; using the profile-scoped output directory");
            return ResolveProfileFallbackOutputDirectory();
        }

        return resolvedPath;
    }

    private static string ResolveProfileFallbackOutputDirectory() =>
        ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath(ProfileFallbackOutputDirectory));

    private static RecorderConfig Default() => new(DefaultOutputDirectory);
}
