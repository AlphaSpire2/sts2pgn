# STS2PGN Implementation Plan

## Progress

- Phase 1 completed: repository scaffold, mod build contract, format proposal, replay contract, and action-coverage ledger.
- Phase 2 completed: portable trace model, append-oriented builder, streaming parser, deterministic writer, validation, canonical JSON/SHA-256 hashing, forward-compatible unknown records, crash-prefix recovery, and automated tests.
- Phase 3 completed and trace-validated with recorder 0.4.2: the recorder captures the supported single-player decision surface, writes portable fingerprints and correctly ordered nested operations, handles Crystal Sphere explicitly, durably flushes each record, and finalizes results atomically.
- Phase 4 implementation completed in recorder 0.6.0: `run_start.core` and `run.core` cover generated acts/maps, rooms and offered choices, player state, live combat, settled actions, turns, reward/shop/rest boundaries, act transitions, and run end. A fresh in-game trace validation remains an acceptance task for Phase 6 because matching game assemblies are not available in this workspace.
- Phase 5 portable foundation completed: the Core provides an ordered replay timeline, simulator-adapter contract, canonical state comparison, and first-divergence/result reporting. Game-coupled playback can now build on the completed Phase 4 projection contract.
- 2026-08-19: a full in-game trace recorded by 0.6.0 (Ironclad, defeat at the act boss) parses and validates cleanly offline, partially discharging the Phase 4 fresh-trace validation; an in-game acceptance pass under Phase 6 still stands.
- Save/resume continuation implemented in recorder 0.7.0: resuming a saved run appends to the run's incomplete trace (matched by the new `GameStartTime` header, the game's own run key) after removing any crash-truncated final line, and records `run.resume` as a re-anchoring boundary carrying the restored snapshot plus portable `run.core` state. The Core validator, replay engine (`Reinitialization` role → `InitializeAsync`), and docs cover the contract; needs in-game validation with matching assemblies.
- `sts2pgn` CLI added (`src/STS2PGN.Tool`): `validate` and `summarize` commands over the portable Core, for acceptance runs and trace inspection.

## Goal

Create a model and Slay the Spire 2 mod that record every gameplay decision and all compatibility information needed to replay a run. The record should be deterministic, durable, human-readable, and inspired by chess PGN.

The ultimate goal is to feed a `.sts2pgn` file produced from the real game into an external STS2 simulator. The simulator will replay the recorded decisions, compare its state with authoritative observations from the real game, and report the first point at which its behavior diverges.

The implementation will use STS2's existing deterministic combat replay architecture as its foundation. The game already records an initial `SerializableRun`, synchronized game actions, mid-action player choices, and checksums in `decomp/src/MegaCrit.Sts2.Core.Multiplayer.Replay/CombatReplayWriter.cs`. STS2PGN will extend those concepts across an entire run and expose them in a readable, versioned format.

## Ultimate Workflow

```text
Real STS2 game
    │
    │ STS2PGN recorder mod
    ▼
.sts2pgn authoritative trace
    │  ├── initial state and compatibility metadata
    │  ├── ordered semantic player actions
    │  └── authoritative state observations/checkpoints
    ▼
External STS2 simulator
    │
    ├── reconstruct initial state
    ├── replay each recorded action
    ├── compare simulated and recorded state
    └── report the first divergence and relevant field-level differences
```

The format is therefore both a replay notation and a conformance-test fixture. It must be consumable without loading STS2 or referencing game assemblies. An in-game replayer remains valuable for validating that a trace is complete, but the external simulator is the primary long-term consumer.

## Initial Scope

The first release will support complete single-player runs in standard, daily, and custom modes.

Supported replay requires no gameplay-affecting mods. The recorder still stores the loaded mod manifest list so consumers can reject unsupported traces clearly; reproducing arbitrary mod behavior is outside the initial scope.

The data model will include actor identifiers and deterministic ordering so multiplayer can be added without redesigning the format, but multiplayer replay is not part of the initial milestone.

Replay means reproducing gameplay decisions and resulting game state. Cosmetic inputs such as mouse movement, hover animations, animation timing, and sound are excluded. Progression inputs such as dialogue advancement, confirmation, cancellation, and proceed actions are included when the game requires them.

