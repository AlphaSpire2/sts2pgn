using System.Text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2PGN.Mod.Recording;

/// <summary>
/// Converts game-owned objects to the stable, game-independent values written to a trace.
/// Never expose STS2's default JSON representation here: enums and ModelId objects are not
/// a portable replay contract.
/// </summary>
internal static class PortablePayloads
{
    public static string Symbol(Enum value)
    {
        string name = value.ToString();
        StringBuilder result = new(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current) &&
                (char.IsLower(name[index - 1]) ||
                 (index + 1 < name.Length && char.IsLower(name[index + 1]))))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }

    public static string SymbolFromType(Type type, string prefix = "", string suffix = "")
    {
        string name = type.Name;
        if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal))
        {
            name = name[prefix.Length..];
        }

        if (suffix.Length > 0 && name.EndsWith(suffix, StringComparison.Ordinal))
        {
            name = name[..^suffix.Length];
        }

        return SymbolFromName(name);
    }

    public static string? ModelId(ModelId? id) =>
        id is null || id == MegaCrit.Sts2.Core.Models.ModelId.none ? null : id.ToString();

    public static object? CardFingerprint(CardModel? card)
    {
        if (card is null)
        {
            return null;
        }

        return card.IsMutable
            ? CardFingerprint(card.ToSerializable())
            : new { model_id = card.Id.ToString() };
    }

    public static object CardFingerprint(SerializableCard card) => new
    {
        model_id = ModelId(card.Id),
        upgrade_level = card.CurrentUpgradeLevel,
        enchantment = card.Enchantment is null ? null : new
        {
            model_id = ModelId(card.Enchantment.Id),
            amount = card.Enchantment.Amount,
            properties = SavedProperties(card.Enchantment.Props),
        },
        properties = SavedProperties(card.Props),
        floor_added_to_deck = card.FloorAddedToDeck,
    };

    public static object RewardFingerprint(Reward reward)
    {
        SerializableReward? save = null;
        try
        {
            save = reward.ToSerializable();
        }
        catch
        {
            // Some in-game CardReward variants deliberately cannot use the save schema.
            // Their already-generated offered cards below are still authoritative.
        }

        return new
        {
            reward_type = save is null ? RewardTypeName(reward) : Symbol(save.RewardType),
            model_id = RuntimeRewardModelId(reward) ?? ModelId(save?.PredeterminedModelId),
            special_card = save?.SpecialCard is null ? null : CardFingerprint(save.SpecialCard),
            gold_amount = save?.GoldAmount,
            was_gold_stolen_back = save?.WasGoldStolenBack,
            source = save is null ? null : Symbol(save.Source),
            rarity_odds = save is null ? null : Symbol(save.RarityOdds),
            card_pools = save?.CardPoolIds.Select(ModelId).ToArray(),
            option_count = save?.OptionCount,
            custom_description_encounter_source_id = ModelId(save?.CustomDescriptionEncounterSourceId),
            offered_cards = reward is CardReward cardReward
                ? cardReward.Cards.Select(CardFingerprint).ToArray()
                : null,
        };
    }

    private static string RewardTypeName(Reward reward) => reward switch
    {
        CardReward => "card",
        GoldReward => "gold",
        PotionReward => "potion",
        RelicReward => "relic",
        CardRemovalReward => "remove_card",
        SpecialCardReward => "special_card",
        _ => SymbolFromType(reward.GetType(), suffix: "Reward"),
    };

    private static string? RuntimeRewardModelId(Reward reward) => reward switch
    {
        PotionReward potion => potion.Potion?.Id.ToString(),
        RelicReward relic => relic.Relic?.Id.ToString(),
        _ => null,
    };

    private static string SymbolFromName(string name)
    {
        StringBuilder result = new(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current) && char.IsLower(name[index - 1]))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }

    private static object? SavedProperties(SavedProperties? properties)
    {
        if (properties is null)
        {
            return null;
        }

        return new
        {
            ints = properties.ints?.OrderBy(item => item.name, StringComparer.Ordinal)
                .Select(item => new { name = item.name, value = item.value }).ToArray(),
            bools = properties.bools?.OrderBy(item => item.name, StringComparer.Ordinal)
                .Select(item => new { name = item.name, value = item.value }).ToArray(),
            strings = properties.strings?.OrderBy(item => item.name, StringComparer.Ordinal)
                .Select(item => new { name = item.name, value = item.value }).ToArray(),
            int_arrays = properties.intArrays?.OrderBy(item => item.name, StringComparer.Ordinal)
                .Select(item => new { name = item.name, value = item.value }).ToArray(),
            model_ids = properties.modelIds?.OrderBy(item => item.name, StringComparer.Ordinal)
                .Select(item => new { name = item.name, value = ModelId(item.value) }).ToArray(),
            cards = properties.cards?.OrderBy(item => item.name, StringComparer.Ordinal)
                .Select(item => new { name = item.name, value = CardFingerprint(item.value) }).ToArray(),
            card_arrays = properties.cardArrays?.OrderBy(item => item.name, StringComparer.Ordinal)
                .Select(item => new
                {
                    name = item.name,
                    value = item.value.Select(CardFingerprint).ToArray(),
                }).ToArray(),
        };
    }
}
