# STS2PGN

STS2PGN records Slay the Spire 2 runs as deterministic, human-readable traces inspired by chess PGN.

The long-term purpose is simulator conformance testing: record a run in the real game, feed the resulting `.sts2pgn` file to an external simulator, replay the same decisions, and identify the first state difference between the simulator and STS2.

> [!IMPORTANT]
> Recorder 0.8.0 adds a *decision-script* profile of the format and an in-game *playback driver*: a simulator proposes a run as a script, the mod executes it in the real game while recording normally, and the recording is replayed back through the simulator. The game-coupled assembly still needs to be built and smoke-tested with matching STS2 assemblies.

## Architecture

```text
Real STS2 + recorder mod -> .sts2pgn trace -> external simulator -> divergence report

simulator -> decision script -> playback driver in the real game (recording) -> simulator replay
```

- `STS2PGN.Core` is a game-independent model, parser, writer, validator, and canonical state-hashing library. Simulator integrations need only this library or the documented format.
- `STS2PGN.Mod` is the STS2 mod that converts authoritative game actions and state into portable records.
- `STS2PGN.Core.Tests` tests the portable contract without requiring the game.

The trace contains initial state, ordered semantic actions, compatibility metadata, and authoritative state observations. Cosmetic input and presentation timing are deliberately excluded.

## Repository layout

```text
src/STS2PGN.Core/          Portable trace model and codec
src/STS2PGN.Mod/           STS2 recorder mod and script playback driver
src/STS2PGN.Tool/          sts2pgn CLI: validate (recordings and scripts) and summarize traces
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

## Copy an unmodded save to the modded profile

STS2 keeps unmodded and modded save data in separate profile directories. With the game
closed, preview a one-way copy of profile 1 with:

```powershell
./copy-save-to-modded.ps1 -DryRun
```

Run the same command without `-DryRun` to perform the copy. The script auto-detects the
platform account when there is exactly one candidate, copies `progress.save` by default,
preserves destination-only mod data, and backs up the existing modded `saves` directory
before making changes. It retains the target timestamp when replacing an existing file
because STS2's Steam Cloud startup sync otherwise restores the older cloud contents.
Select a different profile or disambiguate multiple accounts with:

```powershell
./copy-save-to-modded.ps1 -AccountId 76561198000000000 -ProfileId 2 -DryRun
```

Pass `-AllSaveFiles` to also copy active runs, preferences, history, backups, and other
files beneath the unmodded `saves` directory. This broader mode is opt-in because Steam
Cloud can remove a newly created modded file when no remote counterpart exists; the script
warns when this applies. Disable Steam Cloud before launching if a source-only
`current_run.save` or new history file must survive the first startup.

Use `Get-Help ./copy-save-to-modded.ps1 -Detailed` for path overrides and the complete
parameter reference. Progress and active-run files are replaced rather than merged.

## Inspect traces from the command line

The `sts2pgn` CLI is a consumer of the portable core — it never ships with the mod and
needs no game files:

```bash
dotnet build src/STS2PGN.Tool/STS2PGN.Tool.csproj -c Release

# Strict parse + full validation; non-zero exit on any error
dotnet src/STS2PGN.Tool/bin/Release/net9.0/sts2pgn.dll validate my-run.sts2pgn

# Headers, record statistics, run progress, and observation coverage
dotnet src/STS2PGN.Tool/bin/Release/net9.0/sts2pgn.dll summarize my-run.sts2pgn
```

`--no-recover` treats a crash-truncated final line as an error instead of recovering the
prefix; `validate --quiet` prints only files with problems; `validate --script` checks a
decision script against the script profile instead of the recording profile.

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
  "output_directory": "sts2pgn",
  "playback_timeout_seconds": 30,
  "record_playback_runs": true
}
```

- A relative directory such as `sts2pgn` is resolved beneath the active STS2 profile.
- An absolute OS path writes directly to that directory.
- A Godot path such as `user://my-replays` is converted to its native OS path.
- A missing or invalid configuration falls back to the profile-scoped `sts2pgn` directory and logs the reason.

- `playback_timeout_seconds` bounds how long the playback driver waits for any single step.
- `record_playback_runs` controls whether `sts2pgn drive` and command-line playback write
  a new `.sts2pgn` trace. Set it to `false` to drive and validate through the recorder
  without creating a trace file; playback reports are still written and their
  `recording_path` field is omitted. `sts2pgn handoff` always records because control returns to
  the player with that recording active.

The configuration is read when the mod initializes, so restart the game after changing it.

The recorder captures the exact STS2 save snapshot, run/room/act lifecycle, map movement, combat actions, synchronized player choices, event and rest choices, rewards, treasure, shop operations, progression controls, and Crystal Sphere interactions. Asynchronous actions are written before any nested choice and followed by a correlated resolve record. Cards, rewards, model IDs, enums, actors, and offered index choices use portable semantic fingerprints. Crystal Sphere records its complete deterministically generated 11×11 board and post-generation RNG position as observational state, followed by tool changes and logical cell coordinates.