The simulator itself is outside the initial project scope. This project will define and implement the portable model, authoritative recorder, parser, comparison contract, and fixtures that a simulator needs to consume.

## Proposed Project Structure

```text
sts2pgn/
├── STS2PGN.sln
├── src/
│   ├── STS2PGN.Core/
│   │   ├── Model/
│   │   ├── Parsing/
│   │   ├── Serialization/
│   │   └── Validation/
│   └── STS2PGN.Mod/
│       ├── Recording/
│       ├── Replay/
│       ├── Integration/
│       └── STS2PgnMod.cs
├── tests/
│   └── STS2PGN.Core.Tests/
├── docs/
│   ├── format.md
│   ├── action-coverage.md
│   └── replay-contract.md
├── examples/
├── mod_manifest.json
├── build.ps1
├── README.md
└── LICENSE
```

The mod project will follow the build, manifest, `ModInitializer`, Godot, and Harmony conventions in `../sts2mcp/STS2_MCP.csproj`, `../sts2mcp/mod_manifest.json`, and `../sts2mcp/McpMod.cs`.

## STS2PGN File Format

Records will use the `.sts2pgn` extension. A file will be append-only and human-readable, with PGN-style headers followed by sequential action records whose payloads are JSON objects.

Example:

```text
[Format "STS2PGN"]
[FormatVersion "1"]
[GameVersion "0.103.2"]
[GameCommit "..."]
[ModelIdHash "..."]
[Seed "ABC123"]
[Mode "STANDARD"]
[Ascension "3"]
[Players "IRONCLAD"]
[Result "*"]

1 run.start {"snapshot":{...}}
2 map.choose {"act":0,"row":0,"col":3}
3 combat.play_card {"card":{"model":"STRIKE","instance":12},"target":{"combat_id":2}}
4 combat.end_turn {"turn":1}
5 state.observe {"context":"turn.end","digest":"sha256:...","state":{"player":{"hp":72,"gold":99},"combat":{"round":1}}}
6 reward.choose_card {"model":"POMMEL_STRIKE","reward_index":1}
7 room.proceed {}
*
```

The format will provide:

- PGN-style tag pairs for searchable metadata.
- Monotonically increasing sequence numbers for deterministic ordering.
- Stable model and instance identifiers instead of localized display names.
- An initial `SerializableRun` snapshot containing setup state, maps, unlocks, modifiers, RNG counters, inventories, and modded state.
- Canonical state observations and checkpoints from the real game for detecting simulator divergence.
- Explicit results such as `VICTORY`, `DEFEAT`, `ABANDONED`, and `*` for an unfinished record.
- Preservation of unknown tags and action types so newer files can survive a read/write cycle in older tooling.
- A documented format version and migration policy.

## Core Data Model

The game-independent core library will define:

- `RunRecord`: headers, initial snapshot, ordered records, and result.
- `RunMetadata`: format version, game build, commit, model database hash, seed, mode, ascension, players, timestamps, and loaded mods.
- `RunSnapshot`: the serialized game state plus its schema and encoding metadata.
- `RunRecordEntry`: sequence number, action kind, actor, run location, payload, and optional checkpoint.
- `RunLocation`: act, floor, map coordinate, room stack, combat, and turn context.
- A typed action hierarchy with an extension record for unknown or mod-defined actions.
- `StateCheckpoint`: digest algorithm, digest, context, and a small diagnostic state summary.
- `StateObservation`: a portable, normalized projection of authoritative real-game state that can be compared field by field with simulator state.
- `Divergence`: sequence number, context, expected state, simulated state, and structured differences.
- `RunResult`: victory, defeat, abandonment, incomplete, or replay divergence.

The core library must not reference STS2 or Godot assemblies. Game types will be converted to stable STS2PGN types at the mod boundary. A simulator should be able to parse the trace, enumerate actions, and compare its state against observations using only the core model or the documented file format.

## Action Coverage

### Run lifecycle

- New run start and initial configuration.
- Save/resume markers.
- Act transition.
- Room entry and exit.
- Run completion, defeat, and abandonment.

### Navigation

- Map-node selection using act and map coordinates.
- Next-act progression.

### Combat

