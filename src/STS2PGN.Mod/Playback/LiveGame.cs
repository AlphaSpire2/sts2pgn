using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Serialization;

namespace STS2PGN.Mod.Playback;

/// <summary>Read-only views of the live game shared by the dispatchers.</summary>
internal static class LiveGame
{
    public static RunState? State => RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;

    public static CombatState? Combat => CombatManager.Instance.IsInProgress ? CombatManager.Instance.DebugOnlyGetState() : null;

    public static Player? LocalPlayer
    {
        get
        {
            RunState? state = State;
            return state is null ? null : LocalContext.GetMe((IPlayerCollection)state) ?? state.Players.FirstOrDefault();
        }
    }

    /// <summary>No action running or queued and no modal dialog open.</summary>
    public static bool IsIdle =>
        RunManager.Instance.IsInProgress
        && RunManager.Instance.ActionQueueSet.IsEmpty
        && !RunManager.Instance.ActionExecutor.IsRunning
        && NModalContainer.Instance?.OpenModal is null;

    public static bool NoModalOpen => RunManager.Instance.IsInProgress && NModalContainer.Instance?.OpenModal is null;

    public static IOverlayScreen? TopOverlay => NOverlayStack.Instance?.Peek();

    public static List<T> FindAll<T>(Node? start) where T : Node
    {
        List<T> found = [];
        if (start is not null)
        {
            Collect(start, found);
        }

        return found;
    }

    public static T? FindFirst<T>(Node? start) where T : Node => FindAll<T>(start).FirstOrDefault();

    private static void Collect<T>(Node node, List<T> found) where T : Node
    {
        if (node is T match)
        {
            found.Add(match);
        }

        foreach (Node child in node.GetChildren())
        {
            Collect(child, found);
        }
    }

    public static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

    public static bool SameJson(JsonElement expected, JsonElement live)
    {
        try
        {
            return string.Equals(CanonicalJson.Serialize(expected), CanonicalJson.Serialize(live), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Canonical-JSON equality between a script fingerprint and a live fingerprint object.</summary>
    public static bool FingerprintMatches(JsonElement expected, object? live) =>
        expected.ValueKind != JsonValueKind.Undefined && SameJson(expected, ToJson(live));

    public static bool IsUsable(NClickableControl? control) =>
        control is not null && control.IsVisibleInTree() && control.IsEnabled;
}