Recorder 0.6.0 also writes full `run.core` observations as the run progresses. These include generated acts and map topology, current room stack and decision offers, player inventories and RNG state, and complete combat state. Observations are emitted only at explicit completion signals: action completion, map generation, room/act events, completed event/reward/rest/shop tasks, combat turn events, and run termination.

Each accepted record is appended and flushed immediately. A clean run end writes the authoritative terminal result, then atomically updates the PGN-style `Result` header. An interrupted run remains a strictly parseable prefix ending in exactly one newline.

## Decision scripts and playback

A *decision script* is a `.sts2pgn` file that proposes a run instead of recording one. It
uses the same grammar and record kinds, but carries a `[Producer "name/version"]` header
instead of `RecorderVersion`, uses `Mode CUSTOM` (the only seeded mode the game starts),
has a `run.start` that states the expected profile unlocks and acts, and lists only the
decisions — no observations, digests, resolves, or `run.finish`. See the profile section
of [docs/format.md](docs/format.md) and `examples/script-minimal.sts2pgn`.

```bash
dotnet src/STS2PGN.Tool/bin/Release/net9.0/sts2pgn.dll validate --script my-script.sts2pgn

# Replay a recorded run through the driver: convert the recording into a script first
dotnet src/STS2PGN.Tool/bin/Release/net9.0/sts2pgn.dll to-script my-run.sts2pgn --producer sts2pgn-tool/0.8.0
```

The driver performs the same conversion automatically when it is given a recording
(the report says `converted_from_recording: true`); `to-script` is for producing the
script file explicitly, for example to hand-edit it. Either way the conversion strips the
recording to decisions, swaps `RecorderVersion` for `Producer`, and sets `Mode CUSTOM`.

A recording containing `run.resume` cannot be converted or driven. Playback always starts
a fresh seeded Custom run; it cannot restore the save checkpoint embedded in `run.resume`
or choose only the authoritative side of an abandoned/resumed branch. `to-script`, direct
`drive`, and `handoff` therefore refuse such a recording with
`script_resume_not_supported` instead of silently concatenating both sets of decisions.

The playback driver lives in the mod and executes scripts in the real game while the
recorder observes the session and, by default, writes an authoritative recording or
recording prefix:

```bash
# One script, a directory of scripts, or a .txt list (one path per line); quits with an exit code
"Slay the Spire 2" --sts2pgn-script="C:\scripts\batch"
```

or, from the in-game dev console, `sts2pgn drive <path>` (never quits). To drive a
single partial script and then continue the same run manually, use:

```text
sts2pgn handoff <partial-script.sts2pgn>
```

After the partial script's last decision settles, the driver detaches and leaves the
run, save, and recorder active. Every manual action afterward is appended to the same
authoritative recording through the normal recorder path. Handoff accepts exactly one
script and reports `handed_off: true`; it is successful only if the script leaves an
active recorded run. A handoff report omits `unlocks_after_run` because the run has not
ended. `sts2pgn cancel` aborts and abandons playback—it is not a handoff.

For a takeover at Crystal Sphere, convert a known recording with `to-script`, remove
the decisions after the desired boundary (for example, the `map.choose` that enters its
node), validate the partial script, then run `sts2pgn handoff` on it.

For every script the driver first checks the game build (`GameVersion`, `GameCommit`,
`ModelIdHash`), the character, the ascension, and the live profile's unlock state and
seeded acts, and refuses to start on any mismatch that would change the generated run
(unlock differences the game never reads during generation are only logged). It then starts the seeded custom run and
drives one decision at a time: wait for the game to be idle and the right screen to be up,
verify the script's fingerprints against what the game actually offers, submit through the
same game-code path a player's input reaches, and wait for the recorder to record and
settle it. A mismatch stops the script at that record with the expected and offered values
— the first game-versus-simulator divergence.

Reports go to the recorder's output directory: `<script>.playback.json` per script
(`completed`, `refused_at_start`, `stopped_at_record`, or `game_error`, with the record,
expected/offered values, recording path, the `handed_off` flag, and the profile's unlock
state after a run that playback itself ends) and `playback-summary.json` for the batch.
The process exit code is `0` when every script completed, otherwise `3` (stopped at a
record), `4` (refused), or `5` (game error).

If the driven decisions naturally finish the run, the recording carries the game's real
`VICTORY` or `DEFEAT`. If the script ends or stops while the run is still active, standard
playback closes the recording first as an incomplete `Result "*"` prefix and only then
abandons the live run for batch cleanup. The cleanup therefore never manufactures an
`ABANDONED` result inside the recorded evidence. Handoff mode leaves both the run and
recording open instead.
Details are in [docs/replay-contract.md](docs/replay-contract.md).

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

Load a decision script (validates it in script mode and extracts the drivable decisions):

```csharp
using STS2PGN.Scripting;

DecisionScript script = DecisionScript.Load(File.OpenText("proposal.sts2pgn"));
Console.WriteLine($"{script.Identity.Seed}: {script.Decisions.Count} decisions");
```

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
