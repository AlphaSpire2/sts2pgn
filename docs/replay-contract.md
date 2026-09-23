# Replay and Simulator Contract

## Objective

A `.sts2pgn` trace is an executable conformance fixture. The real game is the oracle. A simulator consumes the same initial state and player decisions, then compares its settled state to observations emitted by STS2.

The primary output is not merely pass/fail. It is the earliest reproducible divergence with enough context to diagnose which simulated rule differs from the game.

## Responsibilities

### Recorder

The recorder must:

1. capture only decisions accepted by authoritative game code;
2. preserve their deterministic order;
3. identify selected objects semantically and validate positional identifiers with fingerprints;
4. wait for deterministic automatic effects to settle before observing state;
5. emit portable normalized observations;
6. include compatibility metadata and RNG state needed for initialization; and
7. flush complete records so a crash leaves a useful prefix.

The recorder must not treat a UI click as accepted merely because it occurred. Failed purchases, illegal card plays, locked event options, and canceled selections are recorded only when their outcome itself is meaningful to replay.

### Simulator consumer

The simulator adapter must:

1. validate the format and supported projection version;
2. reconstruct the portable initial state from `run.start`;
3. maintain the stable identifiers defined by the format;
4. apply action records in sequence order;
5. execute all automatic consequences before comparing the next observation;
6. normalize its state using the same projection rules; and
7. stop at and report the first mismatch unless explicitly configured to collect later differences.

### In-game playback driver

The playback driver (shipped inside the STS2_PGN mod, `Playback/`) executes a
*decision script* (see the decision-script profile in `docs/format.md`) in the real
game while the normal recorder observes the session and, by default, writes it to disk. A
script is a proposal produced by a simulator; the recording the driver produces is the
conformance artifact and is replayed back through the simulator's comparator. Divergences
are therefore caught twice: at drive time (the game offers something other than the script
expects) and at replay time (observation mismatch). The real game stays the oracle; a
script never is. With `record_playback_runs: false`, ordinary drive sessions still pass
through the recorder so drive-time verification works, but no `.sts2pgn` file is created
and replay-time comparison is intentionally unavailable. Reports are still written.

Launch:

- `--sts2pgn-script=<file|directory|list.txt>` on the game command line. A directory
  runs every `*.sts2pgn` and `*.sts2pgn.gz` in ordinal filename order; a `.txt` list runs one path per line
  (`#` comments). The batch starts once the main menu is ready, runs scripts back to back
  (returning to the main menu between them), writes reports, and quits the game with the
  batch exit code.
- File and list inputs also accept gzip-compressed traces/scripts. Completed output
  recordings are automatically compressed, and the report's `recording_path` points
  to the `.sts2pgn.gz` file. Incomplete and active handoff recordings remain plain.
- The dev-console command `sts2pgn drive <path>` does the same interactively and never
  quits.
- `sts2pgn handoff <file>` accepts exactly one partial script. After its final decision
  settles successfully, the driver detaches without abandoning the run or stopping the
  recorder; subsequent player inputs continue the same authoritative recording. A
  validation failure, mismatch, timeout, exception, or `sts2pgn cancel` retains normal
  cleanup and abandons the playback run rather than handing off a divergent state.

Per script, in order:

1. **Pre-flight — refuse rather than reinterpret.** Parse the file; a recording (has
   `RecorderVersion`, no `Producer`) is converted in memory to a script exactly as
   `sts2pgn to-script` does (Producer `sts2pgn-playback/<version>`, reported as
   `converted_from_recording`). A recording containing `run.resume` is refused as
   `script_resume_not_supported`: the driver starts a fresh seeded run and cannot restore
   the saved checkpoint or choose only the resumed side of an abandoned branch. Validate
   the script in script mode; compare `GameVersion`, `GameCommit`, `ModelIdHash`, `Mode` (`CUSTOM`),
   character, and ascension with the live build and profile; compare
   `run.start.data.state.players[0].unlocks` and `acts[]` with the live profile's unlock
   state and the acts the seed generates on it; refuse when a run is in progress or a run
   save exists. Any failure is `refused_at_start` with every mismatch listed and no run
   started.
