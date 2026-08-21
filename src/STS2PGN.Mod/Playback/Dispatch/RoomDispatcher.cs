using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>event.choose, event.advance, rest.choose, shop.purchase, shop.remove_card.</summary>
internal sealed class RoomDispatcher : IDecisionDispatcher
{
    public bool Handles(string kind) =>
        kind is "event.choose" or "event.advance" or "rest.choose" or "shop.purchase" or "shop.remove_card";

    public bool IsReady(ScriptDecision decision)
    {
        try
        {
            return decision.Kind switch
            {
                "event.choose" => NEventRoom.Instance is not null && CurrentEvent() is { IsFinished: false } current && current.CurrentOptions.Count > 0,
                "event.advance" => DialogueHitbox() is not null,
                "rest.choose" => NRestSiteRoom.Instance is not null && RunManager.Instance.RestSiteSynchronizer.GetLocalOptions().Count > 0,
                "shop.purchase" or "shop.remove_card" => NMerchantRoom.Instance is { Inventory.IsOpen: true } && LocalInventory() is not null,
                _ => false,
            };
        }
        catch
        {
            // Rooms expose their synchronizers before they finish entering; treat as not ready.
            return false;
        }
    }

    public VerifyResult Verify(ScriptDecision decision) => decision.Kind switch
    {
        "event.choose" => VerifyEventChoice(decision),
        "event.advance" => VerifyEventAdvance(decision),
        "rest.choose" => VerifyRest(decision),
        "shop.purchase" => VerifyPurchase(decision),
        "shop.remove_card" => VerifyRemoval(decision),
        _ => VerifyResult.Mismatch($"unsupported kind {decision.Kind}", null, null),
    };

    public Task SubmitAsync(ScriptDecision decision)
    {
        switch (decision.Kind)
        {
            case "event.choose":
                ScriptDecisionReader.TryGetInt(decision.Data, "option_index", out int optionIndex);
                RunManager.Instance.EventSynchronizer.ChooseLocalOption(optionIndex);
                break;
            case "event.advance":
                DialogueHitbox()!.ForceClick();
                break;
            case "rest.choose":
                _ = RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(RestOptionIndex(decision));
                break;
            case "shop.purchase":
            {
                MerchantInventory inventory = LocalInventory()!;
                ScriptDecisionReader.TryGetInt(decision.Data, "inventory_index", out int index);
                MerchantEntry entry = inventory.AllEntries.ElementAt(index);
                _ = entry.OnTryPurchaseWrapper(inventory);
                break;
            }

            case "shop.remove_card":
            {
                MerchantInventory inventory = LocalInventory()!;
                bool cancelable = !ScriptDecisionReader.TryGetBool(decision.Data, "cancelable", out bool flag) || flag;
                _ = inventory.CardRemovalEntry!.OnTryPurchaseWrapper(inventory, ignoreCost: false, cancelable: cancelable);
                break;
            }
        }

        return Task.CompletedTask;
    }

    private static EventModel? CurrentEvent() => RunManager.Instance.EventSynchronizer.Events.FirstOrDefault();

    private static NClickableControl? DialogueHitbox()
    {
        if (NEventRoom.Instance?.Layout is not NAncientEventLayout layout)
        {
            return null;
        }

        NClickableControl? hitbox = layout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
        return LiveGame.IsUsable(hitbox) ? hitbox : null;
    }

    private static VerifyResult VerifyEventChoice(ScriptDecision decision)
    {
        EventModel current = CurrentEvent()!;
        IReadOnlyList<EventOption> options = current.CurrentOptions;
        object offered = new
        {
            event_id = current.Id.ToString(),
            options = options.Select((option, index) => new { option_index = index, option_id = option.TextKey, locked = option.IsLocked }).ToArray(),
        };
        string? expectedEvent = ScriptDecisionReader.GetString(decision.Data, "event_id");
        if (expectedEvent is not null && !string.Equals(expectedEvent, current.Id.ToString(), StringComparison.Ordinal))
        {
            return VerifyResult.Mismatch("a different event is in progress", decision.Data, offered);
        }

        if (!ScriptDecisionReader.TryGetInt(decision.Data, "option_index", out int index) || index < 0 || index >= options.Count
            || !string.Equals(options[index].TextKey, ScriptDecisionReader.GetString(decision.Data, "option_id"), StringComparison.Ordinal))
        {
            return VerifyResult.Mismatch("the scripted event option is not offered at that index", decision.Data, offered);
        }

        return options[index].IsLocked
            ? VerifyResult.Mismatch("the scripted event option is locked", decision.Data, offered)
            : VerifyResult.Success;
    }

    private static VerifyResult VerifyEventAdvance(ScriptDecision decision)
    {
        string? live = CurrentEvent()?.Id.ToString();
        string? expected = ScriptDecisionReader.GetString(decision.Data, "event_id");
        return expected is not null && !string.Equals(expected, live, StringComparison.Ordinal)
            ? VerifyResult.Mismatch("a different event is showing dialogue", decision.Data, new { event_id = live })
            : VerifyResult.Success;
    }

