# Action Coverage Ledger

Status: Phase 3 implementation audit against the current `decomp/` snapshot and `../sts2mcp` reference mod. Integration points marked implemented still require an in-game smoke test against the matching assemblies.

This ledger maps semantic STS2PGN records to authoritative game integration points. Line numbers are intentionally omitted because the decompile changes every patch. Paths and member names are the review anchors.

Legend:

- **Event**: usable public event exists.
- **Direct**: usable public method or action type exists; a narrow Harmony postfix/prefix may be required to observe it.
- **Derived**: record is derived from an authoritative state transition rather than a single input method.
- **Research**: exact accepted/settled hook must be confirmed during recorder implementation.

## Lifecycle and navigation

| STS2PGN kind | Authoritative source | Strategy | Status |
|---|---|---|---|
| `run.start` | `Core.Runs/RunManager.cs`: `RunStarted`, `ToSave` | Subscribe after launch; capture exact and portable initial state. | Event |
| `run.resume` | `RunManager.SetUpSavedSingleplayer` | Mark the trace launched from an existing save; multiplayer remains out of v1 scope. | Direct |
| `run.finish` | `RunManager.OnEnded` and abandonment path | Record final state/result before cleanup; distinguish victory, defeat, abandonment. | Direct |
| `act.enter` | `RunManager.ActEntered` | Observe new act after its state is initialized. | Event |
| `act.next` | `NetVoteToMoveToNextActAction` | Record accepted synchronized action. | Direct |
| `room.enter` | `RunManager.RoomEntered` | Derive room identity and stack from `RunState`. | Event |
| `room.exit` | `RunManager.RoomExited` | Capture state after authoritative exit. | Event |
| `room.proceed` | `RunManager.ProceedFromTerminalRewardsScreen` | Record the common progression command; Crystal Sphere uses its more specific record. | Direct |
| `map.choose` | `NetMoveToMapCoordAction`, `NetVoteForMapCoordAction`, `MapSelectionSynchronizer.PlayerVoteChanged` | Prefer accepted synchronized game action; store destination coordinate and source location. | Event/Direct |

## Combat

`Core.GameActions.Multiplayer/ActionQueueSet.cs` exposes `ActionEnqueued` and `ActionResumed`. `Core.Multiplayer.Replay/CombatReplayWriter.cs` proves these events plus `PlayerChoiceReceived` are sufficient for STS2's built-in combat replay.

| STS2PGN kind | Authoritative source | Required payload | Status |
|---|---|---|---|
| `combat.play_card` | `NetPlayCardAction` / `PlayCardAction` | Player slot, combat-card ID, card model/mutations, optional target combat ID. | Event |
| `combat.use_potion` | `NetUsePotionAction` / `UsePotionAction` | Player slot, potion slot/model, combat flag, creature/player target. | Event |
| `combat.discard_potion` | `NetDiscardPotionGameAction` | Player slot, potion slot/model, combat flag. | Event |
| `combat.end_turn` | `NetEndPlayerTurnAction` | Player slot and turn number. | Event |
| `combat.undo_end_turn` | `NetUndoEndPlayerTurnAction` | Player slot and turn number. | Event |
| internal enemy-turn readiness | `NetReadyToBeginEnemyTurnAction` | Normally automatic; retain only if needed for exact ordering. | Research |
| action resume | `ActionQueueSet.ActionResumed` | Paused action ID. Internal replay primitive, not normally semantic movetext. | Event |
| deterministic hook action | `GenericHookGameAction` | Hook ID, owner, action type. Internal replay primitive; simulator normally models its effects. | Event |

Settled-state observation will be based on `ActionExecutor` completion plus player-choice/queue quiescence. The exact signal is a Phase 3/4 audit item.

## Player choices

`Core.GameActions.Multiplayer/PlayerChoiceSynchronizer.cs` exposes `PlayerChoiceReceived`. `NetPlayerChoiceResult` supports canonical cards, combat cards, deck cards, mutable cards, players, and indexes.

| STS2PGN kind | Net choice type | Notes | Status |
|---|---|---|---|
| `choice.cards` | canonical/combat/deck/mutable card | Preserve ordered selected IDs and card fingerprints. | Event |
| `choice.player` | player | Convert network ID to stable player slot. | Event |
| `choice.index` | index | Include offered-choice fingerprints so index meaning can be validated. | Event |
| `choice.bundle` | usually index/card collection | Confirm exact game representation per screen. | Research |
| `choice.confirm` | screen-specific | Record only when confirmation is distinct from the synchronized choice. | Research |
| `choice.cancel` | empty/nullable result or screen-specific | Distinguish meaningful cancellation from presentation input. | Research |

The reference implementations in `../sts2mcp/McpMod.Actions.cs` cover card grids, bundles, combat selection, relic selection, confirmation, and cancellation and will be used as a UI-surface audit list.

## Events and rest sites

