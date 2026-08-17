using System.Text.Json;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2PGN.Mod.Recording;

/// <summary>
/// Produces the first portable Phase 4 projection from STS2's run-start save.
/// This deliberately excludes wall-clock, platform, presentation, and discovery-only data.
/// </summary>
internal static class InitialStateProjector
{
    public const string Scope = "run_start.core";

    public static JsonElement Capture(
        SerializableRun save,
        IReadOnlyDictionary<ulong, string> playerSlots)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(playerSlots);

        return JsonSerializer.SerializeToElement(new
        {
            scope = Scope,
            run = new
            {
                seed = save.SerializableRng.Seed,
                mode = PortablePayloads.Symbol(save.GameMode),
                ascension = save.Ascension,
                act = save.CurrentActIndex,
                floor = save.FloorReached,
                reloads = save.NumReloads,
                visited_map = save.VisitedMapCoords.Select(coord => new
                {
                    col = coord.col,
                    row = coord.row,
                }).ToArray(),
                modifiers = save.Modifiers.Select(modifier => new
                {
                    model_id = PortablePayloads.ModelId(modifier.Id),
                    properties = PortablePayloads.SavedPropertiesFingerprint(modifier.Props),
                }).ToArray(),
                rng = new
                {
                    seed = save.SerializableRng.Seed,
                    counters = Counters(save.SerializableRng.Counters),
                },
                odds = new
                {
                    unknown_map_point_monster = save.SerializableOdds.UnknownMapPointMonsterOddsValue,
                    unknown_map_point_elite = save.SerializableOdds.UnknownMapPointEliteOddsValue,
                    unknown_map_point_treasure = save.SerializableOdds.UnknownMapPointTreasureOddsValue,
                    unknown_map_point_shop = save.SerializableOdds.UnknownMapPointShopOddsValue,
                },
                shared_relic_grab_bag = RelicGrabBag(save.SerializableSharedRelicGrabBag),
                extra = new
                {
                    started_with_neow = save.ExtraFields.StartedWithNeow,
                    test_subject_kills = save.ExtraFields.TestSubjectKills,
                    freed_repy = save.ExtraFields.FreedRepy,
                },
            },
            players = save.Players.Select(player => ProjectPlayer(player, playerSlots)).ToArray(),
            combat = (object?)null,
        });
    }

    private static object ProjectPlayer(
        SerializablePlayer player,
        IReadOnlyDictionary<ulong, string> playerSlots)
    {
        Dictionary<int, SerializablePotion> potions = player.Potions.ToDictionary(potion => potion.SlotIndex);
        return new
        {
            slot = playerSlots[player.NetId],
            character_model = PortablePayloads.ModelId(player.CharacterId),
            current_hp = player.CurrentHp,
            max_hp = player.MaxHp,
            max_energy = player.MaxEnergy,
            gold = player.Gold,
            base_orb_slots = player.BaseOrbSlotCount,
            max_potion_slots = player.MaxPotionSlotCount,
            deck = player.Deck.Select(PortablePayloads.CardFingerprint).ToArray(),
            relics = player.Relics.Select(relic => new
            {
                model_id = PortablePayloads.ModelId(relic.Id),
                floor_added_to_deck = relic.FloorAddedToDeck,
                properties = PortablePayloads.SavedPropertiesFingerprint(relic.Props),
            }).ToArray(),
            potion_slots = Enumerable.Range(0, player.MaxPotionSlotCount).Select(slot => new
            {
                slot,
                model_id = potions.TryGetValue(slot, out SerializablePotion? potion)
                    ? PortablePayloads.ModelId(potion.Id)
                    : null,
            }).ToArray(),
            rng = new
            {
                seed = player.Rng.Seed,
                counters = Counters(player.Rng.Counters),
            },
            odds = new
            {
                card_rarity = player.Odds.CardRarityOddsValue,
                potion_reward = player.Odds.PotionRewardOddsValue,
            },
            relic_grab_bag = RelicGrabBag(player.RelicGrabBag),
            unlocks = new
            {
                epochs = player.UnlockState.UnlockedEpochs.Order(StringComparer.Ordinal).ToArray(),
                encounters_seen = player.UnlockState.EncountersSeen
                    .Select(modelId => PortablePayloads.ModelId(modelId)!)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                number_of_runs = player.UnlockState.NumberOfRuns,
            },
            extra = new
            {
                card_shop_removals_used = player.ExtraFields.CardShopRemovalsUsed,
                wongo_points = player.ExtraFields.WongoPoints,
                ccccombo_badge_unlocked = player.ExtraFields.CccomboBadgeUnlocked,
                damage_dealt = player.ExtraFields.DamageDealt,
                debuffs_applied = player.ExtraFields.DebuffsApplied,
            },
        };
    }

    private static SortedDictionary<string, int> Counters<TEnum>(IReadOnlyDictionary<TEnum, int> counters)
        where TEnum : struct, Enum => new(counters.ToDictionary(
            pair => PortablePayloads.Symbol((Enum)(object)pair.Key),
            pair => pair.Value),
            StringComparer.Ordinal);

    private static SortedDictionary<string, string[]> RelicGrabBag(SerializableRelicGrabBag grabBag) =>
        new(grabBag.RelicIdLists.ToDictionary(
            pair => PortablePayloads.Symbol(pair.Key),
            pair => pair.Value.Select(modelId => PortablePayloads.ModelId(modelId)!).ToArray()),
            StringComparer.Ordinal);
}
