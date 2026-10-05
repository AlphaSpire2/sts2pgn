using System.Text.Json;

namespace STS2PGN.Scripting;

public static class PostVictoryDecisions
{
    public static bool IsPresentation(string kind, JsonElement data) =>
        kind is "act.next" or "room.proceed"
        || (kind is "event.choose" or "event.advance"
            && ScriptDecisionReader.GetString(data, "event_id") == "EVENT.THE_ARCHITECT");
}
