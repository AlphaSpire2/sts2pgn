# STS2PGN

STS2PGN records Slay the Spire 2 runs as deterministic, human-readable traces inspired by chess PGN.

The long-term purpose is simulator conformance testing: record a run in the real game, feed the resulting `.sts2pgn` file to an external simulator, replay the same decisions, and identify the first state difference between the simulator and STS2.

> [!IMPORTANT]
> Phase 3 recorder implementation is in progress. The portable core and durable append writer are tested; the game-coupled recorder has been implemented against the current decompile but still needs to be compiled and smoke-tested with the STS2 assemblies.

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
docs/format.md             Version-one file format proposal
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

The recorder currently captures the exact STS2 save snapshot, run/room/act lifecycle, map movement, combat actions, synchronized player choices, event and rest choices, rewards, treasure, successful shop purchases, progression controls, and Crystal Sphere interactions. Crystal Sphere records its complete generated 11×11 board before recording tool changes and logical cell coordinates, because STS2 marks that minigame as nondeterministic.

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

## Format status

The version-one text grammar and core codec are implemented. The portable state projection remains a proposal until the observation work in Phase 4 is complete, so examples and state schemas may still change without migration support. Once version 1 is declared stable, incompatible grammar changes require a new `FormatVersion`, while comparison-schema changes require a new `projection_version`.

The authoritative design documents are:

- [Format specification](docs/format.md)
- [Replay and simulator contract](docs/replay-contract.md)
- [Action coverage ledger](docs/action-coverage.md)

## License

MIT. See [LICENSE](LICENSE).