2. **Start the run** through the API the custom-run screen uses
   (`NGame.StartNewSingleplayerRun(..., GameMode.Custom, ...)`), with `shouldSave: true`
   so the recorder's save/resume path and `GameStartTime` header match normal play. The
   recorder opens an ordinary recording (`RecorderVersion`, never `Producer`), or a
   transient non-file-backed recording when `record_playback_runs` is false. Handoff
   always opens a file-backed recording regardless of this setting.
3. **Drive, one decision at a time:** *wait* until the game is idle and the record's
   surface is present (no running action, empty queue, no modal, the expected screen or
   phase; while waiting, the driver performs presentation-only steps the recorder never
   writes, such as clicking a rest/shop/treasure room's Proceed button so the map opens) → *verify* the script's fingerprints against what the game offers (hand card
   and target, travelable map point, reward set and entry, event/rest option, merchant
   entry, relic list, every option of a registered choice offer) → *submit* through the
   same game-code path a player's input reaches (see the driver table in
   `docs/action-coverage.md`) → *settle* when the recorder reports the decision recorded
   and its completion observation emitted. A verification mismatch is
   `stopped_at_record` with the record sequence, the expected value, and what the game
   offered. A recorded payload whose identity differs from the script is also
   `stopped_at_record` (`recorded_mismatch`). The driver never remaps an index, set ID,
   or option.
4. **Finish:** `completed` when every decision settled; `game_error` on a driver
   exception, a per-record timeout (`playback_timeout_seconds`, default 30), or an
   unmapped record kind (`unsupported_record`). Standard drive mode returns to the main
   menu. If the run is still active, it first closes the recording as an incomplete
   `Result "*"` prefix, then abandons only the unrecorded live run for cleanup; a run that
   ended naturally keeps its real `VICTORY` or `DEFEAT`. Successful handoff mode instead
   requires an active run and recorder, marks the report `handed_off: true`, detaches the
   driver, and leaves both active for normal play.

Presentation speed is not part of the contract, but it is not free either. The driver never advances
on its own clock, so in principle how long the game takes to present a decision only changes how long
the batch takes. In practice two things are timing-sensitive and must be treated as part of the
contract's surface:

- The `room.enter` observation for a combat room is captured synchronously when
  `RunManager.RoomEntered` fires, which is after `await room.Enter(...)`. How much of combat setup
  has run by then depends on presentation pacing, so removing the game's waits (instant fast mode)
  changes that record's contents. Making game time cheap instead of removing it — launching headless
  with `--fixed-fps` — leaves every wait in place and the trace unchanged.
- The driver must observe the game where the synchronization context is pumped, as `Task.Delay` does,
  not during `ProcessFrame` emission. Polling on the process frame catches the game mid-transition
  and has been observed to drop and duplicate `room.*` records outright.

Waits the driver owns and that are meant to outlast an animation (the selection-screen settle) are
measured with `PresentationDelay`, a `SceneTreeTimer`, so they are denominated in the same game time
the animation is, at any frame rate. Per-step timeouts stay on the wall clock. A trace recorded with
any speed setting must be identical to one recorded without it; that equality is the acceptance test.

Reports are written to the recorder's `output_directory`:

- `<script-name>.playback.json`: `status` (`completed`, `refused_at_start`,
  `stopped_at_record`, `game_error`), `reason`, `message`, `stopped_at_sequence`,
  `stopped_at_decision`, `kind`, `expected`, `offered`, `precondition_failures`,
  `decisions_total`/`decisions_completed`, optional `recording_path` (omitted when playback
  recording is disabled), `handed_off`, optional
  `unlocks_after_run`, and timestamps. `unlocks_after_run` is omitted on handoff because
  the run is still in progress.
- `playback-summary.json`: one row per script plus `exit_code`: `0` all completed, `3`
  any stopped at a record, `4` any refused, `5` any game error (the worst status wins).

