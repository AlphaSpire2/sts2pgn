using Godot;

namespace STS2PGN.Mod.Playback;

internal sealed class PlaybackTimeoutException(string message) : TimeoutException(message);

/// <summary>
/// Main-thread polling. STS2 runs its gameplay on the Godot main thread with a synchronization
/// context, so <c>await Task.Delay</c> resumes on the main thread like the game's own async code.
///
/// Under <see cref="PlaybackSpeed"/> the poll steps on the scene tree's process frame instead of a
/// fixed 50 ms, so the driver keeps pace with a game whose frames no longer arrive on a wall-clock
/// schedule. Timeouts stay on the wall clock: their job is to end a batch whose game has stopped.
/// Presentation waits are neither of these — see <see cref="PresentationDelay"/>.
/// </summary>
internal static class PlaybackWait
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string description, CancellationToken cancellation)
    {
        if (!await TryUntilAsync(condition, timeout, cancellation))
        {
            throw new PlaybackTimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {description}.");
        }
    }

    /// <summary>Waits for the condition; returns false instead of throwing on timeout.</summary>
    public static async Task<bool> TryUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (condition())
        {
            return true;
        }

        // One timer for the whole wait. It is also the stall guard: if the scene tree stops
        // delivering frames, the race in PollAsync still completes and the deadline check below
        // ends the wait instead of hanging an unattended batch forever.
        Task expiry = Task.Delay(timeout, CancellationToken.None);
        while (true)
        {
            await PollAsync(expiry);
            if (condition())
            {
                return true;
            }

            cancellation.ThrowIfCancellationRequested();
            if (expiry.IsCompleted)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// One poll step: the scene tree's next process frame in fast mode, otherwise
    /// <see cref="PollInterval"/>. <paramref name="guard"/> bounds the wait when the caller already
    /// owns a deadline; without one the poll interval bounds it.
    /// </summary>
    public static async Task PollAsync(Task? guard = null)
    {
        if (PlaybackSpeed.FramePolling && Engine.GetMainLoop() is SceneTree tree)
        {
            await Task.WhenAny(NextFrameAsync(tree), guard ?? Task.Delay(PollInterval, CancellationToken.None));
            return;
        }

        Task interval = Task.Delay(PollInterval, CancellationToken.None);
        await (guard is null ? interval : Task.WhenAny(interval, guard));
    }

    private static async Task NextFrameAsync(SceneTree tree) =>
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
}
