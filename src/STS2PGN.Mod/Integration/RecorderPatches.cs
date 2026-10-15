using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Mod.Integration;

[HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
internal static class CardPlayExecutionPatch
{
    [HarmonyPrefix]
    private static void Prefix(PlayCardAction __instance, out IDisposable? __state)
    {
        IDisposable? scope = null;
        RecorderService.ExecuteSafely("tracking card play execution", () =>
            scope = RecorderService.EnterCardPlayExecution(__instance));
        __state = scope;
    }

    // The async method retains its execution context; restore the caller's
    // context when it returns its Task, including synchronous failure paths.
    [HarmonyFinalizer]
    private static void Finalizer(IDisposable? __state) => __state?.Dispose();
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.SpendResources))]
internal static class AcceptedCardPlayPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel __instance) => RecorderService.RecordAcceptedCardPlay(__instance);
}

[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
internal static class NewRunProfilePatch
{
    [HarmonyPrefix]
    private static void Prefix(out string[]? __state)
    {
        string[]? discoveredActs = null;
        RecorderService.ExecuteSafely("capturing run profile", () =>
            discoveredActs = SaveManager.Instance.Progress.DiscoveredActs
                .Select(act => PortablePayloads.ModelId(act)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        __state = discoveredActs;
    }

    [HarmonyPostfix]
    private static void Postfix(RunState __result, string[]? __state)
    {
        if (__state is not null)
        {
            RecorderService.ExecuteSafely("retaining run profile", () =>
                RunProfileCapture.Remember(__result, __state));
        }
    }
}

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.EndTurn))]
internal static class DirectEndTurnPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, out DirectEndTurnSnapshot? __state)
    {
        __state = null;
        if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
        {
            return;
        }

        // Normal UI input is already represented by EndPlayerTurnAction. This patch covers
        // accepted callers which invoke PlayerCmd directly, such as STS2 MCP single-player.
        if (RunManager.Instance.IsInProgress
            && RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null)
        {
            __state = new DirectEndTurnSnapshot(
                player.NetId,
                player.PlayerCombatState?.TurnNumber ?? 0);
        }
    }

    [HarmonyPostfix]
    private static void Postfix(Player player, DirectEndTurnSnapshot? __state)
    {
        if (__state is not null && CombatManager.Instance.IsPlayerReadyToEndTurn(player))
        {
            RecorderService.RecordDirectEndTurn(__state.PlayerId, __state.Turn);
        }
    }

    private sealed record DirectEndTurnSnapshot(ulong PlayerId, int Turn);
}

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
    private static void Prefix(MegaCrit.Sts2.Core.Saves.SerializableRun save) =>
        RecorderService.MarkNextRunAsResume(save);
}

[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
internal static class EventChoicePatch
{
    [HarmonyPrefix]
    private static void Prefix(int index) => RecorderService.RecordEventChoice(index);
}

[HarmonyPatch(typeof(EventOption), nameof(EventOption.Chosen))]
internal static class EventChoiceSettledPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result) => __result = ObserveWhenComplete(__result);

    private static async Task ObserveWhenComplete(Task choice)
    {
        await choice;
        RecorderService.ObserveEventChoiceSettled();
    }
}

[HarmonyPatch(typeof(NAncientEventLayout), "OnDialogueHitboxClicked")]
internal static class EventDialogueAdvancePatch
{
    [HarmonyPrefix]
    private static void Prefix() => RecorderService.RecordEventAdvance();

    [HarmonyPostfix]
    private static void Postfix() => RecorderService.ObserveEventAdvanceSettled();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateMap))]
internal static class GeneratedMapPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task __result) => __result = ObserveWhenComplete(__result);

    private static async Task ObserveWhenComplete(Task generation)
    {
        await generation;
        RecorderService.ObserveMapGenerated();
    }
}

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
internal static class RewardsSetLifetimePatch
{
    [HarmonyPostfix]
    private static void Postfix(RewardsSet set, ref Task __result)
    {
        RecorderService.ExecuteSafely("capturing reward offer", () => RecorderService.BeginRewardsSet(set));
        __result = ObserveWhenComplete(__result, set);
    }