Profile drift: the game adds every won encounter to the profile's encounters-seen set
even in Custom mode, so the live unlock state changes after the first run of a batch.
The driver refuses only generation-relevant drift (epochs, first-run status, the seen-status
of the discovery bosses; see `docs/format.md`), warns about the rest, and reports
`unlocks_after_run` so the simulator can generate the next script from the state the
profile is actually in (chained generation).

The driver must never bypass the recorder: nested choices are answered by completing the
game's own selection screen (which makes the game synchronize the accepted result), not
by calling the synchronizer directly or installing a test selector. A driver fault
degrades to a report, never a crash.

Successful playback shows that the script was executable on the real game and yields a
recording that captured the run. It does not by itself validate a simulator; the replay
of the recording does.

## Record processing algorithm

```text
parse and validate trace
verify supported format/projection versions
initialize simulator from run.start portable state

for each subsequent record:
    if record is run.resume:
        discard simulated state
        re-initialize from the run.resume portable state

    if record is an initiating action or nested choice:
        verify expected location and object fingerprints
        apply the action
        settle deterministic automatic effects

    if record is a resolve record:
        verify it references the matching earlier initiating action
        verify the simulator produced the recorded success/failure

    if record is state.observe:
        project and canonicalize simulator state
        compare it to the authoritative projection/digest
        stop and report if different

verify terminal result
```

Consumers must not use a later authoritative observation to repair simulated state during a normal conformance run. A separate diagnostic mode may resynchronize after reporting a mismatch, but must label all subsequent comparisons as post-divergence.

## Portable consumer API

`STS2PGN.Core` implements this algorithm with `ReplayEngine` and
`IReplaySimulatorAdapter`. The engine owns trace validation, record ordering, canonical
comparison, result comparison, and first-failure reporting. The adapter owns only the
simulator-specific behavior:

1. `CheckCompatibilityAsync` declares whether the simulator supports the trace metadata.
2. `InitializeAsync` reconstructs simulator state from the complete `run.start` record. It is called again for every `run.resume` record, where the adapter must discard its current state and re-anchor on the resumed portable state.
3. `ConsumeAsync` applies or verifies each later action, lifecycle, and resolution record and settles its automatic effects.
4. `CaptureStateAsync` returns the simulator's normalized state for an observation request.
5. `GetResultAsync` returns the simulator's terminal result.

Every record is preserved by `ReplayTimeline`. It assigns one processing role without
changing the original kind or payload: initialization, reinitialization, action,
resolution, lifecycle, observation, or terminal. Unknown future kinds default to actions
so adapters can either implement them or reject them explicitly.

`run.finish` is never passed to `ConsumeAsync`. It is an expected outcome, so the engine
asks the adapter for its independently derived result and compares the two. This keeps the
recorded result from becoming input to the simulation under test.

An observation request contains its sequence, preceding action sequence, context,
projection version, and optional scope. It deliberately does not expose the expected
state to the adapter. The adapter must derive state from its own simulation; the engine
then hashes and compares that state with the authoritative observation. This prevents a
consumer from accidentally using an observation to repair the simulation it is testing.

Objects compare independent of property order and JSON numbers compare canonically.
Arrays are ordered unless a projection rule gives them a stable key. The default keyed
paths are `/players` by `slot` and `/combat/creatures` by `combat_id`. Differences use
JSON Pointer paths, preserve the distinction between a missing value and JSON `null`,
and are capped by `ReplayEngineOptions.MaximumDifferences`.

## Save/resume boundaries

