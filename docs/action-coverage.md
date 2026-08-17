# Action Coverage Ledger

Status: Phase 3 recorder implementation is complete against the current `decomp/` snapshot and `../sts2mcp` reference mod. Full mod compilation and an in-game smoke test still require the matching game assemblies.

This ledger maps semantic STS2PGN records to authoritative game integration points. Line numbers are intentionally omitted because the decompile changes every patch. Paths and member names are the review anchors.

Legend:

- **Event**: usable public event exists.
- **Direct**: usable public method or action type exists; a narrow Harmony postfix/prefix may be required to observe it.
- **Derived**: record is derived from an authoritative state transition rather than a single input method.
- **Excluded**: not a semantic simulator input for the supported single-player trace.

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
| `combat.play_card` | `NetPlayCardAction` / `PlayCardAction` | Player slot, combat-card ID, card model/mutations, and target combat ID plus target model fingerprint. | Event |
| `combat.use_potion` | `NetUsePotionAction` / `UsePotionAction` | Player slot, potion slot/model, combat flag, and creature target ID/model or player target slot. | Event |
| `combat.discard_potion` | `NetDiscardPotionGameAction` | Player slot, potion slot/model, combat flag. | Event |
| `combat.end_turn` | `NetEndPlayerTurnAction` | Player slot and turn number. | Event |
| `combat.undo_end_turn` | `NetUndoEndPlayerTurnAction` | Player slot and turn number. | Event |
| internal enemy-turn readiness | `NetReadyToBeginEnemyTurnAction` | Deterministic automatic synchronization, not a player decision. | Excluded |
| action resume | `ActionQueueSet.ActionResumed` | Paused action ID. Internal replay primitive, not normally semantic movetext. | Event |
| deterministic hook action | `GenericHookGameAction` | Hook ID, owner, action type. Internal replay primitive; simulator normally models its effects. | Event |

Settled-state observation will be based on `ActionExecutor` completion plus player-choice/queue quiescence. That projection and quiescence implementation belongs to Phase 4; Phase 3 records the accepted inputs in execution order.

## Player choices

`Core.GameActions.Multiplayer/PlayerChoiceSynchronizer.cs` exposes `PlayerChoiceReceived`. `NetPlayerChoiceResult` supports canonical cards, combat cards, deck cards, mutable cards, players, and indexes.

| STS2PGN kind | Net choice type | Notes | Status |
|---|---|---|---|
| `choice.cards` | canonical/combat/deck/mutable card | Preserve ordered selected IDs and portable card fingerprints, including deck/combat indexes. | Event |
| `choice.player` | player | Convert network ID to stable player slot. | Event |
| `choice.index` | index | Includes offered fingerprints from card grids, bundles, relic screens, and every card-reward iteration. | Event/Direct |
| bundle choice | index | Represented by `choice.index` with `offered.context = card_bundle`; no separate wire kind is needed. | Direct |
| confirmation | screen-specific | Presentation input collapses into the authoritative synchronized choice result. | Excluded |
| cancellation | empty/nullable result | Represented by an empty synchronized result; intermediate UI back-navigation is presentation-only. | Event |

The reference implementations in `../sts2mcp/McpMod.Actions.cs` cover card grids, bundles, combat selection, relic selection, confirmation, and cancellation and will be used as a UI-surface audit list.

## Events and rest sites

| STS2PGN kind | Authoritative source | Strategy | Status |
|---|---|---|---|
| `event.choose` | `Core.Multiplayer.Game/EventSynchronizer.cs`: `ChooseLocalOption`, `PlayerVoteChanged` | Patch/observe the accepted option; store index plus event and option semantic IDs/text hash. | Direct/Event |
| `event.advance` | `NAncientEventLayout.OnDialogueHitboxClicked` | Record each progression-bearing dialogue advance. | Direct |
| `event.crystal_sphere.start` | `CrystalSphereMinigame` constructor | Record the complete generated grid, item list, cell-to-item mapping, and post-generation RNG seed/counter because the event is nondeterministic. | Direct |
| `event.crystal_sphere.set_tool` | `CrystalSphereMinigame.SetTool` | Record the semantic `small` or `big` tool. | Direct |
| `event.crystal_sphere.click` | `CrystalSphereMinigame.CellClicked` | Record logical cell coordinates, active tool, and divinations remaining before the click. | Direct |
| `event.crystal_sphere.proceed` | `NCrystalSphereScreen.OnProceedButtonPressed` | Record the explicit progression action without duplicating generic room proceed. | Direct |
| `rest.choose` / `rest.resolve` | `RestSiteSynchronizer.BeforePlayerOptionChosen`, `AfterPlayerOptionChosen` | Emit the option before nested card/player choices and correlate its eventual success afterward. | Event |

