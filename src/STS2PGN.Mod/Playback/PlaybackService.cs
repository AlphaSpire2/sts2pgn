using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// Batch orchestrator: waits for the main menu, then for each script runs pre-flight → start →
/// drive → report → back to the main menu, unless a successful single-script handoff leaves the
/// live run under player control. Nothing here may throw into the game; every failure becomes a report row.
/// </summary>
internal static class PlaybackService
{
    private static PlaybackLaunchArgs? _active;
    private static CancellationTokenSource? _cancellation;

    public static bool IsActive => _active is not null;
    public static bool IsHandoffActive => _active?.HandoffWhenDone == true;

    /// <summary>Called once from the mod initializer. No-op unless <c>--sts2pgn-script</c> was given.</summary>
    public static void StartFromCommandLine()
    {
        RecorderService.ExecuteSafely("reading playback command line", () =>
        {
            PlaybackLaunchArgs? args = PlaybackLaunchArgs.FromCommandLine();
            if (args is null)
            {
                return;
            }

            if (!TryStart(args, out string error))
            {
                GD.PrintErr($"[STS2PGN] Playback not started: {error}");
            }
        });
    }

    public static bool TryStart(PlaybackLaunchArgs args, out string error)
    {
        if (_active is not null)
        {
            error = "a playback batch is already running";
            return false;
        }

        if (args.HandoffWhenDone && (args.QuitWhenDone || args.ScriptPaths.Count != 1))
        {
            error = "handoff requires exactly one interactive script";
            return false;
        }

        _active = args;
        _cancellation = new CancellationTokenSource();
        GD.Print($"[STS2PGN] Playback batch: {args.ScriptPaths.Count} script(s), quit when done: {args.QuitWhenDone}, handoff: {args.HandoffWhenDone}");
        _ = RunBatchAsync(args, _cancellation.Token);
        error = string.Empty;
        return true;
    }

    public static void Cancel() => _cancellation?.Cancel();

    private static async Task RunBatchAsync(PlaybackLaunchArgs args, CancellationToken cancellation)
    {
        PlaybackSummary summary = new();
        string? outputDirectory = null;
        try
        {
            await PlaybackRunStarter.WaitForMainMenuAsync(cancellation);
            PlaybackSpeed.Activate(RecorderService.Config.FastPlayback);

            outputDirectory = RecorderService.Config.ResolveOutputDirectory();
            foreach (string scriptPath in args.ScriptPaths)
            {
                cancellation.ThrowIfCancellationRequested();
                PlaybackReport report = await RunScriptAsync(scriptPath, args.HandoffWhenDone, cancellation);
                report.FinishedAt = DateTimeOffset.UtcNow;
                string reportPath = PlaybackReportWriter.Write(outputDirectory, report);
                summary.Scripts.Add(new PlaybackSummaryRow(
                    scriptPath,
                    report.Status,
                    report.Reason,
                    report.StoppedAtSequence,
                    report.RecordingPath,
                    reportPath,
                    report.HandedOff));
                GD.Print($"[STS2PGN] Playback {Path.GetFileName(scriptPath)}: {report.Status} {report.Reason} {report.Message}");
            }
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[STS2PGN] Playback batch aborted: {exception}");
            summary.Scripts.Add(new PlaybackSummaryRow("<batch>", PlaybackStatus.GameError, PlaybackReasons.Exception, null, null, string.Empty, false));
        }
        finally
        {
            // Restore the player's presentation settings before anything else: a handoff leaves
            // the run live under manual control, and quitting should not persist an odd fast mode.
            PlaybackSpeed.Deactivate();
            summary.FinishedAt = DateTimeOffset.UtcNow;
            int exitCode = summary.ExitCode;
            RecorderService.ExecuteSafely("writing playback summary", () =>
            {
                string directory = outputDirectory ?? RecorderService.Config.ResolveOutputDirectory();
                string path = PlaybackReportWriter.Write(directory, summary);
                GD.Print($"[STS2PGN] Playback summary written to {path} (exit code {exitCode})");
            });
            _active = null;
            _cancellation?.Dispose();
            _cancellation = null;
            if (args.QuitWhenDone)
            {
                Quit(exitCode);
            }
        }
    }

