using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Mod.Integration;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
internal static class RunEndedPatch
{
    [HarmonyPostfix]
    private static void Postfix(RunManager __instance, bool isVictory) =>
        RecorderService.Finish(isVictory, __instance.IsAbandoned);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class RunCleanupPatch
{
    [HarmonyPrefix]
    private static void Prefix() => RecorderService.StopIncomplete();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedSingleplayer))]
internal static class RunResumePatch
{
    [HarmonyPrefix]
    private static void Prefix() => RecorderService.MarkNextRunAsResume();
}

[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
internal static class EventChoicePatch
{
    [HarmonyPrefix]
    private static void Prefix(int index) => RecorderService.RecordEventChoice(index);
}

[HarmonyPatch(typeof(NAncientEventLayout), "OnDialogueHitboxClicked")]
internal static class EventDialogueAdvancePatch
{
    [HarmonyPrefix]
    private static void Prefix() => RecorderService.RecordEventAdvance();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.ProceedFromTerminalRewardsScreen))]
internal static class RoomProceedPatch
{
    [HarmonyPrefix]
    private static void Prefix() => RecorderService.RecordRoomProceed();
}

[HarmonyPatch]
internal static class RewardSelectionPatch
{
    private static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(RewardsSetSynchronizer))
        .Single(method => method.Name == "SelectRewardForPlayer" &&
            method.ReturnType == typeof(Task<bool>) &&
            method.GetParameters().Length == 2 &&
            method.GetParameters()[1].ParameterType == typeof(Reward));

    [HarmonyPrefix]
    private static void Prefix(object setState, Reward reward, out RewardSelectionSnapshot? __state)
    {
        RewardSelectionSnapshot? snapshot = null;
        RecorderService.ExecuteSafely("capturing reward selection", () =>
        {
            RewardsSet set = RewardSetFromState(setState);
            snapshot = new RewardSelectionSnapshot(
                RecorderService.BeginRewardClaim(set, reward),
                reward.Player.NetId);
        });
        __state = snapshot;
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task<bool> __result, RewardSelectionSnapshot? __state)
    {
        if (__state is not null)
        {
            __result = RecordResolution(__result, __state);
        }
    }

    private static async Task<bool> RecordResolution(Task<bool> selection, RewardSelectionSnapshot snapshot)
    {
        bool selected = await selection;
        RecorderService.ResolveRewardClaim(snapshot.ActionSequence, snapshot.PlayerId, selected);
        return selected;
    }

    internal static RewardsSet RewardSetFromState(object state) =>
        (RewardsSet)AccessTools.Field(state.GetType(), "set").GetValue(state)!;

    internal sealed record RewardSelectionSnapshot(long? ActionSequence, ulong PlayerId);
}

[HarmonyPatch]
internal static class RewardSkipPatch
{
    private static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(RewardsSetSynchronizer))
        .Single(method => method.Name == "SkipRewardsSetOnStackTopForPlayer");

    [HarmonyPostfix]
    private static void Postfix(object __result) => RecorderService.ExecuteSafely(
        "capturing reward skip",
        () => RecorderService.RecordRewardSkip(RewardSelectionPatch.RewardSetFromState(__result)));
}

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
internal static class MerchantPurchasePatch
{
    [HarmonyPrefix]
    private static void Prefix(
        MerchantEntry __instance,
        MerchantInventory? inventory,
        bool ignoreCost,
        out ShopPurchaseSnapshot? __state)
    {
        ShopPurchaseSnapshot? snapshot = null;
        RecorderService.ExecuteSafely("capturing merchant purchase", () =>
        {
            if (__instance.IsStocked && (__instance.EnoughGold || ignoreCost))
            {
                snapshot = ShopPurchaseSnapshot.Capture(__instance, inventory, ignoreCost);
                snapshot = snapshot with
                {
                    ActionSequence = RecorderService.TryRecord("shop.purchase", new
                    {
                        entry_type = snapshot.EntryType,
                        inventory_index = snapshot.InventoryIndex,
                        entry_group = snapshot.EntryGroup,
                        entry_index = snapshot.EntryIndex,
                        model_id = snapshot.ModelId,
                        cost = snapshot.Cost,
                        ignore_cost = snapshot.IgnoreCost,
                    }, snapshot.PlayerId),
                };
            }
        });
        __state = snapshot;
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task<bool> __result, ShopPurchaseSnapshot? __state)
    {
        if (__state is not null)
        {
            __result = ShopPurchaseSnapshot.RecordResolution(__result, __state);
        }
    }
}

