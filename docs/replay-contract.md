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

### In-game validation replayer

The in-game replayer is a completeness test for the trace. It reconstructs the source snapshot and feeds decisions through authoritative STS2 APIs. It must disable normal saves, profile progress, achievements, metrics, run history, and leaderboard submission.

Successful in-game replay shows that the trace captured enough input. It does not by itself validate a simulator.

## Record processing algorithm

```text
parse and validate trace
verify supported format/projection versions
initialize simulator from run.start portable state

for each subsequent record:
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
2. `InitializeAsync` reconstructs simulator state from the complete `run.start` record.
3. `ConsumeAsync` applies or verifies each later action, lifecycle, and resolution record and settles its automatic effects.
4. `CaptureStateAsync` returns the simulator's normalized state for an observation request.
5. `GetResultAsync` returns the simulator's terminal result.

Every record is preserved by `ReplayTimeline`. It assigns one processing role without
changing the original kind or payload: initialization, action, resolution, lifecycle,
observation, or terminal. Unknown future kinds default to actions so adapters can either
implement them or reject them explicitly.

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

The first implemented scope is `run_start.core` in projection version 1. It normalizes
run/player resources, inventories, RNG counters, odds, relic grab-bag order, unlocks,
and gameplay extra fields from the authoritative run-start save. It is emitted at the
unambiguous settled boundary immediately after `run.start`. Generated acts/maps,
room state, choices, and combat state remain explicit later Phase 4 scopes; until those
are implemented, the initial source snapshot remains necessary for full reconstruction.

## Comparison rules

Each field is classified as one of:

- **exact scalar**: integers, booleans, identifiers, enum values, RNG counters;
- **ordered collection**: draw pile, hand, discard pile, map history, action order;
- **keyed collection**: players and creatures keyed by stable ID;
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

## Remaining Phase 4 decisions

- The smallest reliable STS2 signal that all effects of each action have settled.
- Which post-start action boundaries merit a full projection versus a digest-only observation.
- The exact portable representation of mod-specific mutable fields.
- Whether source snapshots remain inline JSON or use a separately encoded block when large.
- The stable ordering rules for all dictionary- and set-like STS2 collections.
