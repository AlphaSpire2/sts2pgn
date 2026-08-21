using System.Text.Json;

namespace STS2PGN.Scripting;

/// <summary>A selected card reference: a positional index plus its portable fingerprint.</summary>
public sealed record ScriptCardSelection(int? DeckIndex, int? CombatCardIndex, JsonElement Fingerprint);

/// <summary>A combat target reference: combat creature ID plus model fingerprint.</summary>
public sealed record ScriptTarget(uint CombatId, string ModelId);

/// <summary>
/// Small, allocation-light accessors over decision payloads. Shared by the playback driver and tests
/// so both read the same fields the same way.
/// </summary>
public static class ScriptDecisionReader
{
    public static bool TryGetInt(JsonElement data, string property, out int value)
    {
        value = 0;
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    public static bool TryGetBool(JsonElement data, string property, out bool value)
    {
        value = false;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(property, out JsonElement element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    public static string? GetString(JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>Returns the <c>offered</c> block's context and options, when present.</summary>
    public static bool TryGetOffered(JsonElement data, out string context, out JsonElement options)
    {
        context = string.Empty;
        options = default;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("offered", out JsonElement offered)
            || offered.ValueKind != JsonValueKind.Object
            || !offered.TryGetProperty("context", out JsonElement contextElement)
            || contextElement.ValueKind != JsonValueKind.String
            || !offered.TryGetProperty("options", out options))
        {
            return false;
        }

        context = contextElement.GetString()!;
        return true;
    }

    /// <summary>Selected indexes of a <c>choice.index</c> record; empty when absent.</summary>
    public static IReadOnlyList<int> GetSelectedIndexes(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("indexes", out JsonElement indexes)
            || indexes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<int> result = [];
        foreach (JsonElement index in indexes.EnumerateArray())
        {
            if (index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out int value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>Selected cards of a <c>choice.cards</c> record; empty when absent.</summary>
    public static IReadOnlyList<ScriptCardSelection> GetCardSelections(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("cards", out JsonElement cards)
            || cards.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ScriptCardSelection> result = [];
        foreach (JsonElement card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int? deckIndex = TryGetInt(card, "deck_index", out int deck) ? deck : null;
            int? combatIndex = TryGetInt(card, "combat_card_index", out int combat) ? combat : null;
            JsonElement fingerprint = card.TryGetProperty("fingerprint", out JsonElement element) ? element : card;
            result.Add(new ScriptCardSelection(deckIndex, combatIndex, fingerprint));
        }

        return result;
    }

    /// <summary>The combat target of a play/potion record, or null for an untargeted action.</summary>
    public static ScriptTarget? GetTarget(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("target", out JsonElement target)
            || target.ValueKind != JsonValueKind.Object
            || !target.TryGetProperty("combat_id", out JsonElement combatId)
            || !combatId.TryGetUInt32(out uint id))
        {
            return null;
        }

        string? modelId = GetString(target, "model_id");
        return modelId is null ? null : new ScriptTarget(id, modelId);
    }
}
