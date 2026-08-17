using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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
    [HarmonyPostfix]
    private static void Postfix(int index) => RecorderService.RecordEventChoice(index);
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

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward))]
internal static class RewardSelectionPatch
{
    [HarmonyPrefix]
    private static void Prefix(Reward reward, out RewardSelectionSnapshot? __state)
    {
        RewardSelectionSnapshot? snapshot = null;
        RecorderService.ExecuteSafely("capturing reward selection", () =>
            snapshot = new RewardSelectionSnapshot(
                reward.Player.NetId,
                reward.GetType().Name,
                JsonSerializer.SerializeToElement(reward.ToSerializable())));
        __state = snapshot;
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task<bool> __result, RewardSelectionSnapshot? __state)
    {
        if (__state is not null)
        {
            __result = RecordIfSuccessful(__result, __state);
        }
    }

    private static async Task<bool> RecordIfSuccessful(Task<bool> selection, RewardSelectionSnapshot snapshot)
    {
        bool selected = await selection;
        if (selected)
        {
            RecorderService.TryRecord("reward.claim", new
            {
                reward_type = snapshot.RewardType,
                reward = snapshot.Reward,
            }, snapshot.PlayerId);
        }

        return selected;
    }

    internal sealed record RewardSelectionSnapshot(ulong PlayerId, string RewardType, JsonElement Reward);
}

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SkipLocalRewardsSet))]
internal static class RewardSkipPatch
{
    [HarmonyPostfix]
    private static void Postfix() => RecorderService.TryRecord("reward.skip", new { });
}

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
internal static class MerchantPurchasePatch
{
    [HarmonyPrefix]
    private static void Prefix(MerchantEntry __instance, out ShopPurchaseSnapshot? __state)
    {
        ShopPurchaseSnapshot? snapshot = null;
        RecorderService.ExecuteSafely("capturing merchant purchase", () =>
            snapshot = ShopPurchaseSnapshot.Capture(__instance));
        __state = snapshot;
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task<bool> __result, ShopPurchaseSnapshot? __state)
    {
        if (__state is not null)
        {
            __result = ShopPurchaseSnapshot.RecordIfSuccessful(__result, __state);
        }
    }
}

[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper))]
internal static class MerchantCardRemovalPurchasePatch
{
    [HarmonyPrefix]
    private static void Prefix(MerchantCardRemovalEntry __instance, out ShopPurchaseSnapshot? __state)
    {
        ShopPurchaseSnapshot? snapshot = null;
        RecorderService.ExecuteSafely("capturing merchant card removal", () =>
            snapshot = ShopPurchaseSnapshot.Capture(__instance));
        __state = snapshot;
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task<bool> __result, ShopPurchaseSnapshot? __state)
    {
        if (__state is not null)
        {
            __result = ShopPurchaseSnapshot.RecordIfSuccessful(__result, __state);
        }
    }
}

internal sealed record ShopPurchaseSnapshot(ulong PlayerId, string EntryType, string? ModelId, int Cost)
{
    private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(MerchantEntry), "_player");

    public static ShopPurchaseSnapshot Capture(MerchantEntry entry)
    {
        Player player = (Player)PlayerField.GetValue(entry)!;
        string? modelId = entry switch
        {
            MerchantCardEntry card => card.CreationResult?.Card.Id.ToString(),
            MerchantRelicEntry relic => relic.Model?.Id.ToString(),
            MerchantPotionEntry potion => potion.Model?.Id.ToString(),
            _ => null,
        };
        return new ShopPurchaseSnapshot(player.NetId, entry.GetType().Name, modelId, entry.Cost);
    }

    public static async Task<bool> RecordIfSuccessful(Task<bool> purchase, ShopPurchaseSnapshot snapshot)
    {
        bool succeeded = await purchase;
        if (succeeded)
        {
            string kind = snapshot.EntryType == nameof(MerchantCardRemovalEntry)
                ? "shop.remove_card"
                : "shop.purchase";
            RecorderService.TryRecord(kind, new
            {
                entry_type = snapshot.EntryType,
                model_id = snapshot.ModelId,
                cost = snapshot.Cost,
            }, snapshot.PlayerId);
        }

        return succeeded;
    }
}

[HarmonyPatch(typeof(CrystalSphereMinigame), MethodType.Constructor, typeof(Player), typeof(MegaCrit.Sts2.Core.Random.Rng), typeof(int))]
internal static class CrystalSphereStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(CrystalSphereMinigame __instance, Player owner)
    {
        RecorderService.ExecuteSafely("capturing Crystal Sphere board", () =>
            RecorderService.TryRecord("event.crystal_sphere.start", new
        {
            grid = new { width = __instance.GridSize.X, height = __instance.GridSize.Y },
            divinations = __instance.DivinationCount,
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
                    type = serializable.type.ToString().ToLowerInvariant(),
                    position = new { x = item.Position.X, y = item.Position.Y },
                    size = new { width = item.Size.X, height = item.Size.Y },
                    is_good = item.IsGood,
                    card_rarity = serializable.type == CrystalSphereItemType.CardReward
                        ? serializable.cardRarity.ToString().ToLowerInvariant()
                        : null,
                    potion_rarity = serializable.type == CrystalSphereItemType.Potion
                        ? serializable.potionRarity.ToString().ToLowerInvariant()
                        : null,
                    is_big_gold = serializable.type == CrystalSphereItemType.Gold
                        ? serializable.isBigGold
                        : null as bool?,
                };
            }).ToArray(),
        }, owner.NetId));
    }

    internal static string ToolName(CrystalSphereMinigame.CrystalSphereToolType tool) =>
        tool.ToString().ToLowerInvariant();
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