Nested rooms created by events are captured by the normal room stack and lifecycle events, not invented as separate event actions.

## Rewards and treasure

| STS2PGN kind | Authoritative source | Strategy | Status |
|---|---|---|---|
| `reward.claim` / `reward.resolve` | private accepted `RewardsSetSynchronizer.SelectRewardForPlayer(RewardsSetState, Reward)` | Emit set ID, reward index, portable reward contents, and offered cards before nested choice; correlate success afterward. | Direct |
| `reward.skip` | private `SkipRewardsSetOnStackTopForPlayer` | Captures explicit skip while `BeforeLeavingRoom` automatic cleanup remains excluded. | Direct |
| card reward choice | `PlayerChoiceReceived` plus `ReserveChoiceId` | `choice.index` stores every generated offered-card fingerprint and gives STS2's generated alternatives (including vanilla relic alternatives) explicit indexes in the same combined index space. | Event/Direct |
| obtained/skipped card | `RewardSynchronizer.SyncLocalObtainedCard` / `SyncLocalSkippedCard` | Outcome validation; avoid duplicating the player action. | Direct |
| obtained/skipped relic | corresponding `RewardSynchronizer` methods | Outcome validation. | Direct |
| obtained/skipped potion | corresponding `RewardSynchronizer` methods | Outcome validation, including full-belt decisions. | Direct |
| gold change | `SyncLocalObtainedGold` / `SyncLocalGoldLost` | State outcome, not normally a separate player action. | Direct |
| `treasure.choose_relic` | `TreasureRoomRelicSynchronizer.PickRelicLocally`, `PickRelicAction`, `RelicsAwarded` | Record offered relics and accepted index/model. | Direct/Event |
| `treasure.skip_relic` | `TreasureRoomRelicSynchronizer.SkipRelicLocally` | Record explicit skip. | Direct |

## Shops

| STS2PGN kind | Authoritative source | Strategy | Status |
|---|---|---|---|
| `shop.purchase` / `shop.purchase.resolve` | `Core.Entities.Merchant/MerchantEntry.cs`: `OnTryPurchaseWrapper` | Reproduce wrapper validation, then emit the pre-purchase flat inventory index, category/index, and portable type/model/cost before effects; correlate the outcome. | Direct |
| `shop.remove_card` / `shop.remove_card.resolve` | `OneOffSynchronizer.DoLocalMerchantCardRemoval` plus `PlayerChoiceReceived` | Emit cost/cancelability before the deck choice; selected deck index/fingerprint follows; outcome is correlated afterward. | Direct/Event |
| shop/rest close | proceed button opens the map | Presentation-only; the following authoritative `map.choose` advances the run. | Excluded |

Clicks rejected by wrapper validation (out of stock or insufficient gold) are not recorded. An accepted operation that later returns false is recorded with `success: false` because cancellation or a full inventory can affect the interaction sequence.

## Special and control surfaces from STS2 MCP

The reference mod exposes additional progression controls that must be audited even when they do not become distinct format records:

| Reference action | Proposed representation | Status |
|---|---|---|
| `advance_dialogue` | `event.advance` for ancient-event dialogue | Direct |
| `proceed` | `room.proceed` for terminal rewards; Crystal Sphere has its specific record; rest/shop map opening is presentation-only | Direct/Excluded |
| `select_card`, confirm/cancel selection | final `choice.cards` or `choice.index` synchronized result | Event |
| `select_bundle`, confirm/cancel bundle | `choice.index` with bundle fingerprints | Direct/Event |
| `select_relic`, `skip_relic_selection` | `choice.index` with relic fingerprints and an empty index list for skip | Direct/Event |
| `crystal_sphere_set_tool` | `event.crystal_sphere.set_tool` | Direct |
| `crystal_sphere_click_cell` | `event.crystal_sphere.click` | Direct |
| `crystal_sphere_proceed` | `event.crystal_sphere.proceed` | Direct |
| tutorial/modal dismissal | UI/profile progression outside semantic simulator state | Excluded |
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

Phase 3 implementation exit criteria:

1. Every decision exposed by the reference MCP during an active run is mapped or explicitly excluded.
2. Every mapped input has an accepted-action hook; settled state projection is explicitly Phase 4.
3. Duplicate signals from UI, synchronizers, action queues, and reward outcomes are deduplicated.
4. A seeded smoke run exercises every supported action family once matching STS2 assemblies are available.
5. Remaining research is limited to Phase 4 observation boundaries.
