using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace STS2PGN.Mod.Recording;

internal sealed record RecorderConfig(string OutputDirectory, int PlaybackTimeoutSeconds = RecorderConfig.DefaultPlaybackTimeoutSeconds)
{
    public const string FileName = "config.json";
    public const string DefaultOutputDirectory = "sts2pgn";
    public const int DefaultPlaybackTimeoutSeconds = 30;

    public static RecorderConfig Load()
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

            return new RecorderConfig(outputDirectory.GetString()!.Trim(), timeout);
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[STS2PGN] Could not read {configPath}: {exception.Message}; using the default output directory");
            return Default();
        }
    }

    public string ResolveOutputDirectory()
    {
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

    private static RecorderConfig Default() => new(DefaultOutputDirectory);
}