    private static int RestOptionIndex(ScriptDecision decision)
    {
        IReadOnlyList<RestSiteOption> options = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions();
        string? expected = ScriptDecisionReader.GetString(decision.Data, "option_id");
        for (int index = 0; index < options.Count; index++)
        {
            if (string.Equals(options[index].OptionId, expected, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static VerifyResult VerifyRest(ScriptDecision decision)
    {
        IReadOnlyList<RestSiteOption> options = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions();
        object offered = options.Select((option, index) => new { index, option_id = option.OptionId, enabled = option.IsEnabled }).ToArray();
        int index = RestOptionIndex(decision);
        if (index < 0)
        {
            return VerifyResult.Mismatch("the scripted rest option is not offered", decision.Data, offered);
        }

        return options[index].IsEnabled
            ? VerifyResult.Success
            : VerifyResult.Mismatch("the scripted rest option is disabled", decision.Data, offered);
    }

    private static MerchantInventory? LocalInventory()
    {
        RunState? state = LiveGame.State;
        Player? me = LiveGame.LocalPlayer;
        if (state?.CurrentRoom is not MerchantRoom room || me is null)
        {
            return null;
        }

        int slot = state.GetPlayerSlotIndex(me);
        return slot >= 0 && slot < room.Inventories.Count ? room.Inventories[slot] : null;
    }

    private static object EntryListing(MerchantInventory inventory) =>
        inventory.AllEntries.Select((entry, index) => new
        {
            inventory_index = index,
            entry_type = EntryType(entry),
            entry_group = EntryGroup(inventory, entry),
            entry_index = EntryIndex(inventory, entry),
            model_id = EntryModelId(entry),
            cost = entry.Cost,
            stocked = entry.IsStocked,
            affordable = entry.EnoughGold,
        }).ToArray();

    private static VerifyResult VerifyPurchase(ScriptDecision decision)
    {
        MerchantInventory inventory = LocalInventory()!;
        object offered = EntryListing(inventory);
        List<MerchantEntry> entries = inventory.AllEntries.ToList();
        if (!ScriptDecisionReader.TryGetInt(decision.Data, "inventory_index", out int index) || index < 0 || index >= entries.Count)
        {
            return VerifyResult.Mismatch("the scripted inventory index does not exist", decision.Data, offered);
        }

        MerchantEntry entry = entries[index];
        bool identityMatches =
            string.Equals(EntryType(entry), ScriptDecisionReader.GetString(decision.Data, "entry_type"), StringComparison.Ordinal)
            && string.Equals(EntryGroup(inventory, entry), ScriptDecisionReader.GetString(decision.Data, "entry_group"), StringComparison.Ordinal)
            && ScriptDecisionReader.TryGetInt(decision.Data, "entry_index", out int entryIndex) && entryIndex == EntryIndex(inventory, entry)
            && string.Equals(EntryModelId(entry), ScriptDecisionReader.GetString(decision.Data, "model_id"), StringComparison.Ordinal)
            && (!ScriptDecisionReader.TryGetInt(decision.Data, "cost", out int cost) || cost == entry.Cost);
        if (!identityMatches)
        {
            return VerifyResult.Mismatch("the merchant entry at the scripted index has a different identity", decision.Data, offered);
        }

        if (!entry.IsStocked || !entry.EnoughGold)
        {
            return VerifyResult.Mismatch("the merchant entry is out of stock or unaffordable", decision.Data, offered);
        }

        return VerifyResult.Success;
    }

    private static VerifyResult VerifyRemoval(ScriptDecision decision)
    {
        MerchantInventory inventory = LocalInventory()!;
        MerchantCardRemovalEntry? entry = inventory.CardRemovalEntry;
        object offered = new { removal = entry is null ? null : new { cost = entry.Cost, stocked = entry.IsStocked, affordable = entry.EnoughGold } };
        if (entry is null || !entry.IsStocked || !entry.EnoughGold)
        {
            return VerifyResult.Mismatch("card removal is not available", decision.Data, offered);
        }

        if (ScriptDecisionReader.TryGetInt(decision.Data, "cost", out int cost) && cost != entry.Cost)
        {
            return VerifyResult.Mismatch("card removal costs a different amount", decision.Data, offered);
        }

        return VerifyResult.Success;
    }

    private static string EntryType(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry => "card",
        MerchantRelicEntry => "relic",
        MerchantPotionEntry => "potion",
        MerchantCardRemovalEntry => "card_removal",
        _ => entry.GetType().Name,
    };

    private static string EntryGroup(MerchantInventory inventory, MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card => inventory.CharacterCardEntries.Contains(card) ? "character_card" : "colorless_card",
        MerchantRelicEntry => "relic",
        MerchantPotionEntry => "potion",
        _ => "card_removal",
    };

    private static int EntryIndex(MerchantInventory inventory, MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card => inventory.CharacterCardEntries.Contains(card)
            ? inventory.CharacterCardEntries.ToList().IndexOf(card)
            : inventory.ColorlessCardEntries.ToList().IndexOf(card),
        MerchantRelicEntry relic => inventory.RelicEntries.ToList().IndexOf(relic),
        MerchantPotionEntry potion => inventory.PotionEntries.ToList().IndexOf(potion),
        _ => 0,
    };

    private static string? EntryModelId(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card => card.CreationResult?.Card.Id.ToString(),
        MerchantRelicEntry relic => relic.Model?.Id.ToString(),
        MerchantPotionEntry potion => potion.Model?.Id.ToString(),
        _ => null,
    };
}
