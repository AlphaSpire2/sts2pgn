# STS2PGN

STS2PGN records Slay the Spire 2 runs as deterministic, human-readable traces inspired by chess PGN.

The long-term purpose is simulator conformance testing: record a run in the real game, feed the resulting `.sts2pgn` file to an external simulator, replay the same decisions, and identify the first state difference between the simulator and STS2.

> [!IMPORTANT]
> Phase 4 is implemented in recorder 0.6.0. Traces now contain a complete portable run projection at run start and authoritative observations for generated maps, rooms, choices, rewards, combat turns, settled actions, act transitions, and run end. The game-coupled assembly still needs to be built and smoke-tested with matching STS2 assemblies.

## Architecture

```text
Real STS2 + recorder mod -> .sts2pgn trace -> external simulator -> divergence report
```

- `STS2PGN.Core` is a game-independent model, parser, writer, validator, and canonical state-hashing library. Simulator integrations need only this library or the documented format.
- `STS2PGN.Mod` is the STS2 mod that converts authoritative game actions and state into portable records.
- `STS2PGN.Core.Tests` tests the portable contract without requiring the game.

The trace contains initial state, ordered semantic actions, compatibility metadata, and authoritative state observations. Cosmetic input and presentation timing are deliberately excluded.

## Repository layout

```text
src/STS2PGN.Core/          Portable trace model and codec
src/STS2PGN.Mod/           STS2 recorder and replay-validation mod
tests/STS2PGN.Core.Tests/  Game-independent tests
docs/format.md             Version-one format and projection specification
docs/replay-contract.md    Recorder/simulator comparison contract
docs/action-coverage.md    Game integration audit ledger
examples/                  Example traces
decomp/                    Local read-only game decompile; never built
```

See [plan.md](plan.md) for the implementation phases and completion criteria.

## Prerequisites

- .NET 9 SDK
- Slay the Spire 2 for building the mod project
- PowerShell for the convenience build script; direct `dotnet` commands work on every platform

The mod compiles against these files from the platform-specific STS2 data directory:

- `sts2.dll`
- `GodotSharp.dll`
- `0Harmony.dll`

## Build and test

Build the portable core without the game:

```bash
dotnet build src/STS2PGN.Core/STS2PGN.Core.csproj -c Release
dotnet test tests/STS2PGN.Core.Tests/STS2PGN.Core.Tests.csproj -c Release
```

Build the mod by supplying the game installation root:

```bash
dotnet build src/STS2PGN.Mod/STS2PGN.Mod.csproj -c Release \
  -p:STS2GameDir="/path/to/Slay the Spire 2"
```

Alternatively, point directly at the directory containing the three game assemblies:

```bash
dotnet build src/STS2PGN.Mod/STS2PGN.Mod.csproj -c Release \
  -p:STS2GameDataDir="/path/to/platform/data_directory"
```

PowerShell users can run:

```powershell
./build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

`build.ps1` also reads `STS2_GAME_DIR`. Its output is `out/STS2_PGN/`.

## Install the recorder mod

After building, copy these files to the game's `mods/` directory:

```text
STS2_PGN.dll
STS2_PGN.json
config.json
```

`STS2_PGN.dll` includes the portable Core implementation because STS2 does not resolve arbitrary sibling DLL dependencies. The separately built `STS2PGN.Core.dll` is for simulator integrations, command-line tools, and tests; do not install it with the mod.

The game requires the manifest filename to match the mod ID. The source manifest is named `mod_manifest.json`, but the build output renames it to `STS2_PGN.json`.

The mod starts a trace when a supported single-player run launches. Traces are stored under the active profile's `sts2pgn/` directory. Metadata and every accepted action are flushed through to disk immediately, so a crash or save-and-quit leaves a parseable incomplete prefix.

The output directory is configured in `config.json`, installed beside `STS2_PGN.dll`:

```json
{
  "output_directory": "sts2pgn"
}
```

- A relative directory such as `sts2pgn` is resolved beneath the active STS2 profile.
- An absolute OS path writes directly to that directory.
- A Godot path such as `user://my-replays` is converted to its native OS path.
- A missing or invalid configuration falls back to the profile-scoped `sts2pgn` directory and logs the reason.

