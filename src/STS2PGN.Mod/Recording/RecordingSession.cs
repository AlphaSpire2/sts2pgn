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
using STS2PGN.Serialization;

namespace STS2PGN.Mod.Recording;

internal sealed class RecordingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly RunState _state;
    private readonly Dictionary<ulong, string> _playerSlots;
    private readonly FileStream _file;
    private readonly Sts2PgnStreamWriter _writer;
    private bool _disposed;

    public string Path { get; }

    private RecordingSession(
        RunState state,
        Dictionary<ulong, string> playerSlots,
        string path,
        FileStream file,
        Sts2PgnStreamWriter writer)
    {
        _state = state;
        _playerSlots = playerSlots;
        Path = path;
        _file = file;
        _writer = writer;
    }

    public static RecordingSession Start(RunState state, RecorderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Guid runId = Guid.NewGuid();
        Dictionary<ulong, string> playerSlots = state.Players
            .Select((player, index) => (player.NetId, Slot: $"p{index}"))
            .ToDictionary(pair => pair.NetId, pair => pair.Slot);

        RunMetadata metadata = BuildMetadata(state, playerSlots, runId, startedAt);
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
            RecordingSession session = new(state, playerSlots, path, file, writer);
            session.RecordRunStart();
            return session;
        }
        catch
        {
            file.Dispose();
            throw;
        }
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

    public void Complete(RunResult result)
    {
        lock (_gate)
        {
            if (_disposed || _writer.IsCompleted)
            {
                return;
            }

            Record("run.finish", new { result = result.ToToken() });
            _writer.Complete(result);
            _writer.Dispose();
            _file.Dispose();
            Sts2PgnFileFinalizer.FinalizeResultHeader(Path, result);
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
            _file.Dispose();
        }
    }

    private void RecordRunStart()
    {
        SerializableRun save = RunManager.Instance.ToSave(null);
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

    private static RunMetadata BuildMetadata(
        RunState state,
        IReadOnlyDictionary<ulong, string> playerSlots,
        Guid runId,
        DateTimeOffset startedAt)
    {
        string players = JsonSerializer.Serialize(state.Players.Select(player => new
        {
            slot = playerSlots[player.NetId],
            character = player.Character.Id.Entry,
        }));
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
            new("FormatVersion", FormatConstants.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("RecorderVersion", STS2PgnMod.Version),
            new("RunId", runId.ToString("D")),
            new("GameVersion", release?.Version ?? "UNKNOWN"),
            new("GameCommit", release?.Commit ?? "UNKNOWN"),
            new("ModelIdHash", ModelIdSerializationCache.Hash.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Seed", state.Rng.StringSeed),
            new("Mode", state.GameMode.ToString().ToUpperInvariant()),
            new("Ascension", state.AscensionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Players", players),
            new("StartedAt", startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            new("Mods", mods),
            new("Result", "*"),
        ]);
    }
}
