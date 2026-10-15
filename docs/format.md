# STS2PGN Format Versions 1 and 2

Status: **recorder 0.11.0 implements canonical reward departure, version-two lossless observation deltas, state projection version 1, and run-start profile inputs**.

New recordings use `FormatVersion "2"`. Readers also support version 1, which
always carries full observations. Version 2 changes only how observations are
stored: all decisions, observation boundaries, reconstructed states, and comparison
semantics remain the same. Consumers must explicitly support version 2 before
reading it; treating an unknown `state.delta` as an action would be incorrect.

## Purpose

STS2PGN is a portable, append-friendly record of a Slay the Spire 2 run. It serves two related purposes:

1. reproduce the run's semantic decisions; and
2. compare an external simulator's state with authoritative observations captured from the real game.

The format borrows PGN's tag-pair header and ordered movetext structure. JSON payloads are used because STS2 actions and state are more varied than chess moves.

## Encoding and file layout

- Files use UTF-8 without a byte-order mark.
- Readers accept LF and CRLF line endings.
- Writers emit LF line endings.
- The conventional extension is `.sts2pgn`.
- Completed recordings are stored as `.sts2pgn.gz`: a standard gzip wrapper around
  the exact UTF-8 trace. Decompression yields the same version-one or version-two document; neither
  `FormatVersion`, `projection_version`, nor observation digests change. Readers must
  decompress before parsing text. The portable `Sts2PgnFile.OpenText` helper detects
  gzip by its magic bytes and opens both forms.
- A file contains a tag section, one blank line, a record section, and a terminal result line.
- Blank lines are not allowed inside the record section in version 1.

```text
[TagName "escaped value"]
[AnotherTag "value"]

1 action.kind {"actor":"p0","data":{}}
2 state.observe {"after":1,"context":"action.settled","digest":"sha256:...","projection_version":1,"state":{}}
*
```

The action stream is append-only while recording. A clean finalization may atomically change the `Result` header from `*` to its final value; the terminal result line is authoritative if a header and terminal disagree.

The recorder automatically compresses after finalizing a terminal run. It flushes
the completed gzip file to disk and verifies the decompressed bytes against the
finalized source using SHA-256 before publishing the `.gz` file and deleting the
plain source. Compression/publication failure retains the plain trace and is logged;
existing destinations are not overwritten. Active and incomplete recordings stay
plain so save/resume and truncated-line recovery work unchanged. A crash between
publication and deletion can leave both complete copies; temporary compression files
are not playback inputs. Compression is a file transport concern, not a change to
the record grammar or comparison semantics.

## Search metadata sidecars

After publishing a completed recording, the recorder writes
`<filename>.meta.json`, for example `run.sts2pgn.gz.meta.json`. This is an optional,
rebuildable search index, not part of the trace or a prerequisite for playback.
Indexing failure is logged without invalidating the completed archive.

Schema version 1 is a UTF-8 JSON object with these fields:

| Field | JSON type and meaning |
|---|---|
| `schema_version` | Integer, currently `1`; independent of the trace format version. |
| `file` | String, adjacent trace's basename; no absolute machine path. |
| `file_size_bytes` | Integer, size of the indexed file. |
| `file_sha256` | `sha256:` plus lowercase hexadecimal hash of the exact indexed file bytes; for gzip, this hashes the compressed archive. |
| `profile` | String, resolved `recording` or `script`, including legacy inference. |
| `format_version` | Integer, trace `FormatVersion`. |
| `seed` | String, exact `Seed` value, preserving leading zeros. |
| `run_id`, `mode` | Corresponding header strings, or null if absent. |
| `ascension` | Integer from the `Ascension` header. |
| `players` | Parsed `Players` array, including character identities. |
| `game_version`, `game_commit`, `model_id_hash` | Corresponding header strings, or null if absent. |
| `producer`, `recorder_version`, `started_at` | Corresponding header strings, or null if absent. |
| `result` | Authoritative terminal result token, even if the header is stale. |

`sts2pgn index <file>...` backfills metadata for plain or gzip traces, including
scripts. A terminal token is required (`*` is accepted); active recordings without
one are refused. The command scans to the terminal result without reconstructing
observation deltas, then hashes the source bytes. It does not perform full trace
validation; use `validate` separately when needed.

