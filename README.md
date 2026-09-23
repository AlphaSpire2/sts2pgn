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

All three commands (`validate`, `summarize`, and `to-script`) read plain `.sts2pgn`
and gzip-compressed `.sts2pgn.gz` files transparently. `to-script` writes a plain
decision script; its default output for `run.sts2pgn.gz` is `run.script.sts2pgn`.
Standard gzip tools can compress or decompress files for other consumers.

## Install the recorder mod

After building, copy these files to the game's `mods/` directory:

```text
STS2_PGN.dll
STS2_PGN.json
STS2_PGN.config.json
```

`STS2_PGN.dll` includes the portable Core implementation because STS2 does not resolve arbitrary sibling DLL dependencies. The separately built `STS2PGN.Core.dll` is for simulator integrations, command-line tools, and tests; do not install it with the mod.

The game requires the manifest filename to match the mod ID. The source manifest is named `mod_manifest.json`, but the build output renames it to `STS2_PGN.json`.

The mod starts a trace when a supported single-player run launches. By default, traces are stored in the `sts2pgn/` directory under the user's system Documents directory. Metadata and every accepted action are flushed through to disk immediately, so a crash or save-and-quit leaves a parseable incomplete prefix.

Finished runs are automatically compressed to `.sts2pgn.gz`. After finalizing the
result, the recorder writes and flushes a temporary gzip file, verifies that its
decompressed SHA-256 matches the finalized trace, publishes it, and removes the plain
file. A compression failure is logged and leaves the plain recording available;
an existing compressed destination is never overwritten. Active, saved-and-quit,
and interrupted runs remain plain `.sts2pgn` files for append and crash recovery.
Older recordings are not converted automatically. Consumers outside this repository
must support gzip input or receive a decompressed copy.

The output directory is configured in `STS2_PGN.config.json`, installed beside `STS2_PGN.dll`:

```json
{
  "output_directory": "documents://sts2pgn",
  "playback_timeout_seconds": 30,
  "record_playback_runs": true,
  "fast_playback": true
}
```

- A `documents://` path is resolved beneath the user's system Documents directory.
- A relative directory such as `sts2pgn` is resolved beneath the active STS2 profile.
- An absolute OS path writes directly to that directory.
- A Godot path such as `user://my-replays` is converted to its native OS path.
- A missing or invalid configuration uses `documents://sts2pgn`. If the system Documents directory cannot be located, the mod falls back to the profile-scoped `sts2pgn` directory and logs the reason.

