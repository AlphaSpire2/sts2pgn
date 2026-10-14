using System.Text.Json;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback;

internal sealed record PlaybackActionRow(int Position, string Description);

internal static class PlaybackStepContext
{
    public const int Radius = 5;

    public static IReadOnlyList<PlaybackActionRow> Window(IReadOnlyList<string> actions, int position)
    {
        if (position < 1 || position > actions.Count)
        {
            return [];
        }

        List<PlaybackActionRow> rows = [];
        for (int index = Math.Max(0, position - 1 - Radius);
            index < Math.Min(actions.Count, position + Radius); index++)
        {
            rows.Add(new(index + 1, actions[index]));
        }
        return rows;
    }

    public static string Describe(string kind, JsonElement data)
    {
        string title = kind switch
        {
            "combat.play_card" => "Play card",
            "combat.end_turn" => "End turn",
            "combat.use_potion" => "Use potion",
            "combat.discard_potion" => "Discard potion",
            "map.choose" => "Travel to next room",
            "event.choose" => "Choose event option",
            "event.advance" => "Advance dialogue",
            "event.crystal_sphere.set_tool" => "Choose sphere tool",
            "event.crystal_sphere.click" => "Reveal sphere cell",
            "event.crystal_sphere.proceed" => "Leave sphere",
            "rest.choose" => "Choose rest action",
            "shop.purchase" => "Buy item",
            "shop.remove_card" => "Remove card",
            "reward.claim" => "Claim reward",
            "reward.skip" => "Skip rewards",
            "treasure.choose_relic" => "Choose relic",
            "choice.index" or "choice.cards" => "Answer selection",
            "room.proceed" => "Leave room",
            _ => kind,
        };
        string? detail = kind switch
        {
            "combat.play_card" => ScriptDecisionReader.GetString(data, "card_model"),
            "combat.use_potion" or "combat.discard_potion" => ScriptDecisionReader.GetString(data, "potion_model"),
            "event.choose" or "rest.choose" => ScriptDecisionReader.GetString(data, "option_id"),
            "shop.purchase" => ScriptDecisionReader.GetString(data, "model_id"),
            "treasure.choose_relic" => ScriptDecisionReader.GetString(data, "relic_model"),
            "event.crystal_sphere.set_tool" => ScriptDecisionReader.GetString(data, "tool"),
            "map.choose" => Coordinates(data, "col", "row"),
            "event.crystal_sphere.click" => Coordinates(data, "x", "y"),
            "choice.index" => string.Join(", ", ScriptDecisionReader.GetSelectedIndexes(data)),
            "choice.cards" => string.Join(", ", ScriptDecisionReader.GetCardSelections(data)
                .Select(card => ScriptDecisionReader.GetString(card.Fingerprint, "model_id") ?? "card")),
            "reward.claim" => ScriptDecisionReader.TryGetInt(data, "reward_index", out int reward) ? $"reward {reward}" : null,
            _ => null,
        };
        string description = string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}";
        if (kind is "combat.play_card" or "combat.use_potion"
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("target", out JsonElement target)
            && ScriptDecisionReader.GetString(target, "model_id") is { } model)
        {
            description += $" → {model}";
        }

        // A payload's text must stay on one row of the compact action list.
        return string.Join(" ", description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Coordinates(JsonElement data, string x, string y) =>
        ScriptDecisionReader.TryGetInt(data, x, out int column)
        && ScriptDecisionReader.TryGetInt(data, y, out int row) ? $"({column}, {row})" : null;
}
