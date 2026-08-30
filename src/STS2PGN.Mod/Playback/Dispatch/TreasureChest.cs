using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>
/// The one click a treasure room needs that no script carries.
/// </summary>
internal static class TreasureChest
{
    /// <summary>
    /// A treasure room does nothing until its chest is opened: the gold is paid
    /// from the opening, the relics are put on the screen by it, and the Proceed
    /// button stays disabled until it has happened. The click is not a decision —
    /// the recorder writes nothing for it — so playback has to make it itself.
    ///
    /// A chest holding nothing is no exception. A relic that suppresses the chest
    /// leaves the room standing with an empty chest and no relic to pick, and the
    /// room is still left by opening it and then proceeding.
    /// </summary>
    /// <returns>True when this call opened the chest.</returns>
    public static bool OpenIfClosed()
    {
        if (LiveGame.TopOverlay is not null
            || NRun.Instance?.TreasureRoom is not { } room
            || room.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection") is { Visible: true })
        {
            return false;
        }

        NButton? chest = room.GetNodeOrNull<NButton>("%Chest");
        if (!LiveGame.IsUsable(chest))
        {
            return false;
        }

        chest!.ForceClick();
        return true;
    }
}