[HarmonyPatch(typeof(OneOffSynchronizer), nameof(OneOffSynchronizer.DoLocalMerchantCardRemoval))]
internal static class MerchantCardRemovalPatch
{
    [HarmonyPrefix]
    private static void Prefix(int goldCost, bool cancelable, out long? __state) =>
        __state = RecorderService.BeginShopCardRemoval(goldCost, cancelable);

    [HarmonyPostfix]
    private static void Postfix(ref Task<bool> __result, long? __state) =>
        __result = RecordResolution(__result, __state);

    private static async Task<bool> RecordResolution(Task<bool> removal, long? actionSequence)
    {
        bool succeeded = await removal;
        RecorderService.ResolveShopCardRemoval(actionSequence, succeeded);
        return succeeded;
    }
}

internal sealed record ShopPurchaseSnapshot(
    ulong PlayerId,
    string EntryType,
    int? InventoryIndex,
    string? EntryGroup,
    int? EntryIndex,
    string? ModelId,
    int Cost,
    bool IgnoreCost,
    long? ActionSequence = null)
{
    private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(MerchantEntry), "_player");

    public static ShopPurchaseSnapshot Capture(
        MerchantEntry entry,
        MerchantInventory? inventory,
        bool ignoreCost)
    {
        Player player = (Player)PlayerField.GetValue(entry)!;
        string? modelId = entry switch
        {
            MerchantCardEntry card => card.CreationResult?.Card.Id.ToString(),
            MerchantRelicEntry relic => relic.Model?.Id.ToString(),
            MerchantPotionEntry potion => potion.Model?.Id.ToString(),
            _ => null,
        };
        string entryType = entry switch
        {
            MerchantCardEntry => "card",
            MerchantRelicEntry => "relic",
            MerchantPotionEntry => "potion",
            _ => PortablePayloads.SymbolFromType(entry.GetType(), "Merchant", "Entry"),
        };
        int? inventoryIndex = IndexOfReference(inventory?.AllEntries, entry);
        (string? entryGroup, int? entryIndex) = entry switch
        {
            MerchantCardEntry card when IndexOfReference(inventory?.CharacterCardEntries, card) is { } index =>
                ("character_card", index),
            MerchantCardEntry card when IndexOfReference(inventory?.ColorlessCardEntries, card) is { } index =>
                ("colorless_card", index),
            MerchantRelicEntry relic => ("relic", IndexOfReference(inventory?.RelicEntries, relic)),
            MerchantPotionEntry potion => ("potion", IndexOfReference(inventory?.PotionEntries, potion)),
            _ => (null, null),
        };
        return new ShopPurchaseSnapshot(
            player.NetId,
            entryType,
            inventoryIndex,
            entryGroup,
            entryIndex,
            modelId,
            entry.Cost,
            ignoreCost);
    }

    private static int? IndexOfReference<T>(IEnumerable<T>? entries, T target)
        where T : class
    {
        if (entries is null)
        {
            return null;
        }

        int index = 0;
        foreach (T entry in entries)
        {
            if (ReferenceEquals(entry, target))
            {
                return index;
            }

            index++;
        }

        return null;
    }

    public static async Task<bool> RecordResolution(Task<bool> purchase, ShopPurchaseSnapshot snapshot)
    {
        bool succeeded = await purchase;
        if (snapshot.ActionSequence.HasValue)
        {
            RecorderService.TryRecord("shop.purchase.resolve", new
            {
                action_sequence = snapshot.ActionSequence.Value,
                success = succeeded,
            }, snapshot.PlayerId);
        }

        return succeeded;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer),
    nameof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer.ReserveChoiceId))]
