using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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
        "room.proceed" or "act.next" => LiveGame.TopOverlay is NRewardsScreen,
        _ => false,
    };

    public bool SettlesByObservation(ScriptDecision decision) => decision.Kind != "room.proceed";

    /// <summary>
    /// Leaving a rest site, shop, treasure room, or event shop is presentation-only (the recorder
    /// never writes it), so a script goes straight from the room's last decision to map.choose.
    /// Click the room's Proceed button the way a player would; the map then opens.
    /// </summary>
    public bool TryAdvanceSurface(ScriptDecision decision)
    {
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

        if (run.EventRoom is { } eventRoom && LiveGame.FindFirst<NFakeMerchant>(eventRoom) is { } fakeMerchant)
        {
            if (LiveGame.FindFirst<NMerchantInventory>(fakeMerchant) is { IsOpen: true })
            {
                return Click(LiveGame.FindAll<NBackButton>(fakeMerchant).FirstOrDefault(LiveGame.IsUsable));
            }

            return Click(LiveGame.FindFirst<NProceedButton>(fakeMerchant));
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
                _ = RunManager.Instance.ProceedFromTerminalRewardsScreen();
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