The configuration is read when the mod initializes, so restart the game after changing it.

The recorder captures the exact STS2 save snapshot, run/room/act lifecycle, map movement, combat actions, synchronized player choices, event and rest choices, rewards, treasure, shop operations, progression controls, and Crystal Sphere interactions. Asynchronous actions are written before any nested choice and followed by a correlated resolve record. Cards, rewards, model IDs, enums, actors, and offered index choices use portable semantic fingerprints. Crystal Sphere records its complete generated 11×11 board and post-generation RNG position before tool changes and logical cell coordinates because STS2 marks that minigame as nondeterministic.

Recorder 0.6.0 also writes full `run.core` observations as the run progresses. These include generated acts and map topology, current room stack and decision offers, player inventories and RNG state, and complete combat state. Observations are emitted only at explicit completion signals: action completion, map generation, room/act events, completed event/reward/rest/shop tasks, combat turn events, and run termination.

Each accepted record is appended and flushed immediately. A clean run end writes the authoritative terminal result, then atomically updates the PGN-style `Result` header. An interrupted run remains a strictly parseable prefix ending in exactly one newline.

## Core library usage

Parse and validate a trace:

```csharp
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Validation;

RunRecord record = Sts2PgnParser.Parse(File.ReadAllText("run.sts2pgn"));
Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);

if (!validation.IsValid)
{
    foreach (ValidationIssue error in validation.Errors)
    {
        Console.Error.WriteLine($"{error.Code}: {error.Message}");
    }
}
```

Create and serialize a record:

```csharp
using STS2PGN.Model;
using STS2PGN.Serialization;

RunRecord record = new RunRecordBuilder()
    .AddTag("Format", "STS2PGN")
    // Add the other required metadata tags.
    .AppendJson("run.start", "{\"data\":{},\"extensions\":{}}")
    .Build();

string text = Sts2PgnWriter.Write(record);
```

`Sts2PgnParser` preserves unknown tags, action kinds, and payload properties. By default it recovers a valid prefix when only the unterminated final JSON record was truncated. Set `RecoverTruncatedFinalLine` to `false` for strict archival validation.

`CanonicalJson.ComputeSha256` sorts object properties and normalizes JSON numbers before hashing portable state observations.

Run a validated trace through a simulator adapter:

```csharp
using STS2PGN.Replay;

IReplaySimulatorAdapter simulator = new MySts2SimulatorAdapter();
ReplayExecutionResult replay = await new ReplayEngine().ExecuteAsync(record, simulator);

if (!replay.Succeeded)
{
    Console.Error.WriteLine($"{replay.Failure!.Kind}: {replay.Failure.Message}");
}
```

`ReplayEngine` validates compatibility and ordering, initializes the adapter from
`run.start`, forwards later action, lifecycle, and resolution records, requests normalized
simulator state at each `state.observe`, and stops at the first illegal action, state
mismatch, or terminal-result mismatch. A recorded `run.finish` is expected output and is
never fed to the adapter. `ReplayTimeline` is available when an integration only needs the
complete ordered, role-classified record stream. See the replay contract for the complete
adapter lifecycle and divergence format.

## Format status

The version-one text grammar, core codec, and state projection are implemented. Projection version 1 defines `run_start.core` and the live `run.core` schema. Incompatible grammar changes require a new `FormatVersion`; incompatible state-schema or comparison changes require a new `projection_version`.

The authoritative design documents are:

- [Format specification](docs/format.md)
- [Replay and simulator contract](docs/replay-contract.md)
- [Action coverage ledger](docs/action-coverage.md)

## License

MIT. See [LICENSE](LICENSE).
