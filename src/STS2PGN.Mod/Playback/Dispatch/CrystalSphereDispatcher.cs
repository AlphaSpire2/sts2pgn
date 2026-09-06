using HarmonyLib;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>Crystal Sphere tool changes, logical cell clicks, and the room's real Proceed button.</summary>
internal sealed class CrystalSphereDispatcher : IDecisionDispatcher
{
    private static readonly AccessTools.FieldRef<NCrystalSphereScreen, CrystalSphereMinigame> Entity =
        AccessTools.FieldRefAccess<NCrystalSphereScreen, CrystalSphereMinigame>("_entity");

    public bool Handles(string kind) => kind is
        "event.crystal_sphere.click" or
        "event.crystal_sphere.proceed" or
        "event.crystal_sphere.set_tool";

    public bool IsReady(ScriptDecision decision)
    {
        if (LiveGame.TopOverlay is not NCrystalSphereScreen screen)
        {
            return false;
        }

        CrystalSphereMinigame minigame = Entity(screen);
        return decision.Kind switch
        {
            "event.crystal_sphere.click" or "event.crystal_sphere.set_tool" =>
                !minigame.IsFinished
                && LiveGame.IsUsable(ToolButton(screen, ScriptDecisionReader.GetString(decision.Data, "tool"))),
            "event.crystal_sphere.proceed" => LiveGame.IsUsable(ProceedButton(screen)),
            _ => false,
        };
    }

    public VerifyResult Verify(ScriptDecision decision) => decision.Kind switch
    {
        "event.crystal_sphere.click" => VerifyClick(decision),
        "event.crystal_sphere.set_tool" => VerifyTool(decision),
        "event.crystal_sphere.proceed" => VerifyResult.Success,
        _ => VerifyResult.Mismatch($"unsupported kind {decision.Kind}", null, null),
    };

    public Task SubmitAsync(ScriptDecision decision)
    {
        NCrystalSphereScreen screen = (NCrystalSphereScreen)LiveGame.TopOverlay!;
        switch (decision.Kind)
        {
            case "event.crystal_sphere.set_tool":
                ToolButton(screen, ScriptDecisionReader.GetString(decision.Data, "tool"))!.ForceClick();
                break;
            case "event.crystal_sphere.click":
            {
                CrystalSphereMinigame minigame = Entity(screen);
                string? tool = ScriptDecisionReader.GetString(decision.Data, "tool");
                if (!string.Equals(ToolName(minigame.CrystalSphereTool), tool, StringComparison.Ordinal))
                {
                    // A click carries its tool, so it remains self-contained even when a producer
                    // omits the redundant set_tool record.
                    ToolButton(screen, tool)!.ForceClick();
                }

                ScriptDecisionReader.TryGetInt(decision.Data, "x", out int x);
                ScriptDecisionReader.TryGetInt(decision.Data, "y", out int y);
                Cell(screen, x, y)!.ForceClick();
                break;
            }
            case "event.crystal_sphere.proceed":
                ProceedButton(screen)!.ForceClick();
                break;
        }

        return Task.CompletedTask;
    }

    public bool SettlesByObservation(ScriptDecision decision) =>
        decision.Kind != "event.crystal_sphere.proceed";

    /// <summary>
    /// A finished board can remain over the event after its rewards settle.
    /// Producers may go straight to map.choose because this button only leaves
    /// the completed minigame; the recorder still captures the real click.
    /// </summary>
    public static bool TryProceedFinished()
    {
        if (LiveGame.TopOverlay is not NCrystalSphereScreen screen
            || !Entity(screen).IsFinished
            || !LiveGame.IsUsable(ProceedButton(screen)))
        {
            return false;
        }

        ProceedButton(screen)!.ForceClick();
        return true;
    }

    private static VerifyResult VerifyTool(ScriptDecision decision)
    {
        NCrystalSphereScreen screen = (NCrystalSphereScreen)LiveGame.TopOverlay!;
        string? tool = ScriptDecisionReader.GetString(decision.Data, "tool");
        return LiveGame.IsUsable(ToolButton(screen, tool))
            ? VerifyResult.Success
            : VerifyResult.Mismatch("the scripted Crystal Sphere tool is not available", decision.Data, Offered(screen));
    }

    private static VerifyResult VerifyClick(ScriptDecision decision)
    {
        NCrystalSphereScreen screen = (NCrystalSphereScreen)LiveGame.TopOverlay!;
        CrystalSphereMinigame minigame = Entity(screen);
        ScriptDecisionReader.TryGetInt(decision.Data, "x", out int x);
        ScriptDecisionReader.TryGetInt(decision.Data, "y", out int y);
        NCrystalSphereCell? cell = Cell(screen, x, y);
        string? tool = ScriptDecisionReader.GetString(decision.Data, "tool");
        bool remainingMatches = ScriptDecisionReader.TryGetInt(decision.Data, "remaining_before", out int remaining)
            && remaining == minigame.DivinationCount;
        if (!LiveGame.IsUsable(cell)
            || !cell!.Entity.IsHidden
            || !LiveGame.IsUsable(ToolButton(screen, tool))
            || !remainingMatches)
        {
            return VerifyResult.Mismatch(
                "the scripted Crystal Sphere click does not match the live cell, tool, or remaining divinations",
                decision.Data,
                Offered(screen));
        }

        return VerifyResult.Success;
    }

    private static object Offered(NCrystalSphereScreen screen)
    {
        CrystalSphereMinigame minigame = Entity(screen);
        return new
        {
            tool = ToolName(minigame.CrystalSphereTool),
            remaining_before = minigame.DivinationCount,
            hidden_cells = LiveGame.FindAll<NCrystalSphereCell>(screen)
                .Where(cell => cell.Entity.IsHidden)
                .Select(cell => new { x = cell.Entity.X, y = cell.Entity.Y })
                .ToArray(),
        };
    }

    private static NCrystalSphereCell? Cell(NCrystalSphereScreen screen, int x, int y) =>
        LiveGame.FindAll<NCrystalSphereCell>(screen)
            .FirstOrDefault(cell => cell.Entity.X == x && cell.Entity.Y == y);

    private static NDivinationButton? ToolButton(NCrystalSphereScreen screen, string? tool) => tool switch
    {
        "small" => screen.GetNodeOrNull<NDivinationButton>("%SmallDivinationButton"),
        "big" => screen.GetNodeOrNull<NDivinationButton>("%BigDivinationButton"),
        _ => null,
    };

    private static NProceedButton? ProceedButton(NCrystalSphereScreen screen) =>
        screen.GetNodeOrNull<NProceedButton>("%ProceedButton");

    private static string ToolName(CrystalSphereMinigame.CrystalSphereToolType tool) => tool switch
    {
        CrystalSphereMinigame.CrystalSphereToolType.Small => "small",
        CrystalSphereMinigame.CrystalSphereToolType.Big => "big",
        _ => "none",
    };
}
