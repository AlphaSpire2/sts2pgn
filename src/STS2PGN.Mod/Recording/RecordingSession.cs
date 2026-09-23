using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Serialization;

namespace STS2PGN.Mod.Recording;

internal sealed class RecordingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly RunState _state;
    private readonly Dictionary<ulong, string> _playerSlots;
    private readonly IDisposable _streamResources;
    private readonly Sts2PgnStreamWriter _writer;
    private bool _disposed;

    /// <summary>The persisted trace path, or null for a playback-only transient session.</summary>
    public string? Path { get; private set; }

    /// <summary>
    /// The anchor for the first observation: the run.start sequence for a new trace,
    /// or the final already-recorded sequence when an incomplete trace was continued.
    /// </summary>
    public long RunStartSequence { get; private set; }

    /// <summary>Whether this session appends to a trace recorded by an earlier session.</summary>
    public bool IsContinuation { get; }

    private RecordingSession(
        RunState state,
        Dictionary<ulong, string> playerSlots,
        string? path,
        IDisposable streamResources,
        Sts2PgnStreamWriter writer,
        bool isContinuation)
    {
        _state = state;
        _playerSlots = playerSlots;
        Path = path;
        _streamResources = streamResources;
        _writer = writer;
        IsContinuation = isContinuation;
    }

    public static RecordingSession Start(RunState state, RecorderConfig config, bool persistToDisk = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Guid runId = Guid.NewGuid();
        Dictionary<ulong, string> playerSlots = BuildPlayerSlots(state);

        SerializableRun save = RunManager.Instance.ToSave(null);
        RunMetadata metadata = BuildMetadata(state, playerSlots, runId, startedAt, save.StartTime);
        if (!persistToDisk)
        {
            Sts2PgnStreamWriter transientWriter = new(TextWriter.Null, metadata);
            RecordingSession transientSession = new(
                state,
                playerSlots,
                path: null,
                TextWriter.Null,
                transientWriter,
                isContinuation: false);
            transientSession.RecordRunStart(save);
            return transientSession;
        }

        string fileName = $"{startedAt:yyyyMMdd-HHmmss}-{runId:N}{FormatConstants.FileExtension}";
        string outputDirectory = config.ResolveOutputDirectory();
        Directory.CreateDirectory(outputDirectory);
        string path = System.IO.Path.Combine(outputDirectory, fileName);
        FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            StreamWriter text = new(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 16 * 1024, leaveOpen: true)
            {
                NewLine = "\n",
            };
            Sts2PgnStreamWriter writer = new(text, metadata);
            RecordingSession session = new(state, playerSlots, path, file, writer, isContinuation: false);
            session.RecordRunStart(save);
            return session;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Continues the incomplete trace recorded for the same run when one can be found
    /// and safely appended to; returns null so the caller can fall back to a new trace.
    /// </summary>
    public static RecordingSession? TryResume(RunState state, RecorderConfig config, SerializableRun save)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(save);
        string outputDirectory = config.ResolveOutputDirectory();
        if (!Directory.Exists(outputDirectory))
        {
            return null;
        }

        Dictionary<ulong, string> playerSlots = BuildPlayerSlots(state);
        foreach (string path in Directory
            .EnumerateFiles(outputDirectory, $"*{FormatConstants.FileExtension}")
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (!HeaderMatchesResumedRun(path, state, playerSlots, save))
            {
                continue;
            }

            Sts2PgnTraceContinuation continuation;
            try
            {
                continuation = Sts2PgnTraceContinuation.Open(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or Sts2PgnParseException)
            {
                Godot.GD.Print($"[STS2PGN] Cannot continue {path}: {exception.Message}");
                continue;
            }

            try
            {
                RecordingSession session = new(
                    state,
                    playerSlots,
                    path,
                    continuation,
                    continuation.Writer,
                    isContinuation: true)
                {
                    RunStartSequence = continuation.Prefix.Entries[^1].Sequence,
                };
                if (continuation.RemovedTruncatedFinalLine)
                {
                    Godot.GD.Print($"[STS2PGN] Removed a crash-truncated final line before continuing {path}");
                }

                return session;
            }
            catch
            {
                continuation.Dispose();
                throw;
            }
        }

        return null;
    }

    public long? Record(string kind, object? data, ulong? actorId = null)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            actor = ActorSlot(actorId),
            location = BuildLocation(),
            data,
            extensions = new { },
        });

        lock (_gate)
        {
            if (!_disposed && !_writer.IsCompleted)
            {
                return _writer.Append(kind, payload).Sequence;
            }

            return null;
        }
    }

    public JsonElement CaptureState(JsonElement recorderState) =>
        LiveStateProjector.Capture(_state, _playerSlots, recorderState);

    public long? Observe(long after, string context, JsonElement state)
    {
        StateObservationPayload observation = StateObservationFactory.Create(after, context, state);
        JsonElement payload = JsonSerializer.SerializeToElement(observation);
        lock (_gate)
        {
            if (!_disposed && !_writer.IsCompleted)
            {
                return _writer.Append("state.observe", payload).Sequence;
            }

            return null;
        }
    }

    public void Complete(RunResult result, JsonElement terminalState)
    {
        lock (_gate)
        {
            if (_disposed || _writer.IsCompleted)
            {
                return;
            }

            long? finishSequence = Record("run.finish", new { result = result.ToToken() });
            if (finishSequence.HasValue)
            {
                Observe(finishSequence.Value, "run.end", terminalState);
            }

            _writer.Complete(result);
            _writer.Dispose();
            _streamResources.Dispose();
            if (Path is not null)
            {
                Path = Sts2PgnFileFinalizer.FinalizeRecording(Path, result);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
            _streamResources.Dispose();
        }
    }

    private void RecordRunStart(SerializableRun save)
    {
        string sourceJson = JsonSerializationUtility.ToJson(save);
        JsonNode? sourceData = JsonNode.Parse(sourceJson);
        JsonElement portableState = InitialStateProjector.Capture(save, _playerSlots);
        long? runStartSequence = Record("run.start", new
        {
            source_snapshot = new
            {
                schema_version = save.SchemaVersion,
                encoding = "sts2-save-json",
                data = sourceData,
            },
            state = portableState,
        });

        if (runStartSequence.HasValue)
        {
            RunStartSequence = runStartSequence.Value;
            StateObservationPayload observation = StateObservationFactory.Create(
                runStartSequence.Value,
                "run.start",
                portableState);
            JsonElement payload = JsonSerializer.SerializeToElement(observation);
            lock (_gate)
            {
                if (!_disposed && !_writer.IsCompleted)
                {
                    _writer.Append("state.observe", payload);
                }
            }
        }
    }

    private static bool HeaderMatchesResumedRun(
        string path,
        RunState state,
        IReadOnlyDictionary<ulong, string> playerSlots,
        SerializableRun save)
    {
        try
        {
            using StreamReader reader = new(path);
            RunMetadata tags = Sts2PgnParser.ParseMetadata(reader);
            ReleaseInfo? release = ReleaseInfoManager.Instance.ReleaseInfo;
            return tags.GetValue("Format") == FormatConstants.Name
                && tags.GetValue("FormatVersion") is "1" or "2"
                && tags.GetValue("Result") == "*"
                && tags.GetValue("GameStartTime") == save.StartTime.ToString(CultureInfo.InvariantCulture)
                && tags.GetValue("Seed") == state.Rng.StringSeed
                && tags.GetValue("Mode") == state.GameMode.ToString().ToUpperInvariant()
                && tags.GetValue("Ascension") == state.AscensionLevel.ToString(CultureInfo.InvariantCulture)
                && tags.GetValue("Players") == BuildPlayersTag(state, playerSlots)
                && tags.GetValue("GameVersion") == (release?.Version ?? "UNKNOWN")
                && tags.GetValue("GameCommit") == (release?.Commit ?? "UNKNOWN")
                && tags.GetValue("ModelIdHash") == ModelIdSerializationCache.Hash.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Sts2PgnParseException)
        {
            return false;
        }
    }

    private object BuildLocation()
    {
        var coord = _state.CurrentMapCoord;
        var room = _state.CurrentRoom;
        return new
        {
            act = _state.CurrentActIndex,
            floor = _state.ActFloor,
            map = coord.HasValue ? new { col = coord.Value.col, row = coord.Value.row } : null,
            room = room is null ? null : new
            {
                id = room.Id,
                type = room.RoomType.ToString().ToUpperInvariant(),
                model = room.ModelId?.ToString(),
                stack_depth = _state.CurrentRoomCount,
            },
        };
    }

    private string? ActorSlot(ulong? actorId) =>
        actorId.HasValue && _playerSlots.TryGetValue(actorId.Value, out string? slot) ? slot : null;

    private static Dictionary<ulong, string> BuildPlayerSlots(RunState state) => state.Players
        .Select((player, index) => (player.NetId, Slot: $"p{index}"))
        .ToDictionary(pair => pair.NetId, pair => pair.Slot);

    private static string BuildPlayersTag(RunState state, IReadOnlyDictionary<ulong, string> playerSlots) =>
        JsonSerializer.Serialize(state.Players.Select(player => new
        {
            slot = playerSlots[player.NetId],
            character = player.Character.Id.Entry,
        }));

    private static RunMetadata BuildMetadata(
        RunState state,
        IReadOnlyDictionary<ulong, string> playerSlots,
        Guid runId,
        DateTimeOffset startedAt,
        long gameStartTime)
    {
        string players = BuildPlayersTag(state, playerSlots);
        string mods = JsonSerializer.Serialize(ModManager.Mods
            .Where(mod => mod.state == ModLoadState.Loaded && mod.manifest is not null)
            .Select(mod => new
            {
                id = mod.manifest!.id,
                version = mod.manifest.version,
                affects_gameplay = mod.manifest.affectsGameplay,
            }));
        ReleaseInfo? release = ReleaseInfoManager.Instance.ReleaseInfo;

        return new RunMetadata(
        [
            new("Format", FormatConstants.Name),
            new("FormatVersion", FormatConstants.CurrentVersion.ToString(CultureInfo.InvariantCulture)),
            new("RecorderVersion", STS2PgnMod.Version),
            new("RunId", runId.ToString("D")),
            new("GameVersion", release?.Version ?? "UNKNOWN"),
            new("GameCommit", release?.Commit ?? "UNKNOWN"),
            new("ModelIdHash", ModelIdSerializationCache.Hash.ToString(CultureInfo.InvariantCulture)),
            new("Seed", state.Rng.StringSeed),
            new("Mode", state.GameMode.ToString().ToUpperInvariant()),
            new("Ascension", state.AscensionLevel.ToString(CultureInfo.InvariantCulture)),
            new("Players", players),
            new("StartedAt", startedAt.ToString("O", CultureInfo.InvariantCulture)),
            new("GameStartTime", gameStartTime.ToString(CultureInfo.InvariantCulture)),
            new("Mods", mods),
            new("Result", "*"),
        ]);
    }
}