Sidecars are durably written to a temporary file beside the destination and
atomically published. Existing sidecars are preserved unless the CLI's explicit
`--overwrite` option is supplied. The trace is never modified by indexing.
Consumers must tolerate missing sidecars and treat a hash mismatch as stale
metadata. Renaming, editing, or recompressing a trace requires regenerating its
sidecar. Do not use the seed as a unique run identity: many runs may share it.

## Tag pairs

A tag line has this shape:

```text
[Key "value"]
```

Keys match `[A-Za-z][A-Za-z0-9_]*`. Within values, `\"`, `\\`, `\n`, `\r`, and `\t` use JSON-style escaping. Unknown tags must be retained when a reader writes the record again.

### Required tags

| Tag | Meaning |
|---|---|
| `Format` | Always `STS2PGN`. |
| `FormatVersion` | Major format version: `1` for full observations, `2` for full observations plus lossless deltas. |
| `RecorderVersion` | Version of the producing mod. |
| `RunId` | Unique opaque identifier assigned by the recorder. |
| `GameVersion` | Human-readable STS2 release version. |
| `GameCommit` | STS2 build commit, or `UNKNOWN`. |
| `ModelIdHash` | Model database hash used by STS2 multiplayer compatibility checks. |
| `Seed` | Canonical run seed. |
| `Mode` | `STANDARD`, `DAILY`, or `CUSTOM`. |
| `Ascension` | Decimal ascension level. |
| `Players` | Compact JSON array of stable player slots and character model IDs. |
| `StartedAt` | UTC ISO-8601 timestamp for provenance only. |
| `Result` | `VICTORY`, `DEFEAT`, `ABANDONED`, `DIVERGED`, or `*`. |

### Recommended tags

| Tag | Meaning |
|---|---|
| `Profile` | `recording` or `script`. New writers emit this tag; readers accept its absence on older files. See profile detection below. |
| `Platform` | Platform identifier reported by the game. |
| `GameStartTime` | STS2's own run key: `SerializableRun.StartTime` unix seconds, stable across save/resume. Used to find the incomplete trace of a resumed run. |
| `GameSaveSchema` | Initial `SerializableRun` schema version. |
| `Mods` | Compact JSON array of loaded mod IDs, versions, and gameplay flags. |
| `DailyTime` | Daily-run date/time when applicable. |
| `ParentRunId` | Source run when a trace is derived or minimized. |

Timestamps and platform metadata never participate in deterministic state comparison.

## Record grammar

Each record occupies exactly one line:

```text
sequence SP kind SP payload
```

- `sequence` is a positive decimal integer. It starts at 1 and increases by one for every record, including observations.
- `kind` is a lowercase dotted identifier matching `[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+`.
- `payload` is one compact JSON object. It must not be `null`, an array, or a scalar.
- Unknown record kinds and unknown payload properties must survive a read/write cycle.

The version-two `state.delta` transport envelope is closed, as specified below.
Unknown properties inside its `observation` envelope and reconstructed `state` are
preserved just like properties of a full observation.

The final non-empty line contains one result token and has no sequence number. A prefix that ends after a complete record but has no terminal token is treated as incomplete with result `*`.

### Gameplay victory semantics

Starting with RecorderVersion 0.14.0, `VICTORY` denotes completion of combat
against the last required boss, after combat-end effects settle. Double Boss
requires the second boss. `run.finish` and its final `run.end` observation are
written there; the Architect and subsequent presentation are not recorded.
Earlier recordings use the later game UI boundary and may contain a
post-victory tail. Consumers that exclude that tail must still verify final
combat and expose the exclusion. Neither record syntax nor projection fields
change, so format and projection versions remain unchanged.

## Common action envelope

Action payloads use these common properties where applicable:

```json
{
  "actor": "p0",
  "location": {
    "act": 0,
    "floor": 4,
    "map": { "row": 3, "col": 2 },
    "room_id": 0,
    "room_type": "MONSTER",
    "combat": 2,
    "round": 1,
    "turn": 1
  },
  "data": {},
  "extensions": {}
}
```

- `actor` is a stable player slot (`p0`, `p1`, and so on), not a platform account ID.
- `location` records where STS2 accepted the decision. Unavailable fields are omitted.
- `data` contains kind-specific fields.
- `extensions` contains optional producer- or mod-specific data whose semantics are not part of the base format.

## Identity rules

Localized names are descriptive only and cannot identify gameplay objects.

