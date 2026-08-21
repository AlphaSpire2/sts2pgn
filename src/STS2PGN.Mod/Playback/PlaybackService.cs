using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// Batch orchestrator: waits for the main menu, then for each script runs pre-flight → start →
/// drive → report → back to the main menu. Nothing here may throw into the game; every failure
/// becomes a report row.
/// </summary>
internal static class PlaybackService
{
    private static PlaybackLaunchArgs? _active;
    private static CancellationTokenSource? _cancellation;

    public static bool IsActive => _active is not null;

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

        _active = args;
        _cancellation = new CancellationTokenSource();
        GD.Print($"[STS2PGN] Playback batch: {args.ScriptPaths.Count} script(s), quit when done: {args.QuitWhenDone}");
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
            outputDirectory = RecorderService.Config.ResolveOutputDirectory();
            foreach (string scriptPath in args.ScriptPaths)
            {
                cancellation.ThrowIfCancellationRequested();
                PlaybackReport report = await RunScriptAsync(scriptPath, cancellation);
                report.FinishedAt = DateTimeOffset.UtcNow;
                string reportPath = PlaybackReportWriter.Write(outputDirectory, report);
                summary.Scripts.Add(new PlaybackSummaryRow(
                    scriptPath,
                    report.Status,
                    report.Reason,
                    report.StoppedAtSequence,
                    report.RecordingPath,
                    reportPath));
                GD.Print($"[STS2PGN] Playback {Path.GetFileName(scriptPath)}: {report.Status} {report.Reason} {report.Message}");
            }
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[STS2PGN] Playback batch aborted: {exception}");
            summary.Scripts.Add(new PlaybackSummaryRow("<batch>", PlaybackStatus.GameError, PlaybackReasons.Exception, null, null, string.Empty));
        }
        finally
        {
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

    private static async Task<PlaybackReport> RunScriptAsync(string scriptPath, CancellationToken cancellation)
    {
        PlaybackReport report = new() { Script = scriptPath };
        PreflightResult preflight = ScriptPreflight.Run(scriptPath);
        report.ConvertedFromRecording = preflight.ConvertedFromRecording;
        if (preflight.ConvertedFromRecording)
        {
            GD.Print($"[STS2PGN] Playback: {Path.GetFileName(scriptPath)} is a recording; replaying its decisions as a script");
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
        try
        {
            await PlaybackRunStarter.StartAsync(preflight, cancellation);
            runStarted = true;
            report.RecordingPath = RecorderService.CurrentRecordingPath;
            if (report.RecordingPath is null)
            {
                report.Stop(PlaybackStatus.GameError, PlaybackReasons.Exception, "The recorder did not open a recording for the playback run.");
                return report;
            }

            PlaybackDriver driver = new(
                preflight.Script,
                report,
                TimeSpan.FromSeconds(RecorderService.Config.PlaybackTimeoutSeconds),
                cancellation);
            await driver.RunAsync();
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
            if (runStarted)
            {
                try
                {
                    await PlaybackRunStarter.ReturnToMainMenuAsync(cancellation);
                }
                catch (Exception exception)
                {
                    GD.PrintErr($"[STS2PGN] Playback: could not return to the main menu cleanly: {exception}");
                }
            }

            RecorderService.ExecuteSafely("capturing live unlocks after playback", () =>
                report.UnlocksAfterRun = PlaybackUnlocks.From(ScriptPreflight.CurrentLiveUnlocks()));
        }

        return report;
    }

    private static void Quit(int exitCode)
    {
        GD.Print($"[STS2PGN] Playback: quitting with exit code {exitCode}");
        NGame.Instance?.GetTree().Quit(exitCode);
    }
}
