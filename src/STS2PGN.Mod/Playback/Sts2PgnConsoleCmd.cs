using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// Dev-console entry point: <c>sts2pgn drive &lt;file|dir|list.txt&gt;</c> runs a playback batch;
/// <c>sts2pgn step &lt;file&gt;</c> pauses before each decision for a button or key press;
/// <c>sts2pgn handoff &lt;file&gt;</c> drives one partial script and leaves its run under player control;
/// <c>sts2pgn cancel</c> stops and abandons an active playback. Discovered by the game's
/// <c>ReflectionHelper.GetSubtypesInMods</c>, so it must be public with a parameterless constructor,
/// and the command name must be lowercase.
/// </summary>
public sealed class Sts2PgnConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "sts2pgn";

    public override string Args => "drive <file|dir|list.txt> | step <file> | handoff <file> | cancel";

    public override string Description => "Drive STS2PGN scripts while recording, optionally handing the run to the player.";

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
                return Start(args, handoffWhenDone: false);

            case "handoff":
                return Start(args, handoffWhenDone: true);

            case "step":
                return Start(args, handoffWhenDone: false, manualSteps: true);

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

    private static CmdResult Start(string[] args, bool handoffWhenDone, bool manualSteps = false)
    {
        string verb = manualSteps ? "step" : handoffWhenDone ? "handoff" : "drive";
        if (args.Length < 2)
        {
            return new CmdResult(
                success: false,
                handoffWhenDone || manualSteps
                    ? $"{verb} requires one script file"
                    : "drive requires a script file, directory, or list path");
        }

        try
        {
            PlaybackLaunchArgs launch = PlaybackLaunchArgs.FromConsole(
                string.Join(' ', args.Skip(1)),
                handoffWhenDone, manualSteps);
            string started = manualSteps
                ? "starting manual playback; close the console and use Next step or the configured shortcut"
                : handoffWhenDone
                ? "driving one script; control will be handed off after its last decision settles"
                : $"driving {launch.ScriptPaths.Count} script(s)";
            return PlaybackService.TryStart(launch, out string error)
                ? new CmdResult(success: true, $"{started}; reports go to the recorder output directory")
                : new CmdResult(success: false, error);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            return new CmdResult(success: false, $"{verb}: {exception.Message}");
        }
    }
}