- Game content uses STS2 `ModelId` values in their stable `UPPER_SNAKE_CASE` representation.
- Players use their run slot, such as `p0`. Raw network/platform IDs may be retained only as non-authoritative metadata.
- Map points use act index plus row and column.
- Creatures in combat use STS2 combat IDs and include their model ID as a validation fingerprint.
- Targeted combat actions retain `data.target_id` for compatibility and also include
  `data.target = { "combat_id": N, "model_id": "..." }`. Targetless actions use `null`
  for both fields. A consumer must reject a mismatched ID or model fingerprint.
- Combat cards use the `NetCombatCard.CombatCardIndex` assigned by the game and include model ID and mutation data where available.
- Deck-card choices use the deck index at the time of choice plus a card fingerprint. A consumer must reject a mismatched fingerprint rather than silently select a different card.
- Rewards use reward-set ID and reward index plus the reward's semantic contents.
- Merchant purchases include the entry's index in `MerchantInventory.AllEntries`, its
  category (`character_card`, `colorless_card`, `relic`, or `potion`), and its index
  within that category. The type, model ID, and cost are validation fingerprints rather
  than substitutes for those indexes.
- Merchant card removal is represented only by `shop.remove_card`, its nested
  `choice.cards`, and `shop.remove_card.resolve`; it must not also emit a generic
  `shop.purchase` pair.
- UI indexes alone are never sufficient when a semantic identifier is available.

## Initial state

The first record must be `run.start`. Its `data` contains both:

- `source_snapshot`: the exact STS2 `SerializableRun` JSON and schema information, used by compatible in-game replay; and
- `state`: the portable normalized state projection, used by simulators.

Recorder 0.10.1 also writes optional `data.profile` generation inputs:

```json
{"discovered_acts":["ACT.OVERGROWTH","ACT.UNDERDOCKS"]}
```

The list is sorted by ordinal model ID. It snapshots the local progress profile
at `RunState.CreateForNewRun`, before setup and the run-start event, and is tied
to that run instance. The same progress profile controls whether act selection
rolls an alternate act or takes an undiscovered one outright. This metadata is
outside `data.state`, so it does not change projection version 1 or its digests.
An empty list is an explicit statement that no acts were discovered.

Older recordings omit `profile`. A new trace opened from a loaded save also
omits it when the original generation-time profile is unavailable; it must not
substitute the current progress profile. Continuing an existing trace preserves
its original `run.start`. Consumers must document their missing-profile policy;
`sts2sim` assumes all supported acts discovered. Recording-to-script conversion
preserves `profile` along with `state`.

The portable projection is authoritative for cross-implementation comparison. A simulator must not need to understand private STS2 serialization details to initialize a supported run. Projection version 1 defines the initial `run_start.core` and live `run.core` scopes below; the source snapshot is retained for same-build provenance and fallback tooling.

Secrets, platform account identifiers, and unnecessary profile history must not be included. Player identifiers are anonymized to run slots.

## Save and resume

A save-and-quit leaves an incomplete trace: `Result` header `*` and no terminal result
line. When the same run is continued, the recorder locates that trace — matching
`GameStartTime`, `Seed`, `Mode`, `Ascension`, `Players`, and the exact game
version/commit/model hash, with `Result` still `*` — and appends to it instead of
starting a new file:

- If the previous session crashed mid-write, the recorder first removes the truncated
  unterminated final line, restoring the durable prefix that ends in exactly one
  newline. Complete records are never removed.
- Sequence numbering continues from the last complete record.
- The first appended record is `run.resume`, immediately followed by a `state.observe`
  with context `run.resume`.

`run.resume` is a re-anchoring boundary, not a player action. STS2 saves no mid-room
state, so resuming rewinds the run to its last checkpoint: decisions recorded after that
checkpoint remain in the trace but never happened in the resumed timeline. A consumer
must discard its simulated state at `run.resume` and re-initialize from the record's
payload, exactly as it initializes from `run.start`:

```json
{
  "data": {
    "reloads": 1,
    "recorder_version": "0.8.0",
    "restored_pre_finished_room": false,
    "source_snapshot": { "schema_version": 16, "encoding": "sts2-save-json", "data": {} },
    "state": { "scope": "run.core" }
  }
}
```

- `source_snapshot` is the exact `SerializableRun` the game restored, for in-game replay.
- `state` is the portable live projection (scope `run.core`) after the game finished
  restoring; the following observation duplicates it with its canonical digest, mirroring
  the `run.start` pattern.
- `reloads` counts the run's resumes including this one; `restored_pre_finished_room`
  records whether the checkpoint carried a pre-finished room with unclaimed rewards.