- Play card, including stable card instance, model ID, and target.
- Use potion, including slot, model ID, and target.
- Discard potion.
- End player turn.
- Undo end turn where supported.
- Mid-action player choices.

### Player selections

- Combat-card selection.
- Deck-card selection.
- Canonical or generated card selection.
- Index and bundle selection.
- Relic selection.
- Player selection.
- Confirm and cancel operations.

### Events and special rooms

- Event option selection.
- Dialogue advancement.
- Special-event controls, initially including Crystal Sphere interactions identified by the reference MCP mod.
- Nested room transitions caused by event choices.

### Rest sites

- Rest-site option selection.
- Any follow-up card, relic, or bundle choice.

### Shops

- Card, relic, and potion purchases.
- Card-removal purchase and selected card.
- Shop exit.

### Rewards and treasure

- Claim or skip reward.
- Card reward choice or skip.
- Potion acquisition or discard decisions.
- Relic selection or skip.
- Treasure-room relic selection or skip.

### Flow control

- Proceed actions.
- Required confirmation and cancellation.
- Required modal or tutorial dismissal when it blocks deterministic progression.

The initial audit list will use the actions exposed by `../sts2mcp/McpMod.Actions.cs`. Each action will be mapped in `docs/action-coverage.md` to the authoritative game-layer command, synchronizer, or accepted state transition from the decompile. Recording should occur after the game accepts a decision, not merely when a UI control is clicked.

## Implementation Phases

### Phase 1: Scaffold and specification

1. Create the solution, core library, mod library, and test project.
2. Add the STS2 mod manifest and cross-platform build instructions based on `../sts2mcp`.
3. Add `.gitignore`, README, license, and example directories.
4. Write `docs/format.md`, `docs/replay-contract.md`, and the initial action-coverage ledger.
5. Keep `decomp/` read-only and exclude it from builds and source packages.

Deliverable: a documented project skeleton with a stable version-one format proposal.

### Phase 2: Core model and codec

1. Implement immutable or append-oriented record types.
2. Implement header and action serialization.
3. Implement a streaming parser that reports line and sequence errors clearly.
4. Preserve unknown headers, payload fields, and action kinds.
5. Validate required headers, unique sequence numbers, result placement, and payload shape.
6. Recover every complete record when the last line was truncated by a crash.
7. Add canonical serialization for reproducible hashes and fixtures.

Deliverable: a game-independent library that can create, parse, validate, and round-trip `.sts2pgn` files.

### Phase 3: Recorder mod

1. Initialize through `ModInitializer` and install narrowly scoped Harmony patches where public events are insufficient.
2. Subscribe to `RunManager` lifecycle events for run, room, and act transitions.
3. Capture synchronized combat actions from the action queue rather than UI input.
4. Capture `PlayerChoiceSynchronizer.PlayerChoiceReceived` for deterministic mid-action choices.
5. Capture map, event, rest, reward, treasure, shop, and one-off synchronizer decisions.
   - Treat `CRYSTAL_SPHERE` as an explicit nondeterministic special case: record its generated board, item layout, tool changes, logical cell clicks, remaining divinations, and proceed action.
   - For an asynchronous operation that opens a nested player choice, emit its initiating action before the nested `choice.*` records. Emit a separate outcome record after completion only when the outcome adds replay-relevant information. A simulator must be able to consume records strictly in sequence without looking ahead.
   - Cover this ordering explicitly for card rewards, merchant card removal, rest-site upgrades, event follow-up selections, and any other action that suspends for `PlayerChoiceSynchronizer`.
6. Generate an initial snapshot through `RunManager.ToSave()` and STS2's save serializer.
7. Record the game version, commit, model database hash, and loaded mod manifests.
8. Write to a profile-scoped replay directory with collision-resistant filenames.
9. Flush after every accepted decision so a crash leaves a valid replay prefix.
   - Every flushed prefix must parse without trimming or repair. Write exactly one record-ending newline and never append a blank record line.
10. Normalize action payloads at the mod boundary: use symbolic enum names, canonical string model IDs, stable player actors, reward-set/reward indexes, and card or offered-choice fingerprints rather than raw STS2 object serialization.
    - Fingerprint combat targets with both combat ID and model ID.
    - Identify merchant entries by both flattened inventory index and category-local index, with type/model/cost fingerprints.
    - Keep merchant card removal on its specialized initiating-action/choice/resolve path and exclude it from generic purchase records.
    - Give every generated card-reward alternative (including vanilla relic alternatives) an explicit index in the same index space as offered cards so every selected index is directly resolvable.