- `playback_timeout_seconds` bounds how long the playback driver waits for any single step.
- `fast_playback` drops presentation pacing for the duration of a playback batch. See
  [Fast and headless playback](#fast-and-headless-playback).
- `record_playback_runs` controls whether `sts2pgn drive` and command-line playback write
  a new `.sts2pgn` trace. Set it to `false` to drive and validate through the recorder
  without creating a trace file; playback reports are still written and their
  `recording_path` field is omitted. `sts2pgn handoff` always records because control returns to
  the player with that recording active.

The configuration is read when the mod initializes, so restart the game after changing it.

The recorder captures the exact STS2 save snapshot, run/room/act lifecycle, map movement, combat actions, synchronized player choices, event and rest choices, rewards, treasure, shop operations, progression controls, and Crystal Sphere interactions. Asynchronous actions are written before any nested choice and followed by a correlated resolve record. Cards, rewards, model IDs, enums, actors, and offered index choices use portable semantic fingerprints. Crystal Sphere records its complete deterministically generated 11×11 board and post-generation RNG position as observational state, followed by tool changes and logical cell coordinates.

Recorder 0.6.0 also writes full `run.core` observations as the run progresses. These include generated acts and map topology, current room stack and decision offers, player inventories and RNG state, and complete combat state. Observations are emitted only at explicit completion signals: action completion, map generation, room/act events, completed event/reward/rest/shop tasks, combat turn events, and run termination.

Each accepted record is appended and flushed immediately. A clean run end writes the authoritative terminal result, atomically updates the PGN-style `Result` header, and compresses the finalized recording. An interrupted run retains the existing plain-text prefix recovery behavior.

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

# Optional: override STS2_PGN.config.json's output_directory for this launch only. Required when
# running several playback processes at once, because they all read the same installed config.
"Slay the Spire 2" --sts2pgn-script="C:\scripts\batch" --sts2pgn-out="C:\out\batch-1"
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

Command-line playback also takes `--headless --fixed-fps 60`, which runs it without a window
and several times faster without changing the recorded trace; see
[Fast and headless playback](#fast-and-headless-playback).

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

## Fast and headless playback

A playback batch is unattended, so the game never has to present anything to a player and does not
need a window. The recipe:

```powershell
$game = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"

# Steamworks has to initialize or the game blocks on an error popup; see below.
$env:SteamAppId = "2868840"

& "$game\SlayTheSpire2.exe" --headless --fixed-fps 60 `
    "--sts2pgn-out=C:\out\batch-1" "--sts2pgn-script=C:\scripts\batch"
```

It exits with the usual batch code (`0` all completed, `3` stopped at a record, `4` refused,
`5` game error) and writes the same per-script reports and `playback-summary.json` into
`--sts2pgn-out`. Progress goes to the game log at
`%APPDATA%\SlayTheSpire2\logs\godot.log`, since there is no window to watch.

Measured on a five-script batch (1262 driven decisions), wall clock including game boot:

| configuration | time | result |
|---|---|---|
| as shipped before, windowed or headless | 1286 s | reference |
| `--headless --fixed-fps 60` | **190 s** | same stop points, traces identical |
| two concurrent instances, same flags | **127 s** | same stop points, traces identical |

That is 6.8x serially and 10.1x across two processes, with every recorded trace byte-for-byte the
same as the reference apart from the save snapshot's wall-clock timestamps. Headless alone buys
almost nothing — playback is wait-bound, not frame-bound. Its value is that it permits the uncapped
frame rate, makes `--fixed-fps` meaningful, and lets instances run concurrently.

### Why `--fixed-fps` and not a faster game

`--fixed-fps <n>` feeds the main loop a synthetic frame delta and stops synchronising it to real
time. Every wait, tween, and timer still happens, still takes the same number of game-seconds, and
still happens in the same order — the whole schedule simply costs less wall clock. That is why the
trace does not move. Use 60: it is the rate the game's animations are authored against, and a lower
number advances more game time per real second than a higher one.

The alternative — telling the game to *skip* its waits — is equally fast and does change the trace,
so it is off by default. See the levers below.

### The game guards on two clocks

Once game time outruns real time, the two stop being interchangeable, and the game uses both.
Animations are tweens, advanced by the process delta — game time. But
`NChooseACardSelectionScreen.SelectHolder` ignores presses until
`Time.GetTicksMsec() - _openedTicks > 350`, which is real time.

A presentation wait denominated only in game time therefore clears the 0.4 s entry tween and still
lands inside that 350 ms real-time window, where the press is silently dropped and the batch hangs
on a screen that never closes. `PresentationDelay` waits for **both** clocks for this reason. It
also means each such wait costs its full duration in real time however fast the game runs, which is
the floor on how fast a batch can get.

### `fast_playback`

`fast_playback` (default `true`) is active only for the duration of a batch and is restored before a
handoff hands control back to the player. By default it enables exactly one lever, the only one
measured to leave the trace unchanged:

- **`uncapped_frame_rate`** — replaces the player's FPS limit with no limit, headless only. Worth
  about 1.2x alone; combined with `--fixed-fps` it is what turns a synthetic delta into real speed.

The other three are selectable by name (`"fast_playback": ["uncapped_frame_rate", ...]`) but are not
on by default, because each was measured to change recorded output or buy nothing:

- **`instant_presentation`** — as fast as `--fixed-fps`, but it removes waits instead of shortening
  them, so the `room.enter` observation for a combat room is captured after combat has begun
  (`phase=start`, `energy=3`, one card in hand) instead of before it. Seven records of 520 moved.
- **`non_interactive`** — the game's own smoke-test mode. Worth about 1% once the frame rate is
  uncapped, which does not justify being the one lever that changes `ActionExecutor`'s control flow.
- **`frame_polling`** — polls the driver on the scene tree's process frame instead of the 50 ms
  wall-clock interval. **Known unsafe.** It makes the driver observe and act during `ProcessFrame`
  emission rather than where the synchronization context is pumped, catching the game mid-transition.
  Observed dropping a shop's `room.enter` entirely, writing a `room.exit` with a null payload,
  recording the following `room.enter` twice, and hanging a batch in a shop. Kept only so the result
  stays reproducible.

Set `fast_playback` to `false` to drive at normal presentation speed.

`Engine.TimeScale` looks like an equivalent runtime knob and is not: `NHitStop` sets it absolutely
(0.1 then back to 1.0) whenever certain monsters land a hit, so it would be silently reset mid-run.

### Headless needs Steam

Headless boots to the main menu in about 12 s, but the game blocks on an error popup during platform
init if Steamworks cannot start. Let it attach to the running client by setting `SteamAppId=2868840`
in the environment.

The game also accepts `--force-steam=off`, which skips the platform entirely — **do not use it for
conformance runs.** It changes the recorded `platform_type` from `steam` to `none`, and one script
that otherwise stops at its expected record instead failed to drive an out-of-combat
`combat.discard_potion` 60 decisions earlier. The same script on the same isolated profile with
Steam enabled behaves exactly like the reference.

Driving itself is unaffected by headless: every dispatcher submits through
`ForceClick()`/`EmitSignal(Pressed)` rather than synthetic mouse input, which is what the game's own
automation helper documents as the way to "bypass hover/focus/pause checks that can fail in
headless/automated testing".

### Running batches in parallel

Two concurrent instances ran the five-script batch with every stop point and every trace matching
the serial reference. Instances need isolating in two places:

- **Reports and traces.** All instances share the installed `STS2_PGN.config.json`, so pass
  `--sts2pgn-out=<dir>` to give each process its own output directory. Without it their
  `playback-summary.json` files overwrite each other.
- **`user://`.** Saves, profile progress, and logs live there. On Windows Godot resolves `user://`
  under `%APPDATA%`, so setting `APPDATA` per process gives each instance its own tree. Seed each one
  with `steam/<id>/{settings.save,profile.save}` and
  `steam/<id>/modded/profile<n>/saves/{progress.save,prefs.save}` so script pre-flight sees the
  unlocks it expects, and leave `current_run.save` out so each instance starts its own run.

Keep Steam Cloud off: every instance is a separate local tree that the cloud would try to reconcile.

Splitting a batch across two processes, each driving its own `.txt` list of scripts:

```powershell
$game = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
$acct = "steam\76561198026368513"          # your platform account folder under %APPDATA%\SlayTheSpire2
$env:SteamAppId = "2868840"

# Resolve the real profile once: the loop below reassigns APPDATA, so reading it inside
# would make the second instance seed itself from the first one's tree.
$src = "$env:APPDATA\SlayTheSpire2\$acct"

$procs = 1..2 | ForEach-Object {
    $root = "C:\sts2-par\inst$_"

    # One user:// tree per process, seeded with just enough for pre-flight to pass.
    $dst = "$root\SlayTheSpire2\$acct"
    New-Item -ItemType Directory -Force "$dst\modded\profile1\saves" | Out-Null
    Copy-Item "$src\settings.save","$src\profile.save" $dst -Force
    Copy-Item "$src\modded\profile1\saves\progress.save" `
              "$src\modded\profile1\saves\prefs.save" "$dst\modded\profile1\saves" -Force

    $env:APPDATA = $root                     # Start-Process inherits this
    Start-Process "$game\SlayTheSpire2.exe" -WorkingDirectory $game -PassThru -ArgumentList @(
        "--headless", "--fixed-fps", "60",
        "--sts2pgn-out=C:\sts2-par\out$_",
        "--sts2pgn-script=C:\sts2-par\list$_.txt")
}
$procs | ForEach-Object { $_.WaitForExit() }
$procs | ForEach-Object { "PID $($_.Id) exit $($_.ExitCode)" }
```

Do not copy `current_run.save` into a seeded tree: each instance has to start its own run, and a
leftover run save makes pre-flight refuse.

### Comparing two recordings

Exclude, or two runs of the same script will look divergent when they are not:

- the `RunId`, `StartedAt`, and `GameStartTime` headers;
- the save snapshot's `run_time`, `save_time`, `start_time`, and `platform_type`;
- `encounters_seen` — mutable profile progress that grows as runs are played and feeds every
  `state.observe` digest.

One genuine caveat remains. The `room.enter` observation for a combat room is captured synchronously
when `RunManager.RoomEntered` fires, which is after `await room.Enter(...)`, so how much of combat
setup has run by then depends on presentation pacing. Across 2915 records it moved once, flipping
`player_actions_disabled`. Anchoring that observation to an explicit completion signal would remove
the last timing sensitivity in the trace.

## Core library usage

Parse and validate a trace:

```csharp
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Serialization;
using STS2PGN.Validation;

using StreamReader reader = Sts2PgnFile.OpenText("run.sts2pgn.gz"); // Also accepts plain traces.
RunRecord record = Sts2PgnParser.Parse(reader);
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
using STS2PGN.Serialization;

using StreamReader reader = Sts2PgnFile.OpenText("proposal.sts2pgn"); // Or .sts2pgn.gz.
DecisionScript script = DecisionScript.Load(reader);
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