- The per-resume `recorder_version` identifies the writer of that resume, since a
  trace can continue under a different recorder version than its original header.
- Resumes written by recorder 0.17.x also carry
  `combat_runtime.ending_player_turn_phase_two`, the game's turn-handover flag at
  load. No gameplay reads that flag, so readers ignore the field; later recorders
  do not write it.

When no matching incomplete trace can be found or safely continued (missing file,
malformed record, changed game build or model hash), the recorder falls back to a new
trace file: a normal `run.start` built from the restored state, followed by the same
`run.resume` record marking the run as continued.

## Decision-script profile

A *decision script* is a `.sts2pgn` file written by a simulator (or by hand) that
proposes a run: it says which decisions to make but carries no authoritative state. A
playback driver executes it in the real game while the normal recorder writes an
ordinary recording; that recording — never the script — is the conformance artifact.
The profile adds no grammar and no comparison semantics; `FormatVersion` and
`projection_version` are unchanged.

### Profile detection

New writers emit `[Profile "recording"]` or `[Profile "script"]` in both format
versions. A recording contains decisions and observed state; a script proposes
decisions. This additive tag does not change `FormatVersion`, record grammar, or
state comparison semantics. It is separate from the game-generation inputs in
`run.start.data.profile`.

Readers use `Profile` when present. Its values are case-sensitive; unknown values,
duplicates, and conflicts with producer identity are errors. An explicit profile
does not waive any of that profile's required or forbidden tags. For older files
without `Profile`, `Producer` selects the script profile and otherwise the recording
profile applies. Producer identity remains constrained as follows:

| Tag | Recording | Script |
|---|---|---|
| `RecorderVersion` | required | forbidden (`script_carries_recorder_version`) |
| `Producer` | forbidden (`recording_carries_producer`) | required, `name/version` with a lowercase `[a-z0-9_-]` name, for example `alphaspire/0.3.1` |

No file carries both. A file with `Producer` is a proposal and is never an
authoritative fixture.

Writers add the inferred profile when rewriting legacy files. Recording-to-script
conversion replaces the source profile with `script`. Continuing an existing
append-only recording leaves its header intact. CLI `validate` detects the profile;
`--script` explicitly requires a script. The Core validator's parameterless overload
continues to require a recording so conformance callers do not accidentally accept
scripts.

Required script tags: `Format`, `FormatVersion`, `Producer`, `Seed`, `Mode`,
`Ascension`, `Players`, `GameVersion`, `GameCommit`, `ModelIdHash`. The driver compares
the compatibility identity (`GameVersion`, `GameCommit`, `ModelIdHash`) with the live
build and refuses a mismatch before starting anything. `Mode` must be `CUSTOM`
(`script_mode_not_custom`): the custom run is the only workflow in which the game accepts
a chosen seed. `RunId`, `StartedAt`, and `Result` are optional; when `Result` is present
it must be `*`, and the terminal token must be `*` (`script_result_not_incomplete`).

Records:

- The first record is `run.start`. Its `data.state` must carry `acts[]` (`index`,
  `model_id`; the acts the seed is expected to generate on this profile — the custom-run
  lobby may override act 1, so the driver honors the script's first act when it names a
  known act and requires every later act to match the seed) and
  `players[0].unlocks` (`epochs`, `encounters_seen`, `number_of_runs`). The driver
  verifies both against the live profile and refuses to start on a mismatch
  (`script_missing_acts`, `script_missing_unlocks`, `script_invalid_unlocks`). Unlocks are
  compared for generation-equivalence, not byte-equality: `epochs` exactly,
  `number_of_runs` as first-run-or-not, and `encounters_seen` only through the seen-status
  of the twelve discovery bosses (`ScriptUnlocks.DiscoveryBosses`); other differences are
  logged as warnings.
  `source_snapshot`, the full projection, and the `run.start` observation are not
  required.
- Then come decision records in file order, using the existing record kinds and the
  same semantic identity a recording carries: model IDs, `{combat_id, model_id}`
  targets, and every positional index accompanied by its fingerprint
  (`script_missing_identity`). Drivable kinds are `act.next`, `choice.cards`,
  `choice.index`, `combat.discard_potion`, `combat.end_turn`, `combat.play_card`,
  `combat.use_potion`, `event.advance`, `event.choose`, `event.crystal_sphere.click`,
  `event.crystal_sphere.proceed`, `event.crystal_sphere.set_tool`, `map.choose`,
  `rest.choose`, `reward.claim`, `reward.skip`, `room.proceed`, `shop.purchase`,
  `shop.remove_card`, `treasure.choose_relic`, and `treasure.skip_relic`. Crystal
  Sphere clicks carry their complete deterministic identity as
  `{x, y, tool, remaining_before}`; a driver selects the carried tool before clicking,
  so producers may omit redundant `set_tool` records.
