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
                map_history = save.MapPointHistory.Select((act, actIndex) => new
                {
                    act = actIndex,
                    points = act.Select((point, floorIndex) => new
                    {
                        floor = floorIndex,
                        type = PortablePayloads.Symbol(point.MapPointType),
                        rooms = point.Rooms.Select((room, roomIndex) => new
                        {
                            index = roomIndex,
                            type = PortablePayloads.Symbol(room.RoomType),
                            model_id = PortablePayloads.ModelId(room.ModelId),
                            monsters = room.MonsterIds.Select(PortablePayloads.ModelId).ToArray(),
                            turns_taken = room.TurnsTaken,
                        }).ToArray(),
                        players = point.PlayerStats.Select(player => new
                        {
                            slot = playerSlots.TryGetValue(player.PlayerId, out string? slot) ? slot : null,
                            current_hp = player.CurrentHp,
                            max_hp = player.MaxHp,
                            current_gold = player.CurrentGold,
                            gold_gained = player.GoldGained,
                            gold_spent = player.GoldSpent,
                            gold_lost = player.GoldLost,
                            gold_stolen = player.GoldStolen,
                            stolen_loot = player.StolenLoot,
                            damage_taken = player.DamageTaken,
                            hp_healed = player.HpHealed,
                            max_hp_gained = player.MaxHpGained,
                            max_hp_lost = player.MaxHpLost,
                        }).ToArray(),
                    }).ToArray(),
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
            acts = save.Acts.Select((act, index) => ProjectAct(act, index)).ToArray(),
            players = save.Players.Select(player => ProjectPlayer(player, playerSlots)).ToArray(),
            rooms = Array.Empty<object>(),
            choices = new
            {
                player_choices = Array.Empty<object>(),
                rewards = Array.Empty<object>(),
                events = Array.Empty<object>(),
                rest = (object?)null,
                treasure = (object?)null,
                merchant = (object?)null,
            },
            special_event = (object?)null,
            combat = (object?)null,
        });
    }

    private static object ProjectAct(SerializableActModel act, int index)
    {
        SerializableRoomSet rooms = act.SerializableRooms;
        return new
        {
            index,
            model_id = PortablePayloads.ModelId(act.Id),
            rooms = new
            {
                events = rooms.EventIds.Select(PortablePayloads.ModelId).ToArray(),
                events_visited = rooms.EventsVisited,
                normal_encounters = rooms.NormalEncounterIds.Select(PortablePayloads.ModelId).ToArray(),
                normal_encounters_visited = rooms.NormalEncountersVisited,
                elite_encounters = rooms.EliteEncounterIds.Select(PortablePayloads.ModelId).ToArray(),
                elite_encounters_visited = rooms.EliteEncountersVisited,
                boss_encounters_visited = rooms.BossEncountersVisited,
                boss = PortablePayloads.ModelId(rooms.BossId),
                second_boss = PortablePayloads.ModelId(rooms.SecondBossId),
                ancient = PortablePayloads.ModelId(rooms.AncientId),
            },
            saved_map = IsNullMap(act.SavedMap) ? null : ProjectMap(act.SavedMap!),
        };
    }

    private static bool IsNullMap(SerializableActMap? map) =>
        map is null || (map.GridWidth == 0 && map.GridHeight == 0 && map.Points.Count == 0);

    internal static object ProjectMap(SerializableActMap map) => new
    {
        width = map.GridWidth,
        height = map.GridHeight,
        start = ProjectMapPoint(map.StartingPoint),
        boss = ProjectMapPoint(map.BossPoint),
        second_boss = map.SecondBossPoint is null ? null : ProjectMapPoint(map.SecondBossPoint),
        start_nodes = map.StartMapPointCoords?
            .OrderBy(coord => coord.row)
            .ThenBy(coord => coord.col)
            .Select(coord => new { col = coord.col, row = coord.row })
            .ToArray(),
        points = map.Points
            .OrderBy(point => point.Coord.row)
            .ThenBy(point => point.Coord.col)
            .Select(ProjectMapPoint)
            .ToArray(),
    };

    private static object ProjectMapPoint(SerializableMapPoint point) => new
    {
        id = $"{point.Coord.col},{point.Coord.row}",
        col = point.Coord.col,
        row = point.Coord.row,
        type = PortablePayloads.Symbol(point.PointType),
        can_modify = point.CanBeModified,
        children = (point.ChildCoords ?? [])
            .OrderBy(coord => coord.row)
            .ThenBy(coord => coord.col)
            .Select(coord => new { col = coord.col, row = coord.row })
            .ToArray(),
    };

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