| STS2PGN kind | Authoritative source | Strategy | Status |
|---|---|---|---|
| `event.choose` | `Core.Multiplayer.Game/EventSynchronizer.cs`: `ChooseLocalOption`, `PlayerVoteChanged` | Patch/observe the accepted option; store index plus event and option semantic IDs/text hash. | Direct/Event |
| `event.advance` | `NAncientEventLayout.OnDialogueHitboxClicked` | Record each progression-bearing dialogue advance. | Direct |
| `event.crystal_sphere.start` | `CrystalSphereMinigame` constructor | Record the complete generated grid, item list and cell-to-item mapping because the event is nondeterministic. | Direct |
| `event.crystal_sphere.set_tool` | `CrystalSphereMinigame.SetTool` | Record the semantic `small` or `big` tool. | Direct |
| `event.crystal_sphere.click` | `CrystalSphereMinigame.CellClicked` | Record logical cell coordinates, active tool, and divinations remaining before the click. | Direct |
| `event.crystal_sphere.proceed` | `NCrystalSphereScreen.OnProceedButtonPressed` | Record the explicit progression action without duplicating generic room proceed. | Direct |
| `rest.choose` | `RestSiteSynchronizer.BeforePlayerOptionChosen`, `AfterPlayerOptionChosen` | Record canonical option identity and success after synchronization. | Event |

Nested rooms created by events are captured by the normal room stack and lifecycle events, not invented as separate event actions.

## Rewards and treasure

| STS2PGN kind | Authoritative source | Strategy | Status |
|---|---|---|---|
| `reward.claim` | `RewardsSetSynchronizer.SelectLocalReward` | Snapshot reward type/content before mutation and emit only when its task succeeds. | Direct |
| `reward.skip` | `RewardsSetSynchronizer.SkipLocalRewardsSet` and `BeforeLeavingRoom` | Distinguish explicit skip from leaving terminal room rewards. | Direct |
| `reward.choose_card` | card reward screen plus resulting `PlayerChoiceReceived`/reward sync | Store offered cards and chosen model/card identity. | Event/Research |
| obtained/skipped card | `RewardSynchronizer.SyncLocalObtainedCard` / `SyncLocalSkippedCard` | Outcome validation; avoid duplicating the player action. | Direct |
| obtained/skipped relic | corresponding `RewardSynchronizer` methods | Outcome validation. | Direct |
| obtained/skipped potion | corresponding `RewardSynchronizer` methods | Outcome validation, including full-belt decisions. | Direct |
| gold change | `SyncLocalObtainedGold` / `SyncLocalGoldLost` | State outcome, not normally a separate player action. | Direct |
| `treasure.choose_relic` | `TreasureRoomRelicSynchronizer.PickRelicLocally`, `PickRelicAction`, `RelicsAwarded` | Record offered relics and accepted index/model. | Direct/Event |
| `treasure.skip_relic` | `TreasureRoomRelicSynchronizer.SkipRelicLocally` | Record explicit skip. | Direct |

## Shops

| STS2PGN kind | Authoritative source | Strategy | Status |
|---|---|---|---|
| `shop.purchase` | `Core.Entities.Merchant/MerchantEntry.cs`: `OnTryPurchaseWrapper` | Snapshot entry type/model/cost before mutation and emit only when its task succeeds. | Direct |
| `shop.remove_card` | `MerchantCardRemovalEntry.OnTryPurchaseWrapper` plus `PlayerChoiceReceived` | Record cost only on success; the selected deck card is captured by the synchronized choice. | Direct/Event |
| `shop.leave` | merchant room proceed path | Record when required to advance the trace. | Research |

Failed purchases do not mutate the run and are not required for simulator replay. They may be retained later as optional annotations, never as authoritative actions.

## Special and control surfaces from STS2 MCP

The reference mod exposes additional progression controls that must be audited even when they do not become distinct format records:

| Reference action | Proposed representation | Status |
|---|---|---|
| `advance_dialogue` | `event.advance` when progression-bearing | Research |
| `proceed` | `room.proceed` | Research |
| `select_card`, `confirm_selection`, `cancel_selection` | `choice.*` plus synchronized result | Research |
| `select_bundle`, confirm/cancel bundle | `choice.bundle` | Research |
| `select_relic`, `skip_relic_selection` | `choice.relic` or treasure records | Research |
| `crystal_sphere_set_tool` | `event.crystal_sphere.set_tool` | Direct |
| `crystal_sphere_click_cell` | `event.crystal_sphere.click` | Direct |
| `crystal_sphere_proceed` | `event.crystal_sphere.proceed` | Direct |
| tutorial/modal dismissal | control record only if it gates replay | Research |
| non-interactive mode, menu/profile/lobby actions | excluded from run trace | Excluded |

## Observation coverage

The recorder must project enough authoritative state to validate effects that are not player actions:

| Boundary | Minimum observation |
|---|---|
| run start | Full portable run state, RNG counters, map, players, initial inventory. |
| action settled | Digest plus action-specific diagnostic subset. |
| player decision offered | Available choices with stable IDs/fingerprints. |
| turn boundary | Full combat state, piles, creatures, powers, orbs, phase, RNG counters. |
| room boundary | Full run/player state and room/map history. |
| reward boundary | Offered rewards and resulting inventory/resources. |
| run end | Full terminal state and result. |

## Audit exit criteria

Before recorder Phase 3 is complete:

1. Every decision exposed by the reference MCP during an active run is mapped or explicitly excluded.
2. Every mapped action has an accepted-action hook and a settled-state hook.
3. Duplicate signals from UI, synchronizers, action queues, and reward outcomes are deduplicated.
4. A seeded smoke run exercises every supported action family.
5. All remaining **Research** rows are resolved, narrowed to an explicit limitation, or moved to a later format version.
