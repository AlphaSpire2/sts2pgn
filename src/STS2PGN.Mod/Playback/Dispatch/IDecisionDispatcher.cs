using System.Text.Json;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>Result of comparing a scripted decision with what the game currently offers.</summary>
internal sealed record VerifyResult(bool Ok, string Message, JsonElement? Expected, JsonElement? Offered)
{
    public static readonly VerifyResult Success = new(true, string.Empty, null, null);

    public static VerifyResult Mismatch(string message, object? expected, object? offered) =>
        new(false, message, expected is null ? null : LiveGame.ToJson(expected), offered is null ? null : LiveGame.ToJson(offered));
}

/// <summary>
/// One family of decisions. The driver calls <see cref="IsReady"/> until true (with the game idle),
/// then <see cref="Verify"/>, then <see cref="SubmitAsync"/>, then waits for the recorder.
/// </summary>
internal interface IDecisionDispatcher
{
    bool Handles(string kind);

    /// <summary>True when the surface this decision acts on is present. Must not throw.</summary>
    bool IsReady(ScriptDecision decision);

    /// <summary>Compares the script's identity with the live offer. Never mutates game state.</summary>
    VerifyResult Verify(ScriptDecision decision);

    /// <summary>Submits through the same game-code path a player's input reaches.</summary>
    Task SubmitAsync(ScriptDecision decision);

    /// <summary>
    /// Whether the recorder emits a completion observation for this decision. When false the driver
    /// treats the decision as settled once it is recorded and relies on the next decision's readiness.
    /// </summary>
    bool SettlesByObservation(ScriptDecision decision) => true;
}
