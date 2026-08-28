using Godot;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// "Long enough for the game to finish showing something." The game guards those moments on two
/// different clocks, so this waits for both.
///
/// A screen's entry animation is a tween, advanced by the process delta — game time. But
/// <c>NChooseACardSelectionScreen.SelectHolder</c> ignores presses until
/// <c>Time.GetTicksMsec() - _openedTicks > 350</c>, which is real time. Under <c>--fixed-fps</c>
/// game time outruns the wall clock, so a game-time-only wait satisfies the tween and lands inside
/// that 350 ms window, where the press is silently dropped and the batch hangs on a screen that
/// never closes. A wall-clock-only wait has the opposite failure if game time ever runs slower.
///
/// Requiring both is what makes a presentation wait mean the same thing at any speed. It also puts
/// a floor under the batch: each such wait costs its full duration in real time no matter how fast
/// the game is running.
/// </summary>
internal sealed class PresentationDelay
{
    private readonly DateTime _wallDeadline;
    private readonly bool _hasTimer;
    private bool _gameTimeElapsed;

    private PresentationDelay(TimeSpan duration)
    {
        _wallDeadline = DateTime.UtcNow + duration;
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        // The tree holds the timer until it fires, and the handler holds this instance.
        _hasTimer = true;
        tree.CreateTimer(duration.TotalSeconds).Timeout += () => _gameTimeElapsed = true;
    }

    public static PresentationDelay Start(TimeSpan duration) => new(duration);

    /// <summary>True once the duration has passed on both clocks (or on the wall clock alone if
    /// there is no scene tree to measure game time with).</summary>
    public bool HasElapsed => (_gameTimeElapsed || !_hasTimer) && DateTime.UtcNow >= _wallDeadline;
}
