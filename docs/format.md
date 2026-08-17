# STS2PGN Format Version 1

Status: **text grammar implemented in Phase 2; portable state projection not yet stable**.

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
- A file contains a tag section, one blank line, a record section, and a terminal result line.
- Blank lines are not allowed inside the record section in version 1.

```text
[TagName "escaped value"]
[AnotherTag "value"]

1 action.kind {"actor":"p0","data":{}}
2 state.observe {"after":1,"context":"action.settled","digest":"sha256:...","state":{}}
*
```

The action stream is append-only while recording. A clean finalization may atomically change the `Result` header from `*` to its final value; the terminal result line is authoritative if a header and terminal disagree.

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
| `FormatVersion` | Major format version; `1` for this document. |
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
| `Platform` | Platform identifier reported by the game. |
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

The final non-empty line contains one result token and has no sequence number. A prefix that ends after a complete record but has no terminal token is treated as incomplete with result `*`.

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

The first record must be `run.start`. It contains both:

- `source_snapshot`: the exact STS2 `SerializableRun` JSON and schema information, used by compatible in-game replay; and
- `state`: the portable normalized state projection, used by simulators.

The portable projection is authoritative for cross-implementation comparison. A simulator must not need to understand private STS2 serialization details to initialize a supported run.

Secrets, platform account identifiers, and unnecessary profile history must not be included. Player identifiers are anonymized to run slots.

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
- `state` may be a full projection or a documented partial projection.

Writers generate lightweight observations after settled actions and full observations at run start, room boundaries, combat turn boundaries, reward boundaries, and run end. The exact version-one projection is finalized during Phase 4.

The Phase 2 core library validates the observation envelope (`after`, `context`, `digest`, `projection_version`, and object-valued `state`) and verifies that the digest matches the canonical state. It deliberately does not validate game-specific fields inside `state` until that projection is finalized.

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
