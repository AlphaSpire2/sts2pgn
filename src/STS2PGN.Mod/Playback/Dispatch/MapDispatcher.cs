using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>map.choose, room.proceed, act.next.</summary>
internal sealed class MapDispatcher : IDecisionDispatcher
{
    public bool Handles(string kind) => kind is "map.choose" or "room.proceed" or "act.next";

    public bool IsReady(ScriptDecision decision) => decision.Kind switch
    {
        "map.choose" => NMapScreen.Instance is { IsOpen: true } screen && TravelablePoints(screen).Count > 0,
        "room.proceed" => LiveGame.TopOverlay is NRewardsScreen || TreasureProceedButton() is not null,
        "act.next" => LiveGame.TopOverlay is NRewardsScreen,
        _ => false,
    };

    /// <summary>
    /// The treasure room records room.proceed from its own Proceed button (no rewards screen):
    /// enabled with the Proceed label once relic picking has finished, Skip-labelled while it is
    /// still open (that click would be treasure.skip_relic instead).
    /// </summary>
    private static NProceedButton? TreasureProceedButton()
    {
        NProceedButton? button = NRun.Instance?.TreasureRoom?.ProceedButton;
        return LiveGame.TopOverlay is null && LiveGame.IsUsable(button) && !button!.IsSkip ? button : null;
    }

    public bool SettlesByObservation(ScriptDecision decision) => decision.Kind != "room.proceed";

    /// <summary>
    /// Leaving a rest site, shop, treasure room, event shop, or finished event is presentation-only
    /// (the recorder never writes it), so a script goes straight from the room's last decision to map.choose.
    /// Click the room's Proceed button the way a player would; the map then opens.
    /// </summary>
    public bool TryAdvanceSurface(ScriptDecision decision)
    {
        // A treasure room is left by its own Proceed button, and that button is
        // disabled until the chest has been opened. Where the chest holds a relic
        // the script says so and the reward dispatcher opens it; where a relic has
        // suppressed the chest there is nothing to pick, so the script goes from
        // entering the room straight to proceeding and nothing else would open it.
        if (decision.Kind == "room.proceed")
        {
            return TreasureChest.OpenIfClosed();
        }

        if (decision.Kind == "map.choose" && CrystalSphereDispatcher.TryProceedFinished())
        {
            return true;
        }

        if (decision.Kind != "map.choose" || NMapScreen.Instance is { IsOpen: true } || LiveGame.TopOverlay is not null)
        {
            return false;
        }

        NRun? run = NRun.Instance;
        if (run is null)
        {
            return false;
        }

        if (run.RestSiteRoom is { } rest && Click(rest.ProceedButton))
        {
            return true;
        }

        if (run.TreasureRoom is { } treasure && Click(treasure.ProceedButton))
        {
            return true;
        }

        if (run.MerchantRoom is { } merchant)
        {
            if (merchant.Inventory is { IsOpen: true })
            {
                return Click(LiveGame.FindAll<NBackButton>(merchant).FirstOrDefault(LiveGame.IsUsable));
            }

            if (Click(merchant.ProceedButton))
            {
                return true;
            }
        }

        if (run.EventRoom is { } eventRoom)
        {
            if (LiveGame.FindFirst<NFakeMerchant>(eventRoom) is { } fakeMerchant)
            {
                if (LiveGame.FindFirst<NMerchantInventory>(fakeMerchant) is { IsOpen: true })
                {
                    return Click(LiveGame.FindAll<NBackButton>(fakeMerchant).FirstOrDefault(LiveGame.IsUsable));
                }

                return Click(LiveGame.FindFirst<NProceedButton>(fakeMerchant));
            }

            // A finished event shows a synthetic PROCEED option (NEventRoom.SetOptions) whose only
            // effect is opening the map; the recorder ignores it (RecordEventChoice returns on
            // IsFinished), so the script goes straight from the event's last choice to map.choose.
            return Click(LiveGame.FindAll<NEventOptionButton>(eventRoom).FirstOrDefault(button => button.Option is { IsProceed: true }));
        }

        return false;
    }

    private static bool Click(NClickableControl? control)
    {
        if (!LiveGame.IsUsable(control))
        {
            return false;
        }

        control!.ForceClick();
        return true;
    }

    public VerifyResult Verify(ScriptDecision decision)
    {
        switch (decision.Kind)
        {
            case "map.choose":
            {
                List<NMapPoint> points = TravelablePoints(NMapScreen.Instance!);
                object offered = points.Select(point => new { col = point.Point.coord.col, row = point.Point.coord.row }).ToArray();
                return FindPoint(points, decision) is null
                    ? VerifyResult.Mismatch("the scripted map coordinate is not travelable", decision.Data, offered)
                    : VerifyResult.Success;
            }

            case "room.proceed":
            {
                string? model = LiveGame.State?.CurrentRoom?.ModelId?.Entry;
                return model == "CRYSTAL_SPHERE"
                    ? VerifyResult.Mismatch("room.proceed is not recorded for the Crystal Sphere room", decision.Data, new { room_model = model })
                    : VerifyResult.Success;
            }

            default:
                return VerifyResult.Success;
        }
    }

    public Task SubmitAsync(ScriptDecision decision)
    {
        switch (decision.Kind)
        {
            case "map.choose":
                NMapScreen screen = NMapScreen.Instance!;
                screen.OnMapPointSelectedLocally(FindPoint(TravelablePoints(screen), decision)!);
                break;
            case "room.proceed":
                if (TreasureProceedButton() is { } treasureProceed)
                {
                    treasureProceed.ForceClick();
                }
                else
                {
                    _ = RunManager.Instance.ProceedFromTerminalRewardsScreen();
                }

                break;
            case "act.next":
                RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
                break;
        }

        return Task.CompletedTask;
    }

    private static List<NMapPoint> TravelablePoints(NMapScreen screen) =>
        LiveGame.FindAll<NMapPoint>(screen)
            .Where(point => point.State == MapPointState.Travelable && point.Point is not null)
            .ToList();

    private static NMapPoint? FindPoint(List<NMapPoint> points, ScriptDecision decision)
    {
        if (!ScriptDecisionReader.TryGetInt(decision.Data, "col", out int col)
            || !ScriptDecisionReader.TryGetInt(decision.Data, "row", out int row))
        {
            return null;
        }

        return points.FirstOrDefault(point => point.Point.coord.col == col && point.Point.coord.row == row);
    }
}