- `choice.*` records must carry the offered options the producer expects (`offered` with
  `context` and `options` on `choice.index`, `script_choice_missing_offer`; a
  fingerprint plus `deck_index`/`combat_card_index` on every `choice.cards` entry,
  `script_choice_missing_fingerprint`). The game-assigned `choice_id` is optional; the
  driver answers whichever choice the game is currently asking. A `choice.*` record must
  follow an operation that can suspend for it (`combat.play_card`, `combat.use_potion`,
  `combat.end_turn` — the end-of-turn flush can ask which cards to keep — `event.choose`,
  `reward.claim`, `rest.choose`, `shop.purchase`, `shop.remove_card`,
  `treasure.choose_relic`) or another `choice.*` (`script_orphan_choice`). A potion
  use or discard carrying `in_combat: false` may stand between them: outside combat
  the belt is used beside a waiting selection without answering it.
- Potion decisions may carry an optional boolean `in_combat`. The driver derives the
  submission context from the live game and treats this field as a precondition: when it
  is present and disagrees with the live state, playback stops as a mismatch. Producers
  should emit it when they know the intended context; older scripts may omit it.
- `room.proceed` finalizes terminal combat rewards or treasure selection immediately before
  `map.choose`. The two decisions must be adjacent when they represent map departure;
  opening or closing the map is not recorded. Proceeds that resume a parent event or finish
  a rewardless combat remain immediate standalone decisions. Rest/shop/finished-event map
  opening adds no Proceed. Act progression remains `act.next`.
- `event.advance` is one record per dialogue page, as the recorder emits it.
- Outcome records are allowed but ignored with a warning (`script_ignored_record`):
  `state.observe`, `*.resolve`, `act.enter`, `room.enter`, `room.exit`, and
  `event.crystal_sphere.start`. The generated board is an observation, not simulator input.
- `run.resume` is rejected as `script_resume_not_supported`. Playback starts a fresh
  seeded Custom run and cannot restore the saved checkpoint or select only the
  authoritative side of an abandoned/resumed branch.
- Other kinds without drivable identity are rejected (`script_unsupported_record`):
  `run.finish`, `choice.player`, `choice.unknown`, `game.unknown_action`, and
  `combat.undo_end_turn`.
- `set_id` on reward records is the run's deterministic per-run counter and must match
  the live game; the driver never remaps indexes, set IDs, or options.

All shared grammar checks still apply in script mode (sequence numbering, actor slots,
combat-target consistency, shop identity, card-reward offer indexes). Observation,
digest, and projection checks apply only to observations that are actually present.

`Sts2PgnValidator.Validate(record, Sts2PgnValidationMode.Script)` validates the
profile; `sts2pgn validate --script` does the same from the command line. A minimal
example is `examples/script-minimal.sts2pgn`. `ScriptConverter.FromRecording` (CLI:
`sts2pgn to-script <recording> --producer name/version`) turns a recording into a script
that replays the same decisions: it drops observations, resolutions, lifecycle records,
`run.finish`, the raw save snapshot, and provenance tags, replaces `RecorderVersion` with
`Producer`, and sets `Mode` to `CUSTOM`. It refuses a recording containing `run.resume`
rather than concatenating decisions from the abandoned and resumed timelines.

## Action namespaces

Version 1 reserves these namespaces:

| Namespace | Examples |
|---|---|
| `run` | `run.start`, `run.resume`, `run.finish` |
| `act` | `act.enter`, `act.next` |
| `room` | `room.enter`, `room.exit`, `room.proceed` |
| `map` | `map.choose` |
| `combat` | `combat.play_card`, `combat.use_potion`, `combat.discard_potion`, `combat.end_turn`, `combat.undo_end_turn` |
| `choice` | `choice.cards`, `choice.player`, `choice.index` (with an `offered` context for index-based card, bundle, relic, and reward selections) |
| `event` | `event.choose`, `event.advance`, `event.special` |
| `rest` | `rest.choose`, `rest.resolve` |
| `shop` | `shop.purchase`, `shop.purchase.resolve`, `shop.remove_card`, `shop.remove_card.resolve` |
| `reward` | `reward.claim`, `reward.resolve`, `reward.skip` |
| `treasure` | `treasure.choose_relic`, `treasure.skip_relic` |
| `state` | `state.observe` |

