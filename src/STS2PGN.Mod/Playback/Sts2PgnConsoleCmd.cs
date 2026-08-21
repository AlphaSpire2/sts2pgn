using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// Dev-console entry point: <c>sts2pgn drive &lt;file|dir|list.txt&gt;</c> runs a playback batch
/// interactively (never quits the game); <c>sts2pgn cancel</c> stops it. Discovered by the game's
/// <c>ReflectionHelper.GetSubtypesInMods</c>, so it must be public with a parameterless constructor,
/// and the command name must be lowercase.
/// </summary>
public sealed class Sts2PgnConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "sts2pgn";

    public override string Args => "drive <file|dir|list.txt> | cancel";

    public override string Description => "Drive STS2PGN decision scripts in the real game while recording.";

    public override bool IsNetworked => false;

    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(success: false, $"usage: {CmdName} {Args}");
        }

        switch (args[0].ToLowerInvariant())
        {
            case "drive":
                if (args.Length < 2)
                {
                    return new CmdResult(success: false, "drive requires a script file, directory, or list path");
                }

                try
                {
                    PlaybackLaunchArgs launch = PlaybackLaunchArgs.FromConsole(string.Join(' ', args.Skip(1)));
                    return PlaybackService.TryStart(launch, out string error)
                        ? new CmdResult(success: true, $"driving {launch.ScriptPaths.Count} script(s); reports go to the recorder output directory")
                        : new CmdResult(success: false, error);
                }
                catch (Exception exception) when (exception is IOException or ArgumentException)
                {
                    return new CmdResult(success: false, exception.Message);
                }

            case "cancel":
                if (!PlaybackService.IsActive)
                {
                    return new CmdResult(success: false, "no playback batch is running");
                }

                PlaybackService.Cancel();
                return new CmdResult(success: true, "cancelling after the current step");

            default:
                return new CmdResult(success: false, $"unknown subcommand '{args[0]}'; usage: {CmdName} {Args}");
        }
    }
}
