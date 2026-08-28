using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// Individually selectable because they are not equally safe, and only one of them is on by
/// default. The real speedup does not come from here at all: it comes from launching the game with
/// <c>--headless --fixed-fps 60</c>, which leaves every wait in place but lets game time outrun the
/// wall clock. <see cref="UncappedFrameRate"/> is what allows that to happen; the rest were measured
/// and rejected. See the fast-playback section of README.md for the numbers.
/// </summary>
[Flags]
internal enum PlaybackSpeedLevers
{
    None = 0,

    /// <summary>
    /// The game's own unattended mode: skips every gameplay wait, sound effect, and combat pause,
    /// and takes the straight-line branch through <c>ActionExecutor</c>. Measured worth ~1% once the
    /// frame rate is uncapped, so it is not worth being the only lever that changes control flow.
    /// </summary>
    NonInteractive = 1,

    /// <summary>
    /// Instant fast mode. Fast, but it removes waits rather than shortening them: the
    /// <c>room.enter</c> observation for a combat room is then captured after combat has started
    /// (phase=start, energy=3) instead of before it (phase=none, energy=0). Use
    /// <c>--fixed-fps</c> instead, which is just as fast and leaves the trace identical.
    /// </summary>
    InstantPresentation = 2,

    /// <summary>
    /// Let the main loop run as fast as the machine allows instead of the player's FPS limit.
    /// Headless only; a visible window would render every one of those frames. Trace-identical, and
    /// a precondition for <c>--fixed-fps</c> doing anything.
    /// </summary>
    UncappedFrameRate = 4,

    /// <summary>
    /// Poll the driver on the scene tree's process frame instead of the 50 ms wall-clock interval.
    /// KNOWN UNSAFE, kept only so the result stays reproducible: the driver then observes and acts
    /// during <c>ProcessFrame</c> emission rather than where the synchronization context is pumped,
    /// which catches the game mid-transition. Observed dropping a shop's <c>room.enter</c> entirely,
    /// writing a null-payload <c>room.exit</c>, recording the next <c>room.enter</c> twice, and
    /// hanging the batch in a shop. Never include it in a batch whose trace you intend to trust.
    /// </summary>
    FramePolling = 8,

    /// <summary>Every lever, including the ones that change recorded output.</summary>
    All = NonInteractive | InstantPresentation | UncappedFrameRate | FramePolling,
}

/// <summary>
/// Playback is unattended, so the game never has to present anything to a player. This asks it to
/// stop trying, for the duration of a batch only; a handoff restores the player's game.
///
/// Speed is not part of the replay contract — the driver advances on recorder signals, never on its
/// own clock — but a lever that reordered records would be, so each one has to earn its place by
/// producing a trace identical to one recorded without it.
/// </summary>
internal static class PlaybackSpeed
{
    /// <summary>
    /// Frame rate assumed only when the live process delta is unavailable. The game's animations
    /// are authored in seconds against a 60 Hz process loop.
    /// </summary>
    public const int FallbackFps = 60;

    private static Func<bool>? _previousAutoSlayerCheck;
    private static int? _previousMaxFps;
    private static FastModeType? _previousFastMode;

    /// <summary>Which levers the running batch enabled; <see cref="PlaybackSpeedLevers.None"/> when idle.</summary>
    public static PlaybackSpeedLevers Active { get; private set; }

    public static bool IsActive => Active != PlaybackSpeedLevers.None;

    /// <summary>
    /// Whether the driver should poll on process frames rather than the wall clock. Frames are the
    /// finer clock once the frame rate is uncapped, and the only correct one once <c>--fixed-fps</c>
    /// decouples game time from real time.
    /// </summary>
    public static bool FramePolling => Active.HasFlag(PlaybackSpeedLevers.FramePolling);

    public static bool IsHeadless =>
        DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase);

    public static void Activate(PlaybackSpeedLevers levers)
    {
        if (IsActive || levers == PlaybackSpeedLevers.None)
        {
            return;
        }

        Active = levers;
        RecorderService.ExecuteSafely("enabling fast playback", () =>
        {
            if (levers.HasFlag(PlaybackSpeedLevers.NonInteractive))
            {
                // AutoSlayer owns this hook when it is the one driving. Forcing it true subsumes
                // whatever it returned, so keep the old delegate only to hand back an unchanged game.
                _previousAutoSlayerCheck = NonInteractiveMode.AutoSlayerCheck;
                NonInteractiveMode.AutoSlayerCheck = () => true;
            }

            if (levers.HasFlag(PlaybackSpeedLevers.InstantPresentation))
            {
                _previousFastMode = SaveManager.Instance.PrefsSave.FastMode;
                SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            }

            // Only headless can safely spin the loop: a visible window would render every frame.
            if (levers.HasFlag(PlaybackSpeedLevers.UncappedFrameRate) && IsHeadless)
            {
                _previousMaxFps = Engine.MaxFps;
                Engine.MaxFps = 0;
            }

            GD.Print($"[STS2PGN] Playback: fast mode on ({levers})");
        });
    }

    public static void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        Active = PlaybackSpeedLevers.None;
        RecorderService.ExecuteSafely("restoring playback speed settings", () =>
        {
            if (_previousAutoSlayerCheck is not null)
            {
                NonInteractiveMode.AutoSlayerCheck = _previousAutoSlayerCheck;
            }

            if (_previousFastMode is { } fastMode)
            {
                SaveManager.Instance.PrefsSave.FastMode = fastMode;
            }

            if (_previousMaxFps is { } maxFps)
            {
                Engine.MaxFps = maxFps;
            }

            _previousAutoSlayerCheck = null;
            _previousFastMode = null;
            _previousMaxFps = null;
            GD.Print("[STS2PGN] Playback: fast mode off; presentation settings restored");
        });
    }
}