Automatic game effects are represented in the following observation, not as player actions. Later versions may add optional explanatory effect records, but simulators must not require them to replay version 1.

Operations that suspend for a nested synchronized choice use a strict three-part order:

```text
N   reward.claim     {"data":{"set_id":0,"reward_index":2,...}}
N+1 choice.index     {"data":{"choice_id":4,"indexes":[1],"offered":{...}}}
N+2 reward.resolve   {"data":{"action_sequence":N,"success":true}}
```

The initiating record is the action a simulator applies. Nested `choice.*` records answer choices opened while that action is suspended. A `*.resolve` record validates the eventual outcome and must reference its earlier initiating record through `data.action_sequence`; it does not apply the action a second time. The same rule covers rest choices, merchant card removal, and ordinary shop purchases that trigger follow-up choices.

Card rewards use one combined choice-index space. Cards occupy indexes `0` through
`cards.length - 1`; alternatives follow in the exact order presented by STS2. Each
alternative is recorded as `{ "index": N, "option_id": "skip" }` (or another stable
option ID such as `reroll` or `sacrifice`) so every value in `data.indexes` maps to one
explicit offered object.

## State observations

A `state.observe` record describes authoritative state after the real game has settled one or more actions:

```json
{
  "after": 17,
  "context": "combat.action_settled",
  "digest": "sha256:0123...",
  "projection_version": 1,
  "state": {}
}
```

- `after` refers to the action sequence whose settled result is being observed.
- `context` identifies the comparison boundary.
- `digest` hashes the canonical JSON encoding of `state`.
- `projection_version` versions the state schema independently of the file grammar.
- `state` is the complete projection for its declared scope.

Recorder 0.6.0 and later capture complete observations after settled actions and at run start, run resume, map generation, room/act boundaries, combat turn boundaries, choice/reward/rest/shop boundaries, and run end. Version-two writers may store these as lossless deltas; readers reconstruct the full observations before validation or replay.

The core library validates the observation envelope (`after`, `context`, `digest`, `projection_version`, and object-valued `state`) and verifies that the digest matches the canonical state. For `context = run.start`, it also requires the observation to reference `run.start`, match `run.start.data.state`, and conform to the `run_start.core` schema. For `context = run.resume`, it requires the observation to immediately follow and reference a `run.resume` record, match `run.resume.data.state`, and use scope `run.core`.

### Version 2: lossless observation deltas

A full `state.observe` is still a complete checkpoint. Between checkpoints a writer
may replace an observation with one `state.delta` at the **same sequence number**:

```text
18 state.delta {"base":16,"observation":{"after":17,"context":"action.settled","digest":"sha256:...","projection_version":1},"patch":[["set",["run","floor"],4],["remove",["run","old_field"]]]}
```

The payload has exactly three properties:

- `base`: the sequence of the immediately preceding recorded observation, whether
  full or delta. Actions between the observations do not change this reference.
- `observation`: the original observation payload with only `state` removed. It
  retains `after`, `context`, `digest`, `projection_version`, and all unknown
  observation properties. It must not contain a `state` property.
- `patch`: an ordered array of operations against the previous **recorded** state.
  `[]` means an identical state at a new comparison boundary; it does not remove
  the observation or its context.

Each operation is either `["set", path, value]` or `["remove", path]`. A path is a
non-empty array of literal object-property names, not a JSON Pointer: `""`, `"/"`,
and `"~"` need no special escaping beyond normal JSON string encoding. Every
intermediate path component must already identify an object. `set` adds or replaces
the last property; `remove` requires that property to exist. Setting a property to
`null` is distinct from removing it. Arrays are replaced whole, retaining their
order and exact values; patches cannot address array elements. Root replacement,
other operation names, extra operation arguments, and unknown transport-envelope
properties are invalid.

A decoder clones the prior recorded state, applies all operations in order, inserts
the result as `observation.state`, and verifies the original canonical SHA-256 digest
over that **complete** state. It then exposes a normal `state.observe` record with
the original sequence. No values may come from the simulator. Replay compares the
entire reconstructed state, including fields absent from the patch. Deltas must not
round numbers, omit defaults, or change the state projection or digest algorithm.
Missing/mismatched bases, invalid paths, and digest failures are hard errors even
when the invalid record is the unterminated final line.

