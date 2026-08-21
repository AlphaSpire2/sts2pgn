namespace STS2PGN.Mod.Playback;

internal sealed class PlaybackTimeoutException(string message) : TimeoutException(message);

/// <summary>
/// Main-thread polling. STS2 runs its gameplay on the Godot main thread with a synchronization
/// context, so <c>await Task.Delay</c> resumes on the main thread like the game's own async code.
/// </summary>
internal static class PlaybackWait
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string description, CancellationToken cancellation)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            cancellation.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new PlaybackTimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {description}.");
            }

            await Task.Delay(PollInterval, cancellation);
        }
    }

    /// <summary>Waits for the condition; returns false instead of throwing on timeout.</summary>
    public static async Task<bool> TryUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellation)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            cancellation.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PollInterval, cancellation);
        }

        return true;
    }
}
