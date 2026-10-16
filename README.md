# STS2PGN

Record and replay Slay the Spire 2 runs. STS2PGN saves your decisions and game
state in structured files inspired by chess's Portable Game Notation (PGN).

- Record single-player runs automatically as you play.
- Replay a run in the game, or step through its decisions one at a time.
- Play a prepared sequence of decisions, then take control yourself.

## Install

You need Slay the Spire 2 and the .NET 9 SDK to build the mod. From this
repository, run the following in PowerShell, replacing the game path:

```powershell
./build.ps1 -GameDir "C:\path\to\Slay the Spire 2"
```

Alternatively, build directly with the .NET SDK:

```text
dotnet build src/STS2PGN.Mod/STS2PGN.Mod.csproj -c Release -o out/STS2_PGN -p:STS2GameDir="/path/to/Slay the Spire 2"
```

Close the game, then copy these three files from `out/STS2_PGN/` into the
`mods/` directory beside the game executable (create it if needed):

```text
mods/
  STS2_PGN.dll
  STS2_PGN.json
  STS2_PGN.config.json
```

Launch the game and allow mod loading if prompted. The game log should contain
`[STS2PGN] v... initialized` when the mod loads successfully.

## Usage

**Recording.** Start a new single-player run and play normally. Recordings go to
`sts2pgn/` inside your system Documents folder by default.

An active or interrupted run is saved as `.sts2pgn`. A completed run is compressed
to `.sts2pgn.gz`; playback accepts either form without manual decompression.
Saving and quitting keeps the recording available to continue when you resume.
Keep the recording with the save when moving a run to another computer.

**Playback.** Start at the main menu with no active saved run. Open the in-game
console with the backtick or apostrophe key, enter a command below, then close
the console. Replace `<file>` with the full path to your recording or script;
wrap paths containing spaces in double quotes.

| Command | What it does |
| --- | --- |
| `sts2pgn drive <file>` | Plays the decisions automatically, then returns to the main menu. |
| `sts2pgn step <file>` | Waits for you to advance each decision. |
| `sts2pgn handoff <file>` | Plays a partial script, then leaves the run under your control. |
| `sts2pgn cancel` | Stops playback and abandons its run. |

For example:

```text
sts2pgn step "C:\Users\Player\Documents\sts2pgn\my-run.sts2pgn.gz"
```

In step mode, click **Next step** or press **F8**. The overlay shows nearby
script actions; **Hide preview** collapses it. Animations and enemy actions
continue between steps. Avoid making gameplay choices yourself while stepping,
as they can invalidate the remaining decisions.

A recording contains decisions and observed game state. A *decision script*
contains the decisions to execute; playback converts supported recordings
automatically. Handoff needs a partial script that ends while the run is still
active. Your subsequent manual play is recorded in the same file. Cancel is
not a way to take control of a playback run.

Playback recordings and reports go to `Documents/sts2pgn/playback/` by default.
If playback stops or refuses to start, check the corresponding
`<script>.playback.json` report for the reason.

**Settings.** Edit `STS2_PGN.config.json` beside the installed DLL, then restart
the game. The most useful settings are:

| Setting | Default | Purpose |
| --- | --- | --- |
| `output_directory` | `"documents://sts2pgn"` | Where normal recordings are saved. |
| `playback_output_directory` | `"documents://sts2pgn/playback"` | Where playback recordings and reports are saved. |
| `playback_step_key` | `"F8"` | Step shortcut; `"None"` uses only the overlay button. |
| `record_playback_runs` | `true` | Save a new recording during playback. Handoff always records. |
| `record_partial_runs` | `false` | Record a resumed save even when its earlier recording is missing. |

Directory settings accept `documents://` paths or absolute paths. Relative paths
are resolved under the active game profile. The two output directories are
configured independently. If the step shortcut conflicts with a game binding,
use the overlay button or choose another key.

## Limitations

- **Single-player only.** Multiplayer recording and playback are not supported.
- **Playback requires compatible game and profile data.** The game build,
  character, ascension access, and relevant unlocks must match the recording or
  script. Playback reports mismatches before starting.
- **Playback starts a fresh seeded Custom run.** Finish or abandon an existing
  saved run first; playback refuses to overwrite it.
- **Resumed recordings cannot be replayed.** Recording can continue across
  save-and-quit, but playback cannot restore the checkpoints in files containing
  `run.resume`.
- **A missing earlier recording means a resumed run is not recorded by default.**
  Set `record_partial_runs` to `true` to capture the remaining portion. It will
  lack the earlier decisions needed to replay the whole run.
- **Modded and unmodded profiles are separate.** Progress and unlocks from your
  unmodded profile may not be available when playing with the mod.

Licensed under [MIT](LICENSE).