Writers emit full checkpoints for the first observation, after `run.start`,
`run.resume`, `room.enter`, or `act.enter`, and for observations whose context names
one of those boundaries. Decoders clear the old baseline at each such lifecycle
record and require the next observation to be full. In particular, a resume's
checkpoint reflects the restored state: nothing may be inherited from the abandoned
timeline. Prior records remain in the trace. `run.start.data.state`,
`run.resume.data.state`, and source snapshots remain complete.

The bundled writer permits at most 64 consecutive deltas before another full
checkpoint, and uses a full observation whenever its encoding is no larger than
the delta. This interval is a writer policy; decoders may accept longer valid
chains from other producers. Every observation boundary is retained. A reopened
writer starts a fresh chain and writes its next observation in full. Continuing a
version-one recording retains its original version and full-observation encoding.

`Sts2PgnParser` expands and verifies version-two observations before returning a
`RunRecord`. The validator, replay engine, script converter, and playback driver
operate on this decoded model. `Sts2PgnWriter` and `Sts2PgnStreamWriter` encode
deltas automatically for version-two metadata; version-one output stays full.
`Sts2PgnWriterOptions.EncodeStateDeltas = false` writes full observations while
retaining the file's declared version. See `examples/delta-minimal.sts2pgn` for a
complete version-two example with an independently specified patch and digest.

### Projection version 1: `run_start.core`

Recorder version 0.6.0 writes an unambiguous initial settled-state checkpoint. `run.start`
embeds the portable state in `data.state`; the immediately following `state.observe`
duplicates that state, references sequence 1, and supplies its canonical digest.

```json
{
  "scope": "run_start.core",
  "run": {
    "seed": "ABC123",
    "mode": "standard",
    "ascension": 0,
    "act": 0,
    "floor": 0,
    "reloads": 0,
    "visited_map": [],
    "modifiers": [],
    "rng": { "seed": "ABC123", "counters": {} },
    "odds": {},
    "shared_relic_grab_bag": {},
    "extra": {}
  },
  "acts": [
    {
      "index": 0,
      "model_id": "ACT.OVERGROWTH",
      "rooms": {},
      "saved_map": null
    }
  ],
  "players": [
    {
      "slot": "p0",
      "character_model": "CHARACTER.IRONCLAD",
      "current_hp": 80,
      "max_hp": 80,
      "max_energy": 3,
      "gold": 99,
      "base_orb_slots": 0,
      "max_potion_slots": 3,
      "deck": [],
      "relics": [],
      "potion_slots": [],
      "rng": { "seed": 0, "counters": {} },
      "odds": {},
      "relic_grab_bag": {},
      "unlocks": { "epochs": [], "encounters_seen": [], "number_of_runs": 0 },
      "extra": {}
    }
  ],
  "rooms": [],
  "choices": {
    "player_choices": [],
    "rewards": [],
    "events": [],
    "rest": null,
    "treasure": null,
    "merchant": null
  },
  "special_event": null,
  "combat": null
}
```

Comparison rules for this scope are:

- `acts` is keyed by `index`; each act includes its generated encounter/event pools and optional saved map topology.
- `players` is keyed by `slot`, although its wire representation preserves run order.
- Decks, relics, potion slots, visited map coordinates, modifiers, and each relic grab-bag list are ordered.
- RNG counter objects are keyed by their symbolic counter name and compared exactly.
- `unlocks` is the profile state the run was created with: `epochs` (revealed epoch IDs) and `encounters_seen` (encounter model IDs) are sets serialized in ordinally sorted order; `number_of_runs` is the profile's completed-run count. Run generation reads this state in exactly three ways, and the playback driver compares only those: revealed epochs filter card/potion/relic pools and gate alternate acts and ancients (compared exactly); `number_of_runs` is read solely through `== 0` first-run clauses (compared as first-run-or-not); and `encounters_seen` is read only by `ActModel.ApplyDiscoveryOrderModifications`, which overrides the rolled boss with the first unseen entry of the act's `BossDiscoveryOrder` (compared only for those twelve bosses). Recordings still carry the exact values.
- Card, relic, enchantment, and modifier saved properties are name-sorted portable objects.
- Odds are exact STS2 single-precision values encoded as canonical JSON numbers.
- `rooms` and choice collections are explicitly empty and `combat` is `null` at a new-run boundary.
- Wall-clock values, platform identity, discovery/UI history, localization, and presentation state are excluded.

