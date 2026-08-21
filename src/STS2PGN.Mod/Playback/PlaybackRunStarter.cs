using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// Starts and ends runs the way the game's own screens do, without the screens.
/// </summary>
internal static class PlaybackRunStarter
{
    public const string MainMenuNodePath = "/root/Game/RootSceneContainer/MainMenu";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(90);

    /// <summary>True once the main menu scene is the current scene and visible.</summary>
    public static bool IsMainMenuReady()
    {
        NGame? game = NGame.Instance;
        if (game is null || game.MainMenu is null)
        {
            return false;
        }

        Node? node = game.GetNodeOrNull(MainMenuNodePath);
        return node is Control { Visible: true } && !RunManager.Instance.IsInProgress;
    }

    public static Task WaitForMainMenuAsync(CancellationToken cancellation) =>
        PlaybackWait.UntilAsync(IsMainMenuReady, MenuTimeout, "the main menu", cancellation);

    /// <summary>
    /// The same call the custom-run screen's confirm button makes
    /// (<c>NCustomRunScreen.StartNewSingleplayerRun</c>), with the acts pre-flight already generated.
    /// </summary>
    public static async Task StartAsync(PreflightResult preflight, CancellationToken cancellation)
    {
        string seed = SeedHelper.CanonicalizeSeed(preflight.Script!.Identity.Seed);
        NGame game = NGame.Instance ?? throw new InvalidOperationException("NGame is not available.");
        game.DebugSeedOverride = null;
        await game.StartNewSingleplayerRun(
            preflight.Character!,
            shouldSave: true,
            preflight.Acts!,
            modifiers: Array.Empty<ModifierModel>(),
            seed,
            GameMode.Custom,
            preflight.Script.Identity.Ascension);
        await PlaybackWait.UntilAsync(
            () => RunManager.Instance.IsInProgress && RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is not null,
            StartTimeout,
            "the run to enter its first room",
            cancellation);
    }

    /// <summary>
    /// Ends whatever the script left behind and returns to the main menu. A run still in progress is
    /// abandoned through the game's own path (the recorder finalizes the trace as ABANDONED and the
    /// game removes its save); a finished run is simply left.
    /// </summary>
    public static async Task ReturnToMainMenuAsync(CancellationToken cancellation)
    {
        RunManager manager = RunManager.Instance;
        if (manager.IsInProgress && !manager.IsGameOver && !manager.IsAbandoned)
        {
            manager.Abandon();
            await PlaybackWait.TryUntilAsync(
                () => !manager.IsInProgress || manager.IsGameOver || manager.IsAbandoned,
                StartTimeout,
                cancellation);
            // Let the game-over flow finish writing history and deleting the save before leaving.
            await PlaybackWait.TryUntilAsync(() => !SaveManager.Instance.HasRunSave, TimeSpan.FromSeconds(15), cancellation);
        }

        NGame game = NGame.Instance ?? throw new InvalidOperationException("NGame is not available.");
        if (game.MainMenu is null)
        {
            await game.ReturnToMainMenuAfterRun();
        }

        await WaitForMainMenuAsync(cancellation);
        if (SaveManager.Instance.HasRunSave)
        {
            // The run we started is over; a leftover save would make the next pre-flight refuse.
            GD.Print("[STS2PGN] Playback: removing the finished playback run's save");
            SaveManager.Instance.DeleteCurrentRun();
        }
    }
}