    private static async Task<PlaybackReport> RunScriptAsync(
        string scriptPath,
        bool handoffWhenDone,
        CancellationToken cancellation)
    {
        PlaybackReport report = new() { Script = scriptPath };
        PreflightResult preflight = ScriptPreflight.Run(scriptPath);
        report.ConvertedFromRecording = preflight.ConvertedFromRecording;
        if (preflight.ConvertedFromRecording)
        {
            GD.Print($"[STS2PGN] Playback: {Path.GetFileName(scriptPath)} is a recording; replaying its decisions as a script");
        }

        foreach (string warning in preflight.Warnings)
        {
            GD.Print($"[STS2PGN] Playback: {Path.GetFileName(scriptPath)} {warning}");
        }

        if (!preflight.Passed)
        {
            report.PreconditionFailures.AddRange(preflight.Failures);
            bool validation = preflight.Failures.Any(failure => failure.StartsWith(PlaybackReasons.Validation, StringComparison.Ordinal));
            report.Stop(
                PlaybackStatus.RefusedAtStart,
                validation ? PlaybackReasons.Validation : PlaybackReasons.Precondition,
                string.Join("; ", preflight.Failures));
            return report;
        }

        report.DecisionsTotal = preflight.Script!.Decisions.Count;
        bool runStarted = false;
        bool handedOff = false;
        try
        {
            await PlaybackRunStarter.StartAsync(preflight, cancellation);
            runStarted = true;
            report.RecordingPath = RecorderService.CurrentRecordingPath;
            if (!RecorderService.IsRecording)
            {
                report.Stop(PlaybackStatus.GameError, PlaybackReasons.Exception, "The recorder did not start for the playback run.");
                return report;
            }

            PlaybackDriver driver = new(
                preflight.Script,
                report,
                TimeSpan.FromSeconds(RecorderService.Config.PlaybackTimeoutSeconds),
                cancellation,
                waitForIdleAfterCompletion: !handoffWhenDone);
            await driver.RunAsync();

            if (handoffWhenDone && report.Status == PlaybackStatus.Completed)
            {
                if (RunManager.Instance.IsInProgress && RecorderService.IsRecording)
                {
                    handedOff = true;
                    report.HandedOff = true;
                    report.Message = $"all {report.DecisionsCompleted} decisions settled; control handed to the player with recording still active";
                    GD.Print($"[STS2PGN] Playback: control handed off after {report.DecisionsCompleted} decisions; recording continues at {report.RecordingPath}");
                }
                else
                {
                    report.Stop(
                        PlaybackStatus.GameError,
                        PlaybackReasons.Precondition,
                        "handoff was requested, but the script did not leave an active recorded run");
                }
            }
        }
        catch (OperationCanceledException)
        {
            report.Stop(PlaybackStatus.GameError, PlaybackReasons.Exception, "Playback was cancelled.");
        }
        catch (PlaybackTimeoutException exception)
        {
            report.Stop(PlaybackStatus.GameError, PlaybackReasons.Timeout, exception.Message);
        }
        catch (Exception exception)
        {
            report.Stop(PlaybackStatus.GameError, PlaybackReasons.Exception, exception.ToString());
        }
        finally
        {
            if (runStarted && !handedOff)
            {
                try
                {
                    // Standard playback must clean up the live run to make the
                    // next batch item startable. Close a still-live recording
                    // as an incomplete prefix before that cleanup abandons the
                    // game; otherwise the harness manufactures a run.finish
                    // ABANDONED result that was not produced by the script.
                    // A run that ended naturally has already stopped its
                    // recorder and retains its real VICTORY/DEFEAT result.
                    if (RecorderService.IsRecording)
                    {
                        RecorderService.StopIncomplete();
                    }

                    await PlaybackRunStarter.ReturnToMainMenuAsync(cancellation);
                }
                catch (Exception exception)
                {
                    GD.PrintErr($"[STS2PGN] Playback: could not return to the main menu cleanly: {exception}");
                }
            }

            if (!handedOff)
            {
                RecorderService.ExecuteSafely("capturing live unlocks after playback", () =>
                    report.UnlocksAfterRun = PlaybackUnlocks.From(ScriptPreflight.CurrentLiveUnlocks()));
            }
        }

        return report;
    }

    private static void Quit(int exitCode)
    {
        GD.Print($"[STS2PGN] Playback: quitting with exit code {exitCode}");
        NGame.Instance?.GetTree().Quit(exitCode);
    }
}
