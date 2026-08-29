using System.Text.Json;
using Godot;
using STS2PGN.Mod.Playback.Dispatch;
using STS2PGN.Mod.Recording;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback;

/// <summary>
/// Executes one script's decisions in order: wait until the game is idle and the decision's surface
/// is present → verify the script's fingerprints against the live offer → submit through the game's
/// own input path → wait for the recorder to record the decision and emit its completion observation.
/// The driver never advances on its own clock; only recorder signals move it forward.
/// </summary>
internal sealed class PlaybackDriver
{
    private static readonly IReadOnlyList<IDecisionDispatcher> Dispatchers =
    [
        new CombatDispatcher(),
        new CrystalSphereDispatcher(),
        new MapDispatcher(),
        new RoomDispatcher(),
        new RewardDispatcher(),
        new ChoiceDispatcher(),
    ];

    /// <summary>Payload fields whose recorded value must equal the script's, per kind.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> IdentityFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["combat.play_card"] = ["combat_card_index", "card_model", "card", "target"],
        ["combat.use_potion"] = ["potion_slot", "potion_model", "in_combat", "target"],
        ["combat.discard_potion"] = ["potion_slot", "potion_model", "in_combat"],
        ["combat.end_turn"] = ["turn"],
        ["event.crystal_sphere.click"] = ["x", "y", "tool", "remaining_before"],
        ["event.crystal_sphere.set_tool"] = ["tool"],
        ["map.choose"] = ["col", "row"],
        ["event.choose"] = ["option_index", "option_id"],
        ["rest.choose"] = ["option_id"],
        ["shop.purchase"] = ["inventory_index", "entry_index", "entry_group", "entry_type", "model_id"],
        ["shop.remove_card"] = ["cost"],
        ["reward.claim"] = ["set_id", "reward_index", "reward"],
        ["reward.skip"] = ["set_id"],
        ["treasure.choose_relic"] = ["relic_index", "relic_model"],
        ["choice.index"] = ["indexes"],
        ["choice.cards"] = ["cards"],
    };

    private readonly DecisionScript _script;
    private readonly PlaybackReport _report;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _cancellation;
    private readonly bool _waitForIdleAfterCompletion;
    private readonly object _gate = new();
    // Arrival ticks order recorder events. Settling is "a completion boundary arrived after
    // this decision was recorded": some observations (turn boundaries, end-turn flushes)
    // reference the initiating decision's sequence, not the nested choice's.
    private readonly List<(long Tick, long Sequence, string Kind, JsonElement Data)> _recorded = [];
    private readonly List<(long Tick, long Sequence, string Context)> _settled = [];
    private long _tick;
    private DateTime _lastOfferAt = DateTime.MinValue;
    /// <summary>The last scripted decision record matched in the live trace.</summary>
    private long _lastMatchedDecisionSequence;

    public PlaybackDriver(
        DecisionScript script,
        PlaybackReport report,
        TimeSpan timeout,
        CancellationToken cancellation,
        bool waitForIdleAfterCompletion = true)
    {
        _script = script;
        _report = report;
        _timeout = timeout;
        _cancellation = cancellation;
        _waitForIdleAfterCompletion = waitForIdleAfterCompletion;
    }

    public async Task RunAsync()
    {
        RecorderService.Recorded += OnRecorded;
        RecorderService.Settled += OnSettled;
        RecorderService.LiveOfferRegistered += OnLiveOfferRegistered;
        try
        {
            for (int index = 0; index < _script.Decisions.Count; index++)
            {
                ScriptDecision decision = _script.Decisions[index];
                ScriptDecision? nextDecision = index + 1 < _script.Decisions.Count ? _script.Decisions[index + 1] : null;
                _cancellation.ThrowIfCancellationRequested();

                // A selection over a large pile is scripted one card at a time, every step naming
                // the selection so far. The screen takes the whole selection as a single interaction
                // and records it as a single answer, so the steps before the last are counted and
                // passed over; the last one presses every card the run names and confirms once.
                if (decision.ContinuedByNext)
                {
                    _report.DecisionsCompleted++;
                    continue;
                }

                if (!await DriveAsync(decision, nextDecision))
                {
                    return;
                }

                _report.DecisionsCompleted++;
            }

            if (_waitForIdleAfterCompletion)
            {
                bool idle = await PlaybackWait.TryUntilAsync(
                    () => LiveGame.IsIdle || !RecorderService.IsRecording,
                    _timeout,
                    _cancellation);
                if (!idle)
                {
                    ScriptDecision? lastDecision = _script.Decisions.LastOrDefault();
                    _report.Stop(
                        PlaybackStatus.GameError,
                        PlaybackReasons.Timeout,
                        "the game did not become idle after the final decision settled",
                        lastDecision);
                    _report.Offered = LiveGame.ToJson(DescribeSurface());
                    return;
                }
            }

            _report.Status = PlaybackStatus.Completed;
            _report.Reason = null;
            _report.Message = $"all {_script.Decisions.Count} decisions settled";
        }
        finally
        {
            RecorderService.Recorded -= OnRecorded;
            RecorderService.Settled -= OnSettled;
            RecorderService.LiveOfferRegistered -= OnLiveOfferRegistered;
        }
    }

    private async Task<bool> DriveAsync(ScriptDecision decision, ScriptDecision? nextDecision)
    {
        IDecisionDispatcher? dispatcher = Dispatchers.FirstOrDefault(candidate => candidate.Handles(decision.Kind));
        if (dispatcher is null)
        {
            _report.Stop(PlaybackStatus.GameError, PlaybackReasons.UnsupportedRecord, $"no driver mapping for {decision.Kind}", decision);
            return false;
        }

        if (!RecorderService.IsRecording)
        {
            _report.Stop(PlaybackStatus.GameError, PlaybackReasons.Exception, "the recorder stopped before the decision could be driven", decision);
            return false;
        }

        // Some terminal combat flows proceed without another click. In
        // particular, Battleworn Dummy's timer expiring records room.proceed
        // while the preceding end-turn action is settling. If that is the next
        // scripted decision, it has already happened and must be consumed
        // instead of waiting forever for a rewards screen that has closed.
        if (decision.Kind == "room.proceed"
            && FindRecorded(decision.Kind, _lastMatchedDecisionSequence) is { } automatic)
        {
            if (FindRecordedMismatch(decision, automatic.Data) is { } automaticMismatch)
            {
                _report.Stop(
                    PlaybackStatus.StoppedAtRecord,
                    PlaybackReasons.RecordedMismatch,
                    $"the game automatically accepted a different '{automaticMismatch}' than the script",
                    decision);
                _report.Expected = decision.Data;
                _report.Offered = automatic.Data;
                return false;
            }

            _lastMatchedDecisionSequence = automatic.Sequence;
            return true;
        }

        DateTime lastAdvance = DateTime.MinValue;
        bool ready = await PlaybackWait.TryUntilAsync(
            () =>
            {
                // Nested choices and the Crystal Sphere minigame are asked while the initiating
                // event action is still executing, so they cannot wait for the executor to go idle;
                // an open modal still blocks them.
                bool insideRunningAction = decision.IsChoice
                    || decision.Kind.StartsWith("event.crystal_sphere.", StringComparison.Ordinal);
                bool gate = insideRunningAction ? LiveGame.NoModalOpen : LiveGame.IsIdle;
                if (gate && SafeIsReady(dispatcher, decision))
                {
                    return OfferSettleElapsed(decision);
                }

                // Not there yet: let the dispatcher perform any unrecorded presentation step
                // (room Proceed buttons) that a player would click to reach the decision.
                if (gate && DateTime.UtcNow - lastAdvance >= TimeSpan.FromSeconds(1))
                {
                    lastAdvance = DateTime.UtcNow;
                    SafeAdvance(dispatcher, decision);
                }

                return false;
            },
            _timeout,
            _cancellation);
        if (!ready)
        {
            _report.Stop(PlaybackStatus.GameError, PlaybackReasons.Timeout, $"the game never reached a state where {decision.Kind} could be submitted", decision);
            _report.Offered = LiveGame.ToJson(DescribeSurface());
            return false;
        }

        VerifyResult verification;
        try
        {
            verification = dispatcher.Verify(decision);
        }
        catch (Exception exception)
        {
            _report.Stop(PlaybackStatus.GameError, PlaybackReasons.Exception, $"verification threw: {exception}", decision);
            return false;
        }

        if (!verification.Ok)
        {
            _report.StopWithMismatch(decision, verification.Message, verification.Expected, verification.Offered);
            return false;
        }

        long baseline = RecorderService.LastDecisionSequence ?? 0;
        try
        {
            await dispatcher.SubmitAsync(decision);
        }
        catch (Exception exception)
        {
            _report.Stop(PlaybackStatus.GameError, PlaybackReasons.Exception, $"submit threw: {exception}", decision);
            return false;
        }

        (long Tick, long Sequence, string Kind, JsonElement Data)? recorded = null;
        bool wasRecorded = await PlaybackWait.TryUntilAsync(
            () => (recorded = FindRecorded(decision.Kind, baseline)) is not null,
            _timeout,
            _cancellation);
        if (!wasRecorded)
        {
            _report.Stop(PlaybackStatus.GameError, PlaybackReasons.Timeout, $"the recorder never recorded {decision.Kind} after submission", decision);
            _report.Offered = LiveGame.ToJson(DescribeSurface());
            return false;
        }

        if (FindRecordedMismatch(decision, recorded!.Value.Data) is { } mismatchField)
        {
            _report.Stop(PlaybackStatus.StoppedAtRecord, PlaybackReasons.RecordedMismatch, $"the game accepted a different '{mismatchField}' than the script", decision);
            _report.Expected = decision.Data;
            _report.Offered = recorded.Value.Data;
            return false;
        }

        _lastMatchedDecisionSequence = recorded.Value.Sequence;

        if (dispatcher.SettlesByObservation(decision))
        {
            long recordedSequence = recorded.Value.Sequence;
            long recordedTick = recorded.Value.Tick;
            bool finalEndTurn = _waitForIdleAfterCompletion
                && nextDecision is null
                && decision.Kind == "combat.end_turn";
            bool reachedNestedChoice = false;
            bool settled = await PlaybackWait.TryUntilAsync(
                () => (finalEndTurn
                        ? HasCompletedEnemyTurnAfter(recordedTick)
                        : HasSettledAfter(recordedTick))
                    || (reachedNestedChoice = IsNextNestedChoiceReady(nextDecision))
                    || ChoiceLeftNothingToSettle(decision)
                    || !RecorderService.IsRecording,
                _timeout,
                _cancellation);
            if (!settled)
            {
                _report.Stop(PlaybackStatus.GameError, PlaybackReasons.Timeout, $"{decision.Kind} was recorded as sequence {recordedSequence} but never settled", decision);
                return false;
            }

            if (reachedNestedChoice)
            {
                // Hand-selection choices do not register a live offer, while deck grids do.
                // Start the same presentation settle delay for both before the next loop
                // presses holders on the newly opened screen.
                _lastOfferAt = DateTime.UtcNow;
            }
        }

        return true;
    }

    private static bool SafeIsReady(IDecisionDispatcher dispatcher, ScriptDecision decision)
    {
        try
        {
            return dispatcher.IsReady(decision);
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[STS2PGN] Playback readiness check for {decision.Kind} threw: {exception.Message}");
            return false;
        }
    }

    private static void SafeAdvance(IDecisionDispatcher dispatcher, ScriptDecision decision)
    {
        try
        {
            if (dispatcher.TryAdvanceSurface(decision))
            {
                GD.Print($"[STS2PGN] Playback: advanced the screen toward {decision.Kind} (sequence {decision.Sequence})");
            }
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[STS2PGN] Playback surface advance for {decision.Kind} threw: {exception.Message}");
        }
    }

    /// <summary>
    /// A choice asked at a turn boundary rather than from inside an action settles no action, so
    /// the observation this wait looks for never arrives. Tools of the Trade asks for its discard
    /// from the player turn's own start: the answer is accepted and recorded, and the next
    /// observation comes only when the player acts — and the player is this driver, still waiting
    /// here. Once the answer is recorded and the executor has gone idle with nothing over it, there
    /// is nothing further to see.
    /// </summary>
    private static bool ChoiceLeftNothingToSettle(ScriptDecision decision) =>
        decision.IsChoice && LiveGame.IsIdle;

    private static bool IsNextNestedChoiceReady(ScriptDecision? decision)
    {
        if (decision is not { IsChoice: true })
        {
            return false;
        }

        IDecisionDispatcher? dispatcher = Dispatchers.FirstOrDefault(candidate => candidate.Handles(decision.Kind));
        return dispatcher is not null && SafeIsReady(dispatcher, decision);
    }

    private bool OfferSettleElapsed(ScriptDecision decision) =>
        !decision.IsChoice || DateTime.UtcNow - _lastOfferAt >= ChoiceDispatcher.ScreenSettleDelay;

    private void OnRecorded(long sequence, string kind, object? data)
    {
        JsonElement element;
        try
        {
            element = LiveGame.ToJson(data);
        }
        catch
        {
            element = default;
        }

        lock (_gate)
        {
            _recorded.Add((++_tick, sequence, kind, element));
        }
    }

    private void OnLiveOfferRegistered()
    {
        lock (_gate)
        {
            _lastOfferAt = DateTime.UtcNow;
        }
    }

    private void OnSettled(long sequence, string context)
    {
        lock (_gate)
        {
            _settled.Add((++_tick, sequence, context));
            if (context == "choice.offered")
            {
                _lastOfferAt = DateTime.UtcNow;
            }
        }
    }

    private (long Tick, long Sequence, string Kind, JsonElement Data)? FindRecorded(string kind, long baseline)
    {
        lock (_gate)
        {
            foreach (var entry in _recorded)
            {
                if (entry.Sequence > baseline && entry.Kind == kind)
                {
                    return entry;
                }
            }

            return null;
        }
    }

    private bool HasSettledAfter(long recordedTick)
    {
        lock (_gate)
        {
            return _settled.Any(entry => entry.Tick > recordedTick);
        }
    }

    /// <summary>
    /// An end-turn record first emits player-turn-end and enemy-turn-start observations. Neither
    /// means the automatic enemy turn is finished: returning to the menu at either boundary can
    /// turn a natural defeat into an abandoned recording. Wait for the next player turn or for a
    /// reward surface; a terminal run is covered by the recorder-stopped condition at the caller.
    /// </summary>
    private bool HasCompletedEnemyTurnAfter(long recordedTick)
    {
        lock (_gate)
        {
            return _settled.Any(entry => entry.Tick > recordedTick
                && entry.Context is "combat.player_turn.start" or "reward.offered" or "room.exit");
        }
    }

    private static string? FindRecordedMismatch(ScriptDecision decision, JsonElement recorded)
    {
        if (!IdentityFields.TryGetValue(decision.Kind, out string[]? fields) || recorded.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string field in fields)
        {
            if (!decision.Data.TryGetProperty(field, out JsonElement expected) || !recorded.TryGetProperty(field, out JsonElement actual))
            {
                continue;
            }

            bool matches = (decision.Kind, field) switch
            {
                ("choice.index", "indexes") => ScriptDecisionReader.SelectedIndexesMatch(expected, actual),
                // What the recorder wrote down for the claim is read the same way
                // the live offer is read before it: where the save schema refused
                // the offer, the block it would have filled is missing from the
                // record rather than disagreeing with the script.
                ("reward.claim", "reward") => ScriptDecisionReader.RewardFingerprintMatches(expected, actual),
                _ => LiveGame.SameJson(expected, actual),
            };
            if (!matches)
            {
                return field;
            }
        }

        return null;
    }

    private static object DescribeSurface()
    {
        try
        {
            return new
            {
                idle = LiveGame.IsIdle,
                room = LiveGame.State?.CurrentRoom?.ModelId?.ToString(),
                room_type = LiveGame.State?.CurrentRoom?.RoomType.ToString(),
                combat = LiveGame.Combat is { } combat ? combat.CurrentSide.ToString() : null,
                top_overlay = LiveGame.TopOverlay?.GetType().Name,
                last_sequence = RecorderService.LastDecisionSequence,
            };
        }
        catch (Exception exception)
        {
            return new { error = exception.Message };
        }
    }
}
