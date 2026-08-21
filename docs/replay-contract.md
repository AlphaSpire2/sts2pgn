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
game while the normal recorder records the session. A script is a proposal produced by a
simulator; the recording the driver produces is the conformance artifact and is replayed
back through the simulator's comparator. Divergences are therefore caught twice: at
drive time (the game offers something other than the script expects) and at replay time
(observation mismatch). The real game stays the oracle; a script never is.

Launch:

- `--sts2pgn-script=<file|directory|list.txt>` on the game command line. A directory
  runs every `*.sts2pgn` in ordinal filename order; a `.txt` list runs one path per line
  (`#` comments). The batch starts once the main menu is ready, runs scripts back to back
  (returning to the main menu between them), writes reports, and quits the game with the
  batch exit code.
- The dev-console command `sts2pgn drive <path>` does the same interactively and never
  quits.

Per script, in order:

1. **Pre-flight — refuse rather than reinterpret.** Parse the file; a recording (has
   `RecorderVersion`, no `Producer`) is converted in memory to a script exactly as
   `sts2pgn to-script` does (Producer `sts2pgn-playback/<version>`, reported as
   `converted_from_recording`). Validate the script in script mode; compare `GameVersion`, `GameCommit`, `ModelIdHash`, `Mode` (`CUSTOM`),
   character, and ascension with the live build and profile; compare
   `run.start.data.state.players[0].unlocks` and `acts[]` with the live profile's unlock
   state and the acts the seed generates on it; refuse when a run is in progress or a run
   save exists. Any failure is `refused_at_start` with every mismatch listed and no run
   started.
2. **Start the run** through the API the custom-run screen uses
   (`NGame.StartNewSingleplayerRun(..., GameMode.Custom, ...)`), with `shouldSave: true`
   so the recorder's save/resume path and `GameStartTime` header match normal play. The
   recorder opens an ordinary recording (`RecorderVersion`, never `Producer`).
3. **Drive, one decision at a time:** *wait* until the game is idle and the record's
   surface is present (no running action, empty queue, no modal, the expected screen or
   phase) → *verify* the script's fingerprints against what the game offers (hand card
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
   unmapped record kind (`unsupported_record`).

Reports are written to the recorder's `output_directory`:

- `<script-name>.playback.json`: `status` (`completed`, `refused_at_start`,
  `stopped_at_record`, `game_error`), `reason`, `message`, `stopped_at_sequence`,
  `stopped_at_decision`, `kind`, `expected`, `offered`, `precondition_failures`,
  `decisions_total`/`decisions_completed`, `recording_path`, `unlocks_after_run`, and
  timestamps.
- `playback-summary.json`: one row per script plus `exit_code`: `0` all completed, `3`
  any stopped at a record, `4` any refused, `5` any game error (the worst status wins).

Profile drift: the game adds every won encounter to the profile's encounters-seen set
even in Custom mode, so the live unlock state changes after the first run of a batch.
The driver stays strict and reports `unlocks_after_run` so the simulator can generate the
next script from the state the profile is actually in (chained generation).

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