### Projection version 1: `run.core`

Every post-start observation emitted by the recorder uses `scope = "run.core"` and contains the same `run`, `acts`, and `players` foundation as `run_start.core`, plus live state:

- `run.current_act_model`, `total_floor`, `current_map_coord`, `visited_events`, and the current generated `map`;
- `rooms`, ordered by nesting depth;
- `choices.player_choices`, active reward sets, event options, rest options, treasure relics, and merchant inventories;
- `special_event`, normally `null`, carries the full Crystal Sphere board, tool, and divination state while that minigame is active;
- `combat`, or `null` outside a combat room; and
- for combat, encounter/round/side and transition flags, players keyed by slot, ordered piles with stable combat-card IDs, energy/stars/orbs, and creatures with HP, block, powers, moves, and intents.

The turn-handover transition flags (`ending_player_turn_phase_one`, `ending_player_turn_phase_two`) are projected as their *settled* values once a fight has been won: the game's own unwind always clears them behind the win check, and a raw read there would race the recorder's capture against those continuations (recorder 0.7.0 and earlier could record either value at `reward.offered`). A lost fight projects the flags as they stand, because its chain is cancelled and never clears them. Recorder 0.7.1 introduced this normalization; it removes capture-scheduling noise without changing the field's meaning or any comparison rule, so `projection_version` stays 1.

Arrays with semantic identity are compared by key: `acts/index`, `players/slot`, `rooms/stack_depth`, `choices.player_choices/id`, `choices.rewards/id`, `run.map.points/id`, `combat.players/slot`, and `combat.creatures/combat_id`. All other arrays preserve gameplay order.

Crystal Sphere's `event.crystal_sphere.click` observation captures the board after
cell clearing. On the final divination, reward generation can still be pending.
The matching `reward.offered` observation refreshes the minigame projection and
RNG counter after generation, including for an empty reward set. These are distinct
live checkpoints; the earlier snapshot is not replaced or relabelled as the payout.

Recorder contexts currently include `run.resume`, `map.generated`, `choice.offered`, `action.settled`, `room.enter`, `room.exit`, `act.enter`, `combat.player_turn.start`, `combat.enemy_turn.start`, `combat.turn.end`, event/Crystal Sphere contexts, reward/rest/shop settled contexts, and `run.end`. The `after` field always references an earlier decision or lifecycle record; when an action contains a nested synchronized choice, it references the latest nested decision.

## Canonical JSON and hashing

Canonical state JSON uses:

- UTF-8 encoding;
- lexicographically sorted object properties;
- no insignificant whitespace;
- invariant decimal formatting;
- arrays only where order has gameplay meaning; and
- explicitly sorted representations for sets and maps.

The digest is lowercase hexadecimal SHA-256 prefixed by `sha256:`. Fields documented as provenance or cosmetic state are excluded before canonicalization.

## Crash recovery

A recorder flushes after every accepted decision. If the final line is truncated, readers discard only that incomplete line and retain every preceding complete record. Missing terminal result means `*`.

A malformed complete line in the middle of a file is an error and must not be skipped silently.

## Compatibility

- A reader must reject unsupported major `FormatVersion` values.
- This implementation accepts versions 1 and 2. Version-one-only consumers need a
  decoder update for version-two recordings; gzip decompression alone is not enough.
- Unknown tags, record kinds, and payload fields are preserved for forward compatibility.
- Exact game replay requires compatible game version, model hash, and gameplay-affecting mods.
- Simulator replay may proceed across builds only when the consumer deliberately supports their semantics; compatibility mismatches remain visible in the report.
- Changes to comparison semantics increment `projection_version` even if the text grammar does not change.

## Phase 2 implementation notes

The portable implementation lives in `src/STS2PGN.Core/` and provides:

- immutable `RunRecord`, `RunMetadata`, and `RunRecordEntry` values;
- `RunRecordBuilder` for append-oriented producers;
- `Sts2PgnParser` overloads for strings and incremental `TextReader` input;
- `Sts2PgnWriter` with deterministic LF output and canonical JSON payloads;
- `Sts2PgnValidator` with structured error/warning codes;
- opt-out recovery of a likely truncated, unterminated final JSON line; and
- `CanonicalJson` serialization and `sha256:` digest generation.

Unknown tags, record kinds, and JSON payload properties are represented without registration and survive parse/write cycles. Parser recovery never skips a malformed newline-terminated record or a malformed record in the middle of a file.