STS2 serializes no mid-room state: its last checkpoint is written either when a map
point is entered (before the room's content is rolled) or when a room pre-finishes with
unclaimed rewards. Resuming therefore legally rewinds the run — a player who quits
mid-combat resumes that room from its beginning, and decisions the recorder faithfully
captured after the checkpoint never happened in the resumed timeline.

A `run.resume` record marks that boundary inside the same trace file. It is a
re-anchoring record, not an action:

- Its `data` carries the restored save (`source_snapshot`), the portable live state
  after the game finished restoring (`state`, scope `run.core`), the reload count, and
  the recording mod version. The next `state.observe` (context `run.resume`) duplicates
  `data.state` with its canonical digest, exactly like the `run.start` pattern.
- A consumer must discard its simulated state at `run.resume` and re-initialize from the
  record's payload, then continue consuming subsequent records normally. It must not
  treat pre-resume observations as comparable to post-resume state.
- Decisions between the last checkpoint and the resume remain in the trace as a faithful
  record of the abandoned tail. Divergence detection within that tail still works
  through its own observations; comparisons after the resume start from the re-anchored
  state.
- A mismatch between the simulator's re-anchored state and later observations is still a
  divergence. The re-anchor is initialization input, not a repair channel: the engine
  passes it through `InitializeAsync`, never through an observation comparison.

## Settled-state boundary

An action is settled when all deterministic consequences that must occur before the next player decision have completed.

Examples:

- A played card is settled after its queued effects, hooks, deaths, triggered powers, and resulting player choices complete.
- End turn is settled at the documented turn checkpoint after discard, enemy actions, start-of-turn hooks, and draw processing reach the next actionable phase.
- A reward choice is settled after inventory mutation and reward-screen state update.
- A map choice is settled after room generation/entry and its initial automatic effects.

Animation completion, audio, input debounce, hover state, and wall-clock delays are not part of settling.

If STS2 exposes no public quiescence signal for a boundary, the integration ledger must document the event, queue condition, or narrowly scoped patch used to detect it.

## State projection principles

The portable state projection contains gameplay facts, not object graphs or UI state.

Expected top-level areas include:

- run identity, mode, seed, ascension, act, floor, map history, room stack, and RNG counters;
- each player's character, HP, maximum HP, energy, gold, potion slots, deck, relics, potions, unlock-relevant state, and player RNG counters;
- offered event/rest/shop/reward choices at decision boundaries;
- combat phase, round, turn, piles, combat-card IDs, creatures, HP, block, powers, orbs, intents, and deterministic queue status; and
- result and terminal state.

Every model and enum uses a stable, locale-independent identifier. Optional human-readable labels are ignored during comparison.

Projection version 1 defines two complete scopes. `run_start.core` normalizes generated
acts and saved maps, run/player resources, inventories, RNG counters, odds, relic grab-bag
order, unlocks, empty room/choice state, and null combat state at the boundary immediately
after `run.start`. `run.core` adds the current generated map, room stack, active choices,
reward sets, Crystal Sphere minigame state, and live combat state for every later
observation. The source snapshot remains
available as same-build provenance and an initialization fallback, but a portable simulator
uses the normalized projection as its comparison contract.

## Comparison rules

Each field is classified as one of:

- **exact scalar**: integers, booleans, identifiers, enum values, RNG counters;
- **ordered collection**: draw pile, hand, discard pile, map history, action order;
- **keyed collection**: acts, players, rooms, map points, active choices/rewards, combat players, and creatures keyed by stable ID;
- **set**: values whose order has no gameplay meaning;
- **decimal**: invariant exact decimal representation unless the game rule is explicitly floating point; or
- **ignored provenance**: timestamps, platform, display names, animation, sound, and localization.

Missing and `null` are distinct unless the projection schema explicitly equates them. Consumers must report unsupported fields instead of silently ignoring them.

## Divergence report

A divergence report should contain:

```json
{
  "run_id": "...",
  "sequence": 42,
  "after_action": 41,
  "action_kind": "combat.play_card",
  "action_payload": {
    "actor": "p0",
    "data": { "card_combat_id": 7, "target_combat_id": "m0" }
  },
  "context": "combat.action_settled",
  "expected_digest": "sha256:...",
  "actual_digest": "sha256:...",
  "expected_state": {
    "combat": { "creatures": [{ "combat_id": "m0", "hp": 31 }] }
  },
  "actual_state": {
    "combat": { "creatures": [{ "combat_id": "m0", "hp": 32 }] }
  },
  "differences": [
    {
      "path": "/combat/creatures/m0/hp",
      "expected": 31,
      "actual": 32,
      "expected_present": true,
      "actual_present": true
    }
  ],
  "differences_truncated": false
}
```

Paths use JSON Pointer syntax. Collections keyed during comparison use their stable key in the diagnostic path, even if their wire representation is an array.

Reports should also include the most recent full matching checkpoint, the intervening actions, compatibility warnings, and whether comparison occurred after an earlier resynchronization.

## Compatibility policy

The trace records game version, commit, model database hash, loaded mods, recorder version, and state-projection version.

- In-game replay defaults to strict compatibility and refuses mismatched model hashes or gameplay-affecting mods.
- A simulator declares which format, projection, game builds, and mods it implements.
- Unsupported compatibility is reported separately from state divergence. A version mismatch is not proof of a simulation bug.
- Unsafe overrides must be explicit and included in the final report.

## Error categories

Consumers distinguish:

- `invalid_trace`: syntax, schema, ordering, digest, or fingerprint failure;
- `unsupported_trace`: valid input using unsupported format, projection, game content, or mod semantics;
- `illegal_action`: the simulator cannot apply an action that STS2 accepted;
- `state_divergence`: the action was applied but normalized state differs;
- `result_divergence`: checkpoints matched until terminal outcome differed; and
- `incomplete_trace`: valid crash-recovered prefix with result `*`.

## Phase 4 boundary policy

Ordinary queued actions are observed at `ActionExecutor.AfterActionExecuted`. Operations
whose generic action finishes before their gameplay transition use later signals: map and
room events for travel, combat turn events for end/undo turn, completed event-option tasks,
completed reward-set lifetimes, and correlated reward/rest/shop resolution tasks. Run-end
state is captured before cleanup and written immediately after `run.finish`.

Every recorder observation is a full projection. A simulator must settle the preceding
input using its own equivalent boundary, then return the requested scope. Presentation,
animation, localized text, timestamps, and mod-specific state remain outside version 1;
modded runs are not a supported compatibility target.

### Playback potion readiness after combat

For a combat room that gives rewards, an out-of-combat potion use or discard
waits until the recorder has observed that room's reward offer. The game's
combat-in-progress flag clears before victory cleanup and reward generation
finish, and an idle executor alone does not guarantee those tasks have finished.
Accepting a potion during that gap can make a transient lethal-card observation
the last checkpoint before the next decision. Readiness remains true after the
reward set is dismissed; it is scoped to the room and reset with the recording
session. Noncombat rooms and combats without rewards retain their existing flow.
### Playback transitions after automatic event completion

The driver checks for an already recorded `room.proceed` on every readiness
poll, before submitting a click. An event combat can proceed automatically
after the preceding card's action observation has arrived. The scripted proceed
consumes that automatic record even if it arrived during the wait; it must not
click the parent event's reward screen and produce a second proceed.

A script may go directly from Crystal Sphere's final divination to `map.choose`.
When the minigame is finished and its own Proceed button is enabled, playback
clicks that button to leave the overlay. The real click is still recorded as
`event.crystal_sphere.proceed`. An unfinished board or a reward overlay is never
dismissed by this transition; explicit reward and minigame decisions retain
their normal verification.

Event-option readiness also waits for the event synchronizer's pending option
tasks to complete. A nested reward observation and an idle action executor do
not mean the event has finished updating its page. Card choices and rewards
inside that task remain independently drivable.

When a live selection suspends a rest or event task, an intervening scripted
out-of-combat potion use or discard can run before the selection is answered.
The driver requires a pending choice, an idle executor, and normal potion
readiness; it does not wait for the suspended task to finish first.

An empty deck-enchantment choice bypasses the main confirm, whose handler does
nothing when no card is selected. On a cancelable screen, playback emits the
close button's Released signal so the screen's completion source resolves. On
a non-cancelable screen, playback stops with a fingerprint mismatch before
submission because the game has no input that can produce an empty result.