internal static class ReservedChoiceOfferPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player player, uint __result) =>
        RecorderService.RegisterReservedChoice(player, __result);
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
internal static class ChooseCardOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cards, Player player, out uint? __state) =>
        __state = RecorderService.RegisterUpcomingChoice(player, "choose_a_card", new
        {
            options = cards.Select((card, index) => new
            {
                index,
                fingerprint = PortablePayloads.CardFingerprint(card),
            }).ToArray(),
        });

    [HarmonyPostfix]
    private static void Postfix(ref Task<CardModel?> __result, Player player, uint? __state) =>
        __result = ClearWhenComplete(__result, player, __state);

    private static async Task<CardModel?> ClearWhenComplete(Task<CardModel?> task, Player player, uint? choiceId)
    {
        try
        {
            return await task;
        }
        finally
        {
            RecorderService.ClearUpcomingChoice(player, choiceId);
        }
    }
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards))]
internal static class RewardGridOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(List<CardCreationResult> cards, Player player, out uint? __state) =>
        __state = CardGridOfferPatch.Register(cards.Select(result => result.Card).ToArray(), player, "reward_grid");

    [HarmonyPostfix]
    private static void Postfix(ref Task<IEnumerable<CardModel>> __result, Player player, uint? __state) =>
        __result = CardGridOfferPatch.ClearWhenComplete(__result, player, __state);
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
internal static class CardGridOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cardsIn, Player player, out uint? __state) =>
        __state = Register(cardsIn, player, "card_grid");

    [HarmonyPostfix]
    private static void Postfix(ref Task<IEnumerable<CardModel>> __result, Player player, uint? __state) =>
        __result = ClearWhenComplete(__result, player, __state);

    internal static uint? Register(IReadOnlyList<CardModel> cards, Player player, string context) =>
        RecorderService.RegisterUpcomingChoice(player, context, new
        {
            options = cards.Select((card, index) => new
            {
                index,
                fingerprint = PortablePayloads.CardFingerprint(card),
            }).ToArray(),
        });

    internal static async Task<IEnumerable<CardModel>> ClearWhenComplete(
        Task<IEnumerable<CardModel>> task,
        Player player,
        uint? choiceId)
    {
        try
        {
            return await task;
        }
        finally
        {
            RecorderService.ClearUpcomingChoice(player, choiceId);
        }
    }
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseABundleScreen))]
internal static class BundleOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<IReadOnlyList<CardModel>> bundles, Player player, out uint? __state) =>
        __state = RecorderService.RegisterUpcomingChoice(player, "card_bundle", new
        {
            options = bundles.Select((bundle, index) => new
            {
                index,
                cards = bundle.Select(PortablePayloads.CardFingerprint).ToArray(),
            }).ToArray(),
        });

    [HarmonyPostfix]
    private static void Postfix(ref Task<IEnumerable<CardModel>> __result, Player player, uint? __state) =>
        __result = CardGridOfferPatch.ClearWhenComplete(__result, player, __state);
}

[HarmonyPatch(typeof(RelicSelectCmd), nameof(RelicSelectCmd.FromChooseARelicScreen))]
internal static class RelicOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<RelicModel> relics, Player player, out uint? __state) =>
        __state = RecorderService.RegisterUpcomingChoice(player, "relic", new
        {
            options = relics.Select((relic, index) => new
            {
                index,
                model_id = relic.Id.ToString(),
            }).ToArray(),
        });

    [HarmonyPostfix]
    private static void Postfix(ref Task<RelicModel?> __result, Player player, uint? __state) =>
        __result = ClearWhenComplete(__result, player, __state);

    private static async Task<RelicModel?> ClearWhenComplete(Task<RelicModel?> task, Player player, uint? choiceId)
    {
        try
        {
            return await task;
        }
        finally
        {
            RecorderService.ClearUpcomingChoice(player, choiceId);
        }
    }
}

