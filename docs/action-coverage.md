# Action Coverage Ledger

Status: Phase 3 action capture and Phase 4 authoritative observations are implemented against the current `decomp/` snapshot and `../sts2mcp` reference mod. Full mod compilation and an in-game smoke test still require the matching game assemblies.

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
| `run.resume` | `RunManager.SetUpSavedSingleplayer` (prefix captures the restored `SerializableRun`) | Continue the run's incomplete trace when `GameStartTime`/seed/build headers match, removing a crash-truncated final line first; re-anchor with the restored snapshot, portable `run.core` state, and a `run.resume` observation. Fall back to a fresh trace when no file can be safely continued. Multiplayer remains out of v1 scope. | Direct |
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
| `combat.end_turn` | `NetEndPlayerTurnAction`; accepted direct `PlayerCmd.EndTurn` fallback | Player slot and turn number. The fallback covers non-gameplay control mods such as STS2 MCP single-player without duplicating the normal queued action. | Event/Direct |
| `combat.undo_end_turn` | `NetUndoEndPlayerTurnAction` | Player slot and turn number. | Event |
| internal enemy-turn readiness | `NetReadyToBeginEnemyTurnAction` | Deterministic automatic synchronization, not a player decision. | Excluded |
| action resume | `ActionQueueSet.ActionResumed` | Paused action ID. Internal replay primitive, not normally semantic movetext. | Event |
| deterministic hook action | `GenericHookGameAction` | Hook ID, owner, action type. Internal replay primitive; simulator normally models its effects. | Event |

