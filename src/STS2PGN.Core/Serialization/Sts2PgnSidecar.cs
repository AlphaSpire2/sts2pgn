using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;

namespace STS2PGN.Serialization;

/// <summary>A rebuildable search index. Playback never depends on this file.</summary>
public static class Sts2PgnSidecar
{
    public const string Extension = ".meta.json";

    /// <summary>
    /// Indexes a closed plain/gzip trace without reconstructing its observations. Requires
    /// a terminal token, including '*' for scripts. This is not a trace validation pass.
    /// The SHA-256 covers the exact source file bytes (compressed bytes for gzip).
    /// Existing sidecars are preserved unless replacement is explicitly requested.
    /// </summary>
    public static string Write(string path, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string output = path + Extension;
        if (!overwrite && File.Exists(output))
        {
            throw new IOException($"Sidecar '{output}' already exists; use --overwrite to rebuild it.");
        }
        string temporary = $"{output}.writing-{Guid.NewGuid():N}.tmp";
        // Keep a read-only sharing handle throughout extraction, hashing and publication.
        using FileStream source = File.OpenRead(path);
        RunMetadata metadata;
        RunResult? terminal = null;
        using (StreamReader reader = Sts2PgnFile.OpenText(path))
        {
            metadata = Sts2PgnParser.ParseMetadata(reader);
            while (reader.ReadLine() is { } line)
            {
                if (terminal.HasValue)
                {
                    throw new InvalidDataException("No content is allowed after the terminal result.");
                }
                if (RunResultExtensions.TryParseToken(line, out RunResult result))
                {
                    terminal = result;
                }
            }
        }
        if (!terminal.HasValue)
        {
            throw new InvalidDataException("Cannot index an open or truncated trace without a terminal result.");
        }

        string profile = ScriptProfile.GetProfile(metadata);
        if (metadata.GetValue("Format") != FormatConstants.Name
            || !int.TryParse(metadata.GetValue("FormatVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            || !FormatConstants.IsSupportedVersion(version))
        {
            throw new InvalidDataException("Unsupported STS2PGN format or version.");
        }
        string seed = metadata.GetValue("Seed") ?? throw new InvalidDataException("Seed is required for indexing.");
        if (!int.TryParse(metadata.GetValue("Ascension"), NumberStyles.None, CultureInfo.InvariantCulture, out int ascension))
        {
            throw new InvalidDataException("Ascension must be a non-negative integer.");
        }
        using JsonDocument players = JsonDocument.Parse(metadata.GetValue("Players") ?? "null");
        if (players.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Players must be a JSON array.");
        }
        var index = new
        {
            schema_version = 1,
            file = Path.GetFileName(path),
            file_size_bytes = source.Length,
            file_sha256 = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(source)),
            profile,
            format_version = version,
            seed,
            run_id = metadata.GetValue("RunId"),
            mode = metadata.GetValue("Mode"),
            ascension,
            players = players.RootElement,
            game_version = metadata.GetValue("GameVersion"),
            game_commit = metadata.GetValue("GameCommit"),
            model_id_hash = metadata.GetValue("ModelIdHash"),
            producer = metadata.GetValue("Producer"),
            recorder_version = metadata.GetValue("RecorderVersion"),
            started_at = metadata.GetValue("StartedAt"),
            result = terminal.Value.ToToken(),
        };
        try
        {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, index, new JsonSerializerOptions { WriteIndented = true });
                file.Write(Encoding.UTF8.GetBytes("\n"));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, output, overwrite);
            return output;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