    private static async Task ObserveWhenComplete(Task lifetime, RewardsSet set)
    {
        try
        {
            await lifetime;
        }
        finally
        {
            RecorderService.ExecuteSafely("capturing completed reward set", () => RecorderService.EndRewardsSet(set));
        }
    }
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
            // Card removal has its own initiating action, nested choice, and resolution
            // records. It can reach this base wrapper through callers typed as
            // MerchantEntry, but must never also become a generic shop purchase.
            if (__instance is not MerchantCardRemovalEntry
                && __instance.IsStocked
                && (__instance.EnoughGold || ignoreCost))
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
            RecorderService.RecordShopPurchaseResolution(
                snapshot.ActionSequence.Value,
                snapshot.PlayerId,
                succeeded);
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
        }, cards);

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
        }, cards);

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
        }, bundles);

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
        }, relics);

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
        Player owner)
    {
        RecorderService.RecordCrystalSphereStart(__instance, owner.NetId, () => ProjectState(__instance));
    }

    internal static object ProjectState(CrystalSphereMinigame minigame) => new
    {
        kind = "crystal_sphere",
        grid = new { width = minigame.GridSize.X, height = minigame.GridSize.Y },
        divinations = minigame.DivinationCount,
        rng = new { seed = minigame.Rng.Seed, counter = minigame.Rng.Counter },
        tool = ToolName(minigame.CrystalSphereTool),
        placed_all_items = minigame.PlacedAllItems,
        cells = Enumerable.Range(0, minigame.GridSize.X)
            .SelectMany(x => Enumerable.Range(0, minigame.GridSize.Y).Select(y => new
            {
                x,
                y,
                is_hidden = minigame.cells[x, y].IsHidden,
                item_index = minigame.cells[x, y].Item is { } cellItem
                    ? Enumerable.Range(0, minigame.Items.Count)
                        .First(index => ReferenceEquals(minigame.Items[index], cellItem))
                    : (int?)null,
            }))
            .ToArray(),
        items = minigame.Items.Select(item =>
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
    };

    internal static string ToolName(CrystalSphereMinigame.CrystalSphereToolType tool) =>
        PortablePayloads.Symbol(tool);
}

[HarmonyPatch(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.SetTool))]
internal static class CrystalSphereToolPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        CrystalSphereMinigame __instance,
        CrystalSphereMinigame.CrystalSphereToolType tool) =>
        RecorderService.RecordCrystalSphereTool(
            __instance,
            CrystalSphereStartPatch.ToolName(tool),
            CrystalSphereStartPatch.ProjectState(__instance));
}

[HarmonyPatch(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked))]
internal static class CrystalSphereCellPatch
{
    [HarmonyPrefix]
    private static void Prefix(CrystalSphereMinigame __instance, CrystalSphereCell clickedCell, out long? __state) =>
        __state = RecorderService.RecordCrystalSphereClick(
            __instance,
            clickedCell.X,
            clickedCell.Y,
            CrystalSphereStartPatch.ToolName(__instance.CrystalSphereTool),
            __instance.DivinationCount);

    [HarmonyPostfix]
    private static void Postfix(CrystalSphereMinigame __instance, long? __state, ref Task __result) =>
        __result = ObserveWhenComplete(__result, __instance, __state);

    private static async Task ObserveWhenComplete(Task click, CrystalSphereMinigame minigame, long? sequence)
    {
        await click;
        RecorderService.ObserveCrystalSphereClickSettled(minigame, sequence, CrystalSphereStartPatch.ProjectState(minigame));
    }
}

[HarmonyPatch(typeof(CrystalSphereMinigame), "CompleteMinigame")]
internal static class CrystalSpherePayoutPatch
{
    [HarmonyPrefix]
    private static void Prefix(CrystalSphereMinigame __instance, out IDisposable __state) =>
        __state = RecorderService.EnterCrystalSpherePayout(__instance);

    // Restore the caller's context when the async method returns its Task. Its
    // continuations retain the payout scope until they offer the generated set.
    [HarmonyFinalizer]
    private static void Finalizer(IDisposable? __state) => __state?.Dispose();
}

[HarmonyPatch(typeof(NCrystalSphereScreen), "OnProceedButtonPressed")]
internal static class CrystalSphereProceedPatch
{
    [HarmonyPrefix]
    private static void Prefix() => RecorderService.RecordCrystalSphereProceed();
}

// Combat-pile and deck selection screens are opened after the choice id was reserved. Register
// their live models for the playback driver only; the recorded trace remains unchanged.

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCombatPileCardSelectScreen),
    nameof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCombatPileCardSelectScreen.Create))]
internal static class CombatPileGridLiveOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardPile pile, Func<CardModel, bool> filter) =>
        RecorderService.RegisterLiveOnlyChoice(
            "combat_pile_grid",
            pile.Cards.Where(filter).ToArray());
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen),
    nameof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen.Create))]
internal static class DeckGridLiveOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cards) =>
        RecorderService.RegisterLiveOnlyChoice("deck_grid", cards);
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen),
    nameof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen.ShowScreen))]
internal static class DeckUpgradeLiveOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cards) =>
        RecorderService.RegisterLiveOnlyChoice("deck_upgrade", cards);
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen),
    nameof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen.ShowScreen))]
internal static class DeckTransformLiveOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cards) =>
        RecorderService.RegisterLiveOnlyChoice("deck_transform", cards);
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen),
    nameof(MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen.ShowScreen))]
internal static class DeckEnchantLiveOfferPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<CardModel> cards) =>
        RecorderService.RegisterLiveOnlyChoice("deck_enchant", cards);
}