Phase 4 observations use `ActionExecutor.AfterActionExecuted` for ordinary queued actions. Map movement, end turn, act transitions, event choices, and asynchronous reward/rest/shop operations deliberately use their later domain-specific completion events instead, because the generic action callback is too early for those transitions.

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
| `event.crystal_sphere.start` | `CrystalSphereMinigame` constructor | Observe the complete deterministically generated grid, item list, cell-to-item mapping, and post-generation RNG seed/counter for state conformance; it is not simulator input. | Direct |
| `event.crystal_sphere.set_tool` | `CrystalSphereMinigame.SetTool` | Record the semantic `small` or `big` tool and observe the updated full minigame state. | Direct |
| `event.crystal_sphere.click` | `CrystalSphereMinigame.CellClicked` | Record logical cell coordinates, active tool, and divinations remaining before the click; await cell clearing/reward effects before observing the updated board. | Direct |
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
| `shop.remove_card` / `shop.remove_card.resolve` | `OneOffSynchronizer.DoLocalMerchantCardRemoval` plus `PlayerChoiceReceived` | Emit cost/cancelability before the deck choice; selected deck index/fingerprint follows; outcome is correlated afterward. `MerchantCardRemovalEntry` is excluded from generic `shop.purchase` interception so these paths are mutually exclusive. | Direct/Event |
| shop/rest/finished-event close | proceed button (or the finished event's synthetic PROCEED option) opens the map | Presentation-only; the following authoritative `map.choose` advances the run. | Excluded |

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

## Playback driver entry points

The playback driver (`src/STS2PGN.Mod/Playback/`) submits scripted decisions through the
same game-code paths a player's input reaches, so the recorder's existing hooks record
them unchanged. Per record kind: what the driver waits for, what it verifies against the
live game before acting, the call it makes, and the recorder context that settles it.

| Script kind | Ready when | Verified against | Submit call | Settles on |
|---|---|---|---|---|
| `combat.play_card` | combat in progress, player side in the Play phase (`PlayerCombatState.Phase`), queue idle | hand card by `combat_card_index` + `card` fingerprint; target creature `combat_id` + `model_id` | `ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target))` | `action.settled`, or readiness of the immediately following nested choice (including combat-pile grids such as Hologram) |
| `combat.use_potion` | idle | `potion_slot` model + target | `PotionModel.EnqueueManualUse(target)` | `action.settled` |
| `combat.discard_potion` | idle | `potion_slot` model | discard action enqueue | `action.settled` |
| `combat.end_turn` | player side, not yet ended, `turn` matches | turn number | `PlayerCmd.EndTurn(player, canBackOut: false)` (recorded by `DirectEndTurnPatch`) | `combat.player_turn.start` / reward or run-end settle |
| `map.choose` | map screen open (the driver first clicks the unrecorded Proceed button of a rest site, shop, treasure room, or event shop, or a finished event's synthetic PROCEED option, exactly as a player would) | travelable point at `(col,row)` | `NMapScreen.OnMapPointSelectedLocally(point)` | `room.enter` |
| `act.next` | act-end surface | — | `ActChangeSynchronizer.SetLocalPlayerReady()` | `act.enter` |
| `room.proceed` | terminal rewards screen, or the treasure room's enabled Proceed button after relic picking | not Crystal Sphere | `RunManager.ProceedFromTerminalRewardsScreen()` (treasure: force-click its Proceed button, which calls the same) | `reward.settled` / map open |
| `event.choose` | event room, options shown | `option_index` + `option_id`, unlocked | `EventSynchronizer.ChooseLocalOption(index)` | `event.choice.settled` / `room.enter` / `choice.offered` |
| `event.advance` | ancient dialogue shown | `event_id` | dialogue hitbox click | `event.dialogue.settled` |
| `event.crystal_sphere.set_tool` | Crystal Sphere screen open, minigame unfinished | semantic `small` or `big` tool is available | force-click the matching divination button | `event.crystal_sphere.tool` |
| `event.crystal_sphere.click` | Crystal Sphere screen open, minigame unfinished | logical `(x,y)` is a hidden live cell; `remaining_before` matches; `tool` is available | select the click's carried tool when needed, then force-click the live cell | `event.crystal_sphere.click` |
| `event.crystal_sphere.proceed` | Crystal Sphere's enabled Proceed button is visible after rewards finish | — | force-click the real Proceed button | decision recorded; the following map decision waits for map open |
| `rest.choose` | rest site options shown | enabled option with `option_id` | force-click the option's `NRestSiteButton` (the only path that calls `NRestSiteRoom.AfterSelectingOption`, which enables the Proceed button); `ChooseLocalOption(index)` only as fallback | `rest.settled`, or readiness of the immediately following nested choice |
| `shop.purchase` | merchant inventory open (the driver first clicks the unrecorded merchant button to open it) | entry at `inventory_index`/`entry_index`, type, group, model, stocked, affordable | `MerchantEntry.OnTryPurchaseWrapper(inventory)` | `shop.purchase.settled` |
| `shop.remove_card` | merchant inventory open (opened the same way) | removal entry stocked/affordable, `cost` | `OnTryPurchaseWrapper` on the removal entry | readiness of the immediately following nested choice, then `shop.remove_card.settled` |
| `reward.claim` / `reward.skip` | rewards set active | set by `set_id`; `Rewards[reward_index]` fingerprint, not yet selected; skipping allowed | force-click the screen's `NRewardButton` for the reward (the only path that emits `RewardClaimed`, which removes the button and closes a non-terminal loot screen) / the non-terminal screen's Skip `NProceedButton`; `SelectLocalReward(reward)` / `SkipLocalRewardsSet()` only as fallback | `reward.claim.settled` / `reward.skip.settled` |
| `treasure.choose_relic` / `skip_relic` | chest opened and its relic holders showing (the driver first clicks the unrecorded chest, as a player would; picking before the chest opens is accepted by the synchronizer but never obtains the relic or re-enables Proceed) | `offered_relics` equals `CurrentRelics`; `relic_index` model | force-click the `NTreasureRoomRelicHolder` (→ `PickRelicLocally(index)`) / `SkipRelicLocally` | `action.settled` |
| `choice.index` | a registered choice offer is pending and its screen is open | offer `context` and every option fingerprint/option id equal the script's `offered` | complete the open selection screen (press the live holders, confirm/skip) | decision recorded, then the parent's settle |
| `choice.cards` | as above | deck/hand card by index + fingerprint; registered offer if any | complete the open selection screen | decision recorded, then the parent's settle |
| `event.crystal_sphere.start` | — | generated board is observational, not simulator input | ignored by the script driver | — |
| `choice.player`, `choice.unknown`, `game.unknown_action`, `combat.undo_end_turn` | — | — | rejected by script validation; runtime backstop reports `unsupported_record` | — |

Nested choices are never submitted through `PlayerChoiceSynchronizer.SyncLocalChoice`:
`CardSelectCmd`, `CardReward`, and `RelicSelectCmd` await their screen and call it
themselves, and `CardSelectCmd.UseSelector` bypasses the synchronizer (and therefore the
recorder) entirely. Deck grids opened by `CardSelectCmd.FromDeck*` (upgrade, transform,
enchant, generic removal) register *driver-side only* live offers (`deck_upgrade`,
`deck_transform`, `deck_enchant`, `deck_grid`) so the driver can locate the live cards; these
never enter the trace or the projected `player_choices`, so recordings are unchanged.

## Observation coverage

The recorder must project enough authoritative state to validate effects that are not player actions:

| Boundary | Minimum observation |
|---|---|
| run start | Full portable run state, generated acts, saved maps, RNG counters, players, and initial inventory. |
| map generated | Full generated topology, current coordinate, visit history, act pools, and RNG state. |
| action settled | Full `run.core` projection correlated to the latest nested decision. |
| player decision offered | Available choices with stable IDs/fingerprints. |
| turn boundary | Full combat state, piles, creatures, powers, orbs, phase, RNG counters. |
| room boundary | Full run/player state and room/map history. |
| reward boundary | Offered rewards and resulting inventory/resources. |
| run end | Full terminal state and result. |

## Audit exit criteria

Phase 3 implementation exit criteria:

1. Every decision exposed by the reference MCP during an active run is mapped or explicitly excluded.
2. Every mapped input has an accepted-action hook; Phase 4 `run_start.core` and live `run.core` observations are implemented.
3. Duplicate signals from UI, synchronizers, action queues, and reward outcomes are deduplicated.
4. A seeded smoke run exercises every supported action family once matching STS2 assemblies are available.
5. A fresh trace confirms the implemented observation boundaries against the matching game build.