11. Finalize the result and headers when the run ends.

Deliverable: complete `.sts2pgn` recording for supported single-player runs.

### Phase 4: Authoritative observations and determinism validation

**Implementation complete in recorder 0.6.0.** The remaining seeded full-run smoke validation is tracked under Phase 6 and requires the matching STS2 build.

Implementation order for the current Phase 4 gate:

1. Add portable generated-act and generated-map topology, including stable coordinates, room types, connections, boss/start nodes, and current act identity.
2. Add current room/room stack and currently offered event, rest, reward, treasure, shop, and synchronized player choices.
3. Add live combat projection: phase/round/turn, player piles and energy, stable combat-card IDs, creatures, HP/block/powers/orbs/intents, and relevant queue/choice state.
4. Emit full observations after map generation, room entry/exit, rewards, act transitions, combat turn boundaries, and run end.
5. Emit action-correlated observations only after the action executor and any nested player choice have reached a settled boundary.
6. Validate a new full run for contiguous `combat.end_turn` decisions and observation coverage before resuming the in-game Phase 5 replayer.

1. Define a canonical state projection that excludes timestamps, animation state, localized text, and other cosmetic values.
2. Include gameplay state such as player resources, deck and card mutations, relics, potions, RNG counters, map history, room identity, available choices, combat piles, creatures, powers, orbs, intents, action queues, and turn phase as appropriate.
3. Capture an observation only after the real game has settled the recorded action and completed its deterministic automatic effects.
4. Generate lightweight digests after settled actions and full structured observations at run start, room boundaries, combat turn boundaries, rewards, and run end.
5. Give every observation the sequence number of the action it follows so a simulator can locate the first mismatch precisely.
6. Store normalized values and stable identifiers rather than localized text or UI indexes wherever possible.
7. Define field-level comparison rules, including ordered versus unordered collections and fields that may legitimately be ignored.
8. Ensure identical logical state produces identical observations and digests across serialization runs.

Deliverable: a portable authoritative-state contract that lets a simulator detect and explain divergence from the real game.

### Phase 5: Replay and simulator-consumer contract

1. **Implemented:** define a game-independent action iterator and observation-comparison API for simulator integrations.
2. **Implemented:** document how a simulator reconstructs initial state, applies an action, waits for its own state to settle, and compares the resulting state.
3. **Implemented:** provide structured first-divergence output containing the failing sequence, preceding action, expected observation, simulated observation, digests, context, and field-level expected/actual differences.
4. Add a deliberate in-game replay entry point as a completeness-validation harness that cannot overwrite or submit a normal run accidentally.
5. Parse and validate the selected `.sts2pgn` file.
6. Check game version, game commit, model database hash, and gameplay-affecting mod compatibility before in-game loading.
7. Reconstruct the initial run from the embedded `SerializableRun` snapshot.
8. Feed combat actions through STS2's `INetAction` and action synchronization pathways where possible.
9. Execute run-level decisions through the same game-layer APIs used during normal play.
10. Wait for the expected room, screen, phase, or player-choice request before executing each record.
11. Compare checkpoints and stop immediately on divergence.
12. Mark in-game replay sessions so they do not create normal progress, history, achievements, metrics, or leaderboard submissions.

Deliverable: a documented simulator-consumer contract plus deterministic in-game playback for verifying trace completeness.

### Phase 6: Testing and acceptance

Add automated tests for:

- Header and action round trips.
- JSON escaping and Unicode.
- Unknown headers and action kinds.
- Malformed and duplicate sequence numbers.
- Truncated final-line recovery.
- Strict parsing of live/incomplete files ending in exactly one newline, with regression coverage preventing extra trailing blank lines.
- Required-header and result validation.
- Stable canonical state hashing.
- Portable state-observation serialization and field-level comparison.
- Identification of the first divergent action from a deliberately altered simulated state.
- Serialization of every supported action type.
- Initiating-action → nested-choice → outcome ordering for rewards, merchant removal, rest-site choices, and event follow-up selections.
- Explicit actor attribution for every player decision, including event and proceed actions.
- Stable symbolic enum/model serialization with no reliance on internal numeric enum values or default object-shaped `ModelId` JSON.
- Offered-choice and selected-card fingerprints, including deck indexes plus model and mutation data.
- Exact combat-target fingerprints, shop inventory/category indexes, and complete card-reward alternative index mappings.
- Format-version compatibility behavior.

