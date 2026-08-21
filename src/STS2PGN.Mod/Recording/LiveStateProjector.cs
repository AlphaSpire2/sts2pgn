using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2PGN.Mod.Recording;

/// <summary>
/// Produces the complete portable Phase 4 state from authoritative live game objects.
/// Presentation state, localized text, wall-clock values, and profile side effects are excluded.
/// </summary>
internal static class LiveStateProjector
{
    public const string Scope = "run.core";

    public static JsonElement Capture(
        RunState state,
        IReadOnlyDictionary<ulong, string> playerSlots,
        JsonElement recorderState)
    {
        SerializableRun save = RunManager.Instance.ToSave(null);
        JsonObject root = JsonNode.Parse(InitialStateProjector.Capture(save, playerSlots).GetRawText())!.AsObject();
        root["scope"] = Scope;

        JsonObject run = root["run"]!.AsObject();
        run["current_act_model"] = state.Act.Id.ToString();
        run["total_floor"] = state.TotalFloor;
        run["current_map_coord"] = state.CurrentMapCoord is { } coord
            ? JsonSerializer.SerializeToNode(new { col = coord.col, row = coord.row })
            : null;
        run["visited_events"] = JsonSerializer.SerializeToNode(state.VisitedEventIds
            .Select(id => id.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray());
        run["map"] = state.Map is NullActMap
            ? null
            : JsonSerializer.SerializeToNode(
                InitialStateProjector.ProjectMap(SerializableActMap.FromActMap(state.Map)));

        JsonObject captured = JsonNode.Parse(recorderState.GetRawText())!.AsObject();
        root["rooms"] = captured["rooms"]?.DeepClone() ?? new JsonArray();
        root["choices"] = ProjectChoices(state, playerSlots, captured);
        root["special_event"] = captured["special_event"]?.DeepClone();
        root["combat"] = ProjectCombat(state, playerSlots);
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonNode ProjectChoices(
        RunState state,
        IReadOnlyDictionary<ulong, string> playerSlots,
        JsonObject recorderState)
    {
        object events = RunManager.Instance.EventSynchronizer.Events
            .Where(current => !current.IsFinished)
            .Select(current => new
            {
                player = current.Owner is null ? null : Slot(playerSlots, current.Owner.NetId),
                event_id = current.Id.ToString(),
                options = current.CurrentOptions.Select((option, index) => new
                {
                    index,
                    option_id = option.TextKey,
                    is_proceed = option.IsProceed,
                }).ToArray(),
            })
            .OrderBy(current => current.player, StringComparer.Ordinal)
            .ToArray();

        object? rest = state.CurrentRoom is RestSiteRoom restSite
            ? new
            {
                options = restSite.Options.Select((option, index) => new
                {
                    index,
                    option_id = option.OptionId,
                }).ToArray(),
            }
            : null;

        object? treasure = state.CurrentRoom is TreasureRoom
            && RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is { } relics
            ? new
            {
                relics = relics.Select((relic, index) => new
                {
                    index,
                    model_id = relic.Id.ToString(),
                }).ToArray(),
            }
            : null;

        object? merchant = state.CurrentRoom is MerchantRoom merchantRoom
            ? new
            {
                inventories = merchantRoom.Inventories.Select((inventory, playerIndex) => new
                {
                    player = playerIndex < state.Players.Count
                        ? Slot(playerSlots, state.Players[playerIndex].NetId)
                        : null,
                    entries = inventory.AllEntries.Select((entry, index) => ProjectMerchantEntry(entry, index)).ToArray(),
                }).ToArray(),
            }
            : null;

        return JsonSerializer.SerializeToNode(new
        {
            player_choices = recorderState["player_choices"]?.DeepClone(),
            rewards = recorderState["rewards"]?.DeepClone(),
            events,
            rest,
            treasure,
            merchant,
        })!;
    }

    private static object ProjectMerchantEntry(MerchantEntry entry, int index) => new
    {
        index,
        type = entry switch
        {
            MerchantCardEntry => "card",
            MerchantRelicEntry => "relic",
            MerchantPotionEntry => "potion",
            MerchantCardRemovalEntry => "card_removal",
            _ => PortablePayloads.SymbolFromType(entry.GetType(), "Merchant", "Entry"),
        },
        model_id = entry switch
        {
            MerchantCardEntry card => card.CreationResult?.Card.Id.ToString(),
            MerchantRelicEntry relic => relic.Model?.Id.ToString(),
            MerchantPotionEntry potion => potion.Model?.Id.ToString(),
            _ => null,
        },
        stocked = entry.IsStocked,
        cost = entry.Cost,
    };

    private static JsonNode? ProjectCombat(
        RunState state,
        IReadOnlyDictionary<ulong, string> playerSlots)
    {
        if (state.CurrentRoom is not CombatRoom room)
        {
            return null;
        }

        CombatState combat = room.CombatState;
        object players = combat.Players
            .OrderBy(player => Slot(playerSlots, player.NetId), StringComparer.Ordinal)
            .Select(player => ProjectCombatPlayer(player, playerSlots))
            .ToArray();
        object creatures = combat.Creatures
            .OrderBy(creature => creature.CombatId ?? uint.MaxValue)
            .Select(creature => ProjectCreature(creature, combat, playerSlots))
            .ToArray();

        // Once a fight has been won, the turn-handover flags always settle to
        // false: every path from the win check unwinds through synchronous
        // continuations to the line that clears them, and only the scheduling
        // of this capture against those continuations (the awaits
        // EndCombatInternal makes for saving and presentation) could read true
        // here. Project the settled value so the trace is deterministic. A
        // lost fight keeps its flags: its chain is cancelled with the combat's
        // own token, never reaches the clear, and that standing state is real.
        bool settledAfterWin = !CombatManager.Instance.IsInProgress
            && !CombatManager.Instance.IsStarting
            && combat.Players.All(player => !player.Creature.IsDead);

        return JsonSerializer.SerializeToNode(new
        {
            encounter_model = combat.Encounter?.Id.ToString(),
            round = combat.RoundNumber,
            side = PortablePayloads.Symbol(combat.CurrentSide),
            ending_player_turn_phase_one =
                !settledAfterWin && CombatManager.Instance.EndingPlayerTurnPhaseOne,
            ending_player_turn_phase_two =
                !settledAfterWin && CombatManager.Instance.EndingPlayerTurnPhaseTwo,
            player_actions_disabled = CombatManager.Instance.PlayerActionsDisabled,
            players,
            creatures,
        });
    }

    private static object ProjectCombatPlayer(
        Player player,
        IReadOnlyDictionary<ulong, string> playerSlots)
    {
        PlayerCombatState? combat = player.PlayerCombatState;
        return new
        {
            slot = Slot(playerSlots, player.NetId),
            turn = combat?.TurnNumber,
            phase = combat is null ? null : PortablePayloads.Symbol(combat.Phase),
            energy = combat?.Energy,
            max_energy = combat?.MaxEnergy,
            stars = combat?.Stars,
            ready_to_end_turn = combat is not null && CombatManager.Instance.IsPlayerReadyToEndTurn(player),
            piles = combat?.AllPiles.ToDictionary(
                pile => PortablePayloads.Symbol(pile.Type),
                pile => pile.Cards.Select(ProjectCombatCard).ToArray(),
                StringComparer.Ordinal),
            orbs = combat is null ? null : new
            {
                capacity = combat.OrbQueue.Capacity,
                items = combat.OrbQueue.Orbs.Select((orb, index) => new
                {
                    index,
                    model_id = orb.Id.ToString(),
                    passive = orb.PassiveVal,
                    evoke = orb.EvokeVal,
                    properties = PortablePayloads.SavedPropertiesFingerprint(SavedProperties.From(orb)),
                }).ToArray(),
            },
        };
    }

    private static object ProjectCombatCard(CardModel card) => new
    {
        combat_card_id = NetCombatCard.FromModel(card).CombatCardIndex,
        fingerprint = PortablePayloads.CardFingerprint(card),
        energy_cost = card.EnergyCost.GetWithModifiers(CostModifiers.All),
        costs_x = card.EnergyCost.CostsX,
        star_cost = card.GetStarCostWithModifiers(),
    };

    private static object ProjectCreature(
        Creature creature,
        CombatState combat,
        IReadOnlyDictionary<ulong, string> playerSlots) => new
        {
            combat_id = creature.CombatId,
            model_id = creature.ModelId.ToString(),
            side = PortablePayloads.Symbol(creature.Side),
            player = creature.Player is null ? null : Slot(playerSlots, creature.Player.NetId),
            pet_owner = creature.PetOwner is null ? null : Slot(playerSlots, creature.PetOwner.NetId),
            slot_name = creature.SlotName,
            current_hp = creature.CurrentHp,
            max_hp = creature.MaxHp,
            block = creature.Block,
            alive = creature.IsAlive,
            hittable = creature.IsHittable,
            powers = creature.Powers.Select((power, index) => new
            {
                index,
                model_id = power.Id.ToString(),
                amount = power.Amount,
                amount_on_turn_start = power.AmountOnTurnStart,
                type = PortablePayloads.Symbol(power.TypeForCurrentAmount),
                applier = power.Applier?.CombatId,
                target = power.Target?.CombatId,
                properties = PortablePayloads.SavedPropertiesFingerprint(SavedProperties.From(power)),
            }).ToArray(),
            move = creature.Monster is null ? null : new
            {
                move_id = creature.Monster.NextMove.Id,
                intents = creature.Monster.NextMove.Intents
                    .Select(intent => ProjectIntent(intent, combat, creature))
                    .ToArray(),
            },
        };

    private static object ProjectIntent(AbstractIntent intent, CombatState combat, Creature owner)
    {
        AttackIntent? attack = intent as AttackIntent;
        return new
        {
            type = PortablePayloads.Symbol(intent.IntentType),
            damage = attack?.GetSingleDamage(combat.PlayerCreatures, owner),
            repeats = attack?.Repeats,
            total_damage = attack?.GetTotalDamage(combat.PlayerCreatures, owner),
        };
    }

    private static string? Slot(IReadOnlyDictionary<ulong, string> slots, ulong playerId) =>
        slots.TryGetValue(playerId, out string? slot) ? slot : null;
}
