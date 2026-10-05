using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;

namespace STS2PGN.Mod.Playback;

/// <summary>Completes the game's victory UI/history after recording has ended.
/// No input or observation here participates in gameplay conformance.</summary>
internal static class PostVictoryPresentation
{
    private static readonly AccessTools.FieldRef<EventSynchronizer, List<Task>> PendingOptionTasks =
        AccessTools.FieldRefAccess<EventSynchronizer, List<Task>>("_pendingOptionTasks");

    public static async Task CompleteAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        RunManager manager = RunManager.Instance;
        if (manager.IsGameOver || !manager.IsInProgress)
        {
            return;
        }
        manager.ActChangeSynchronizer.SetLocalPlayerReady();
        await PlaybackWait.UntilAsync(
            () => manager.IsGameOver || manager.EventSynchronizer.Events.FirstOrDefault() is TheArchitect,
            timeout, "post-victory presentation", cancellation);
        while (!manager.IsGameOver && manager.IsInProgress)
        {
            EventModel? current = null;
            await PlaybackWait.UntilAsync(() => manager.IsGameOver
                || ((current = manager.EventSynchronizer.Events.FirstOrDefault()) is TheArchitect
                    && current.CurrentOptions.Count == 1
                    && !current.CurrentOptions[0].IsLocked
                    && PendingOptionTasks(manager.EventSynchronizer).All(task => task.IsCompleted)),
                timeout, "victory presentation to become ready", cancellation);
            if (manager.IsGameOver)
            {
                break;
            }
            var option = current!.CurrentOptions[0];
            manager.EventSynchronizer.ChooseLocalOption(0);
            await PlaybackWait.UntilAsync(() => manager.IsGameOver
                || !current.CurrentOptions.Contains(option),
                timeout, "victory presentation to advance", cancellation);
        }
    }
}