[HarmonyPatch(typeof(CrystalSphereMinigame), MethodType.Constructor, typeof(Player), typeof(MegaCrit.Sts2.Core.Random.Rng), typeof(int))]
internal static class CrystalSphereStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        CrystalSphereMinigame __instance,
        Player owner,
        MegaCrit.Sts2.Core.Random.Rng rng)
    {
        RecorderService.ExecuteSafely("capturing Crystal Sphere board", () =>
            RecorderService.TryRecord("event.crystal_sphere.start", new
        {
            grid = new { width = __instance.GridSize.X, height = __instance.GridSize.Y },
            divinations = __instance.DivinationCount,
            rng = new { seed = rng.Seed, counter = rng.Counter },
            initial_tool = ToolName(__instance.CrystalSphereTool),
            placed_all_items = __instance.PlacedAllItems,
            cells = Enumerable.Range(0, __instance.GridSize.X)
                .SelectMany(x => Enumerable.Range(0, __instance.GridSize.Y).Select(y => new
                {
                    x,
                    y,
                    is_hidden = __instance.cells[x, y].IsHidden,
                    item_index = __instance.cells[x, y].Item is { } cellItem
                        ? Enumerable.Range(0, __instance.Items.Count)
                            .First(index => ReferenceEquals(__instance.Items[index], cellItem))
                        : (int?)null,
                }))
                .ToArray(),
            items = __instance.Items.Select(item =>
            {
                SerializableCrystalSphereItem serializable = item.ToSerializable();
                return new
                {
                    type = PortablePayloads.Symbol(serializable.type),
                    position = new { x = item.Position.X, y = item.Position.Y },
                    size = new { width = item.Size.X, height = item.Size.Y },
                    is_good = item.IsGood,
                    card_rarity = serializable.type == CrystalSphereItemType.CardReward
                        ? PortablePayloads.Symbol(serializable.cardRarity)
                        : null,
                    potion_rarity = serializable.type == CrystalSphereItemType.Potion
                        ? PortablePayloads.Symbol(serializable.potionRarity)
                        : null,
                    is_big_gold = serializable.type == CrystalSphereItemType.Gold
                        ? serializable.isBigGold
                        : null as bool?,
                };
            }).ToArray(),
        }, owner.NetId));
    }

    internal static string ToolName(CrystalSphereMinigame.CrystalSphereToolType tool) =>
        PortablePayloads.Symbol(tool);
}

[HarmonyPatch(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.SetTool))]
internal static class CrystalSphereToolPatch
{
    [HarmonyPostfix]
    private static void Postfix(CrystalSphereMinigame.CrystalSphereToolType tool) =>
        RecorderService.RecordCrystalSphereTool(CrystalSphereStartPatch.ToolName(tool));
}

[HarmonyPatch(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked))]
internal static class CrystalSphereCellPatch
{
    [HarmonyPrefix]
    private static void Prefix(CrystalSphereMinigame __instance, CrystalSphereCell clickedCell) =>
        RecorderService.ExecuteSafely("capturing Crystal Sphere click", () =>
            RecorderService.RecordCrystalSphereClick(
                clickedCell.X,
                clickedCell.Y,
                CrystalSphereStartPatch.ToolName(__instance.CrystalSphereTool),
                __instance.DivinationCount));
}

[HarmonyPatch(typeof(NCrystalSphereScreen), "OnProceedButtonPressed")]
internal static class CrystalSphereProceedPatch
{
    [HarmonyPrefix]
    private static void Prefix() => RecorderService.RecordCrystalSphereProceed();
}