Perform seeded integration runs covering:

- Multiple combat turns and targeted cards.
- Combat and non-combat potion use.
- Mid-combat card choices.
- Map navigation.
- Events and nested event combat.
- Rest sites and card selections.
- Shops and card removal.
- Rewards and skipped rewards.
- Treasure relic selection.
- Save and resume.
- Victory, defeat, and abandonment.
- Rejection of traces containing gameplay-affecting mods.

The main acceptance criterion is a record-to-replay cycle that reaches every checkpoint with the same canonical state digest. A second fixture will intentionally alter one result and must identify the correct first divergent action and state fields.

## Design Principles

- Record authoritative accepted decisions, not raw input events.
- Prefer stable semantic identifiers while retaining instance identifiers needed to disambiguate duplicates.
- Store enough initial state to replay custom and unlocked vanilla runs without depending on the current profile.
- Record loaded mod compatibility metadata and reject gameplay-affecting mods unless a future simulator explicitly implements them.
- Treat real-game observations as the authoritative oracle against which simulator behavior is measured.
- Keep action and observation schemas portable so simulator code never needs to reference STS2 or Godot assemblies.
- Detect incompatibility instead of silently attempting an unreliable replay.
- Make partial records useful after crashes.
- Keep the core format independent of STS2 assemblies and UI implementation details.
- Keep the first version extensible for multiplayer and mod-defined actions.

## Risks and Mitigations

### Patch and mod compatibility

Exact deterministic playback cannot be promised across gameplay-changing STS2 patches. Record the build, commit, model hash, and mod manifests; reject gameplay-affecting mods in the initial release and fail clearly on incompatible game builds.

### Incomplete action interception

STS2 uses several synchronization mechanisms outside combat. Maintain an action-coverage ledger and test each room/action family. Prefer authoritative synchronizer methods and state transitions over individual UI node patches.

### Nondeterministic state

Use the same RNG state and action pathways as STS2's multiplayer/combat replay code. Exclude known cosmetic RNG from checkpoints and investigate every gameplay-state mismatch rather than weakening the digest broadly.

### Game updates

Keep Harmony patches small and isolated. Document the decompiled type and method used by every integration point so each new game patch can be audited systematically.

### Replay side effects

Replay mode must disable normal saving, progress updates, achievements, metrics, and leaderboard submission before executing the first action.

The current STS2 `RunManager.SetUpReplay` cannot be called unguarded: it hardcodes
`shouldSave: true` and carries the source run's `DailyTime` into the replay. The validation
harness must activate a replay-session flag before setup and patch the shared setup inputs
to `shouldSave: false` and `dailyTime: null`; end-of-run side-effect entry points should
also reject validation sessions as defense in depth.

## Current Environment Constraints

- The .NET 9 SDK is available for core builds and tests.
- The STS2 game assemblies are not currently available in this environment. Full mod compilation requires `STS2_GAME_DIR` or `STS2GameDataDir` pointing to a matching game installation.
- The repository has normal Git metadata and implementation work should continue to be committed in focused changes.
- `decomp/` is a read-only link to the current game decompile and is an information source only; it must never be edited or included as project source.

## Completion Criteria

The initial project is complete when:

1. A supported single-player run produces a valid, readable `.sts2pgn` file.
2. Every decision needed to advance the run is represented by a documented action record.
3. The record embeds sufficient initial state and compatibility metadata to reconstruct the run independently of the active profile.
4. Replay uses authoritative game APIs and reaches matching checkpoints.
5. Divergence and compatibility failures produce actionable diagnostics.
6. Parser, serializer, recovery, validation, and action-model tests pass.
7. The format and replay guarantees are documented well enough for another implementation to consume the file without reading the mod source.
8. An external simulator can reconstruct the initial state, replay the ordered actions, compare portable observations, and identify the first divergence from the real game.
