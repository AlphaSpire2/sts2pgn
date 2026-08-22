using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2PGN.Model;
using STS2PGN.Mod.Playback;

namespace STS2PGN.Mod.Recording;

internal static class RecorderService
{
    private static RecordingSession? _session;
    private static RunState? _state;
    private static RecorderConfig _config = new(RecorderConfig.DefaultOutputDirectory);
    private static readonly List<RoomTraceData> RoomStack = [];
    private static readonly Dictionary<(ulong PlayerId, uint ChoiceId), JsonElement> ChoiceOffers = [];
    private static readonly Dictionary<ulong, CardReward> ActiveCardRewards = [];
    private static readonly Dictionary<(ulong PlayerId, int SetId), RewardsSet> ActiveRewardSets = [];
    private static readonly Dictionary<ulong, long> PendingRestActions = [];
    private static readonly Dictionary<GameAction, long> ActionSequences = [];
    private static readonly FieldInfo? CurrentRoomsField = typeof(RunState).GetField(
        "_currentRooms",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static long? _lastDecisionSequence;
    private static long? _pendingEndTurnSequence;
    private static long? _pendingUndoEndTurnSequence;
    private static long? _pendingEventChoiceSequence;
    private static long? _pendingEventAdvanceSequence;
    private static long? _pendingCrystalSphereSequence;
    private static JsonElement? _specialEventState;
    private static PendingResume? _pendingResume;
    private static readonly Dictionary<(ulong PlayerId, uint ChoiceId), LiveChoiceOffer> LiveChoiceOffers = [];

    /// <summary>Raised after a record was accepted: (sequence, kind, data object as passed to the recorder).</summary>
    public static event Action<long, string, object?>? Recorded;

    /// <summary>Raised after a state observation was appended: (observed sequence, completion context).</summary>
    public static event Action<long, string>? Settled;

    /// <summary>Raised when a driver-only live offer (deck selection screen) was registered.</summary>
    public static event Action? LiveOfferRegistered;

    public static RecorderConfig Config => _config;
    public static string? CurrentRecordingPath => _session?.Path;
    public static RunState? CurrentState => _state;
    public static long? LastDecisionSequence => _lastDecisionSequence;
    public static bool IsRecording => _session is not null;

    /// <summary>The live game objects behind a registered choice offer, for drive-time verification.</summary>
    public static bool TryGetLiveOffer(ulong playerId, uint choiceId, out LiveChoiceOffer offer) =>
        LiveChoiceOffers.TryGetValue((playerId, choiceId), out offer!);

    public static IEnumerable<KeyValuePair<(ulong PlayerId, uint ChoiceId), LiveChoiceOffer>> PendingLiveOffers(ulong playerId) =>
        LiveChoiceOffers.Where(pair => pair.Key.PlayerId == playerId);

    public static bool TryGetActiveRewardSet(ulong playerId, int setId, out RewardsSet set) =>
        ActiveRewardSets.TryGetValue((playerId, setId), out set!);

    public static IEnumerable<RewardsSet> ActiveRewardSetsFor(ulong playerId) =>
        ActiveRewardSets.Where(pair => pair.Key.PlayerId == playerId).Select(pair => pair.Value);

    public static void Initialize()
    {
        _config = RecorderConfig.Load();
        RunManager manager = RunManager.Instance;
        manager.RunStarted += OnRunStarted;
        manager.RoomEntered += OnRoomEntered;
        manager.RoomExited += OnRoomExited;
        manager.ActEntered += OnActEntered;
    }

    public static void Finish(bool isVictory, bool isAbandoned)
    {
        RunResult result = isAbandoned ? RunResult.Abandoned : isVictory ? RunResult.Victory : RunResult.Defeat;
        try
        {
            if (_session is { } session)
            {
                JsonElement terminalState = session.CaptureState(BuildRecorderState());
                session.Complete(result, terminalState);
            }
        }
        catch (Exception exception)
        {
            ReportError("finalizing trace", exception);
        }
        finally
        {
            ExecuteSafely("closing trace", StopSession);
        }
    }

    public static void StopIncomplete()
    {
        ExecuteSafely("closing incomplete trace", StopSession);
    }

    public static void MarkNextRunAsResume(SerializableRun save)
    {
        // Snapshot the restored save immediately: it is the authoritative anchor the
        // game loaded from disk, before setup mutates any state derived from it.
        string? snapshotJson = null;
        ExecuteSafely("capturing resumed save snapshot", () => snapshotJson = JsonSerializationUtility.ToJson(save));
        _pendingResume = new PendingResume(save, snapshotJson);
    }

    public static void RecordEventChoice(int optionIndex)
    {
        ExecuteSafely("capturing event choice", () =>
        {
            var currentEvent = RunManager.Instance.EventSynchronizer.Events.FirstOrDefault();
            if (currentEvent is null || currentEvent.IsFinished || optionIndex < 0 || optionIndex >= currentEvent.CurrentOptions.Count)
            {
                return;
            }

            var option = currentEvent.CurrentOptions[optionIndex];
            _pendingEventChoiceSequence = TryRecord("event.choose", new
            {
                event_id = currentEvent?.Id.ToString(),
                option_index = optionIndex,
                option_id = option.TextKey,
                is_proceed = option.IsProceed,
            }, LocalPlayerId());
        });
    }

    public static void RecordCrystalSphereStart(object state, ulong playerId) => ExecuteSafely(
        "capturing Crystal Sphere initial state",
        () =>
        {
            _specialEventState = JsonSerializer.SerializeToElement(state);
            ObserveAfter(TryRecord("event.crystal_sphere.start", state, playerId), "event.crystal_sphere.start");
        });

    public static void RecordCrystalSphereTool(string tool, object state) => ExecuteSafely(
        "capturing Crystal Sphere tool state",
        () =>
        {
            _specialEventState = JsonSerializer.SerializeToElement(state);
            ObserveAfter(
                TryRecord("event.crystal_sphere.set_tool", new { tool }, LocalPlayerId()),
                "event.crystal_sphere.tool");
        });

    public static void RecordCrystalSphereClick(int x, int y, string tool, int remainingBefore) => ExecuteSafely(
        "capturing Crystal Sphere cell action",
        () => _pendingCrystalSphereSequence = TryRecord(
            "event.crystal_sphere.click",
            new { x, y, tool, remaining_before = remainingBefore },
            LocalPlayerId()));

    public static void ObserveCrystalSphereClickSettled(object state) => ExecuteSafely(
        "capturing settled Crystal Sphere cell state",
        () =>
        {
            _specialEventState = JsonSerializer.SerializeToElement(state);
            long? sequence = _pendingCrystalSphereSequence.HasValue
                ? Math.Max(_pendingCrystalSphereSequence.Value, _lastDecisionSequence ?? 0)
                : null;
            ObserveAfter(sequence, "event.crystal_sphere.click");
            _pendingCrystalSphereSequence = null;
        });

    public static void RecordCrystalSphereProceed() =>
        TryRecord("event.crystal_sphere.proceed", new { }, LocalPlayerId());

    public static void RecordDirectEndTurn(ulong playerId, int turn) =>
        ExecuteSafely("capturing direct end turn", () =>
            _pendingEndTurnSequence = TryRecord("combat.end_turn", new { turn }, playerId));

    public static void RecordRoomProceed()
    {
        ExecuteSafely("capturing room proceed", () =>
        {
            if (_state?.CurrentRoom is { } room && room.ModelId?.Entry != "CRYSTAL_SPHERE")
            {
                TryRecord("room.proceed", new { }, LocalPlayerId());
            }
        });
    }

    public static void RecordEventAdvance() => ExecuteSafely("capturing event dialogue", () =>
        _pendingEventAdvanceSequence = TryRecord("event.advance", new
        {
            event_id = RunManager.Instance.EventSynchronizer.Events.FirstOrDefault()?.Id.ToString(),
        }, LocalPlayerId()));

    public static void ObserveEventAdvanceSettled() => ExecuteSafely(
        "capturing settled event dialogue",
        () =>
        {
            ObserveAfter(_pendingEventAdvanceSequence, "event.dialogue.settled");
            _pendingEventAdvanceSequence = null;
        });

    public static void ExecuteSafely(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ReportError(operation, exception);
        }
    }

    public static long? TryRecord(string kind, object? data, ulong? actorId = null)
    {
        try
        {
            long? sequence = _session?.Record(kind, data, actorId);
            if (sequence.HasValue)
            {
                _lastDecisionSequence = sequence;
                Recorded?.Invoke(sequence.Value, kind, data);
            }

            return sequence;
        }
        catch (Exception exception)
        {
            ReportError($"recording {kind}", exception);
            return null;
        }
    }

    public static void ObserveEventChoiceSettled()
    {
        ExecuteSafely("capturing settled event choice", () =>
        {
            long? sequence = _pendingEventChoiceSequence.HasValue
                ? Math.Max(_pendingEventChoiceSequence.Value, _lastDecisionSequence ?? 0)
                : null;
            ObserveAfter(sequence, "event.choice.settled");
            _pendingEventChoiceSequence = null;
        });
    }

    public static void ObserveMapGenerated() => ExecuteSafely(
        "capturing generated map",
        () => ObserveAfter(_lastDecisionSequence, "map.generated"));

    public static void BeginRewardsSet(RewardsSet set)
    {
        ActiveRewardSets[(set.Player.NetId, set.Id)] = set;
        ObserveAfter(_lastDecisionSequence, "reward.offered");
    }

    public static void EndRewardsSet(RewardsSet set)
    {
        ActiveRewardSets.Remove((set.Player.NetId, set.Id));
        ObserveAfter(_lastDecisionSequence, "reward.settled");
    }

    public static long? BeginRewardClaim(RewardsSet set, Reward reward)
    {
        int rewardIndex = set.Rewards.IndexOf(reward);
        object fingerprint = PortablePayloads.RewardFingerprint(reward);
        long? sequence = TryRecord("reward.claim", new
        {
            set_id = set.Id,
            reward_index = rewardIndex,
            reward = fingerprint,
        }, reward.Player.NetId);

        if (reward is CardReward cardReward)
        {
            ActiveCardRewards[reward.Player.NetId] = cardReward;
        }

        return sequence;
    }

    public static void ResolveRewardClaim(long? actionSequence, ulong playerId, bool succeeded)
    {
        ActiveCardRewards.Remove(playerId);
        if (!actionSequence.HasValue)
        {
            return;
        }

        long? resolution = TryRecord("reward.resolve", new
        {
            action_sequence = actionSequence.Value,
            success = succeeded,
        }, playerId);
        ObserveAfter(resolution, "reward.claim.settled");
    }

    public static void RecordRewardSkip(RewardsSet set)
    {
        long? sequence = TryRecord("reward.skip", new
        {
            set_id = set.Id,
            remaining_rewards = set.Rewards
                .Select((reward, index) => new
                {
                    reward_index = index,
                    selected = reward.SuccessfullySelected,
                    reward = PortablePayloads.RewardFingerprint(reward),
                })
                .ToArray(),
        }, set.Player.NetId);
        ObserveAfter(sequence, "reward.skip.settled");
    }

    public static long? BeginShopCardRemoval(int goldCost, bool cancelable)
    {
        ulong? playerId = LocalPlayerId();
        long? sequence = TryRecord("shop.remove_card", new
        {
            cost = goldCost,
            cancelable,
        }, playerId);

        Player? player = playerId.HasValue
            ? _state?.Players.FirstOrDefault(candidate => candidate.NetId == playerId.Value)
            : null;
        if (player is not null)
        {
            RegisterUpcomingChoice(player, "merchant_card_removal", new
            {
                cards = player.Deck.Cards.Select((card, index) => new
                {
                    deck_index = index,
                    fingerprint = PortablePayloads.CardFingerprint(card),
                }).ToArray(),
            }, player.Deck.Cards);
        }

        return sequence;
    }

    public static void ResolveShopCardRemoval(long? actionSequence, bool succeeded) =>
        ExecuteSafely("recording shop card removal outcome", () =>
        {
            if (actionSequence.HasValue)
            {
                long? resolution = TryRecord("shop.remove_card.resolve", new
                {
                    action_sequence = actionSequence.Value,
                    success = succeeded,
                }, LocalPlayerId());
                ObserveAfter(resolution, "shop.remove_card.settled");
            }
        });

    public static void RecordShopPurchaseResolution(long actionSequence, ulong playerId, bool succeeded)
    {
        long? resolution = TryRecord("shop.purchase.resolve", new
        {
            action_sequence = actionSequence,
            success = succeeded,
        }, playerId);
        ObserveAfter(resolution, "shop.purchase.settled");
    }

    public static uint? RegisterUpcomingChoice(Player player, string context, object options, object? liveModels = null)
    {
        if (_state is null)
        {
            return null;
        }

        int playerIndex = _state.Players.ToList().FindIndex(candidate => candidate.NetId == player.NetId);
        if (playerIndex < 0)
        {
            return null;
        }

        IReadOnlyList<uint> ids = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds;
        uint choiceId = playerIndex < ids.Count ? ids[playerIndex] : 0;
        JsonElement offered = JsonSerializer.SerializeToElement(new
        {
            context,
            options,
        });
        ChoiceOffers[(player.NetId, choiceId)] = offered;
        LiveChoiceOffers[(player.NetId, choiceId)] = new LiveChoiceOffer(context, offered, liveModels);
        ObserveAfter(_lastDecisionSequence, "choice.offered");
        return choiceId;
    }

    /// <summary>
    /// Registers the live models behind a selection screen the recorder does not describe in the trace
    /// (deck upgrade/transform/enchant/removal grids). Driver-side only: it never changes recorded
    /// offers or the projected <c>player_choices</c>, so recordings stay byte-identical.
    /// </summary>
    public static void RegisterLiveOnlyChoice(string context, IReadOnlyList<CardModel> cards)
    {
        ExecuteSafely("registering live-only choice offer", () =>
        {
            Player? player = _state?.Players.FirstOrDefault(candidate => candidate.NetId == LocalPlayerId());
            if (player is null)
            {
                return;
            }

            int playerIndex = _state!.Players.ToList().FindIndex(candidate => candidate.NetId == player.NetId);
            IReadOnlyList<uint> ids = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds;
            // The screen opens after ReserveChoiceId, so the pending id is the last one handed out.
            uint choiceId = playerIndex >= 0 && playerIndex < ids.Count && ids[playerIndex] > 0 ? ids[playerIndex] - 1 : 0;
            (ulong, uint) key = (player.NetId, choiceId);
            if (LiveChoiceOffers.ContainsKey(key))
            {
                return;
            }

            JsonElement offered = JsonSerializer.SerializeToElement(new
            {
                context,
                options = cards.Select((card, index) => new
                {
                    deck_index = player.Deck.Cards.ToList().IndexOf(card),
                    index,
                    fingerprint = PortablePayloads.CardFingerprint(card),
                }).ToArray(),
            });
            LiveChoiceOffers[key] = new LiveChoiceOffer(context, offered, cards, RecordedInTrace: false);
            LiveOfferRegistered?.Invoke();
        });
    }

    public static void RegisterReservedChoice(Player player, uint choiceId)
    {
        if (!ActiveCardRewards.TryGetValue(player.NetId, out CardReward? reward))
        {
            return;
        }

        var cards = reward.Cards.Select((card, index) => new
        {
            index,
            fingerprint = PortablePayloads.CardFingerprint(card),
        }).ToArray();
        string[] alternativeIds = CardRewardAlternative.Generate(reward)
            .Select(alternative => alternative.OptionId.ToLowerInvariant())
            .ToArray();

        JsonElement offered = JsonSerializer.SerializeToElement(new
        {
            context = "card_reward",
            options = new
            {
                cards,
                alternatives = alternativeIds.Select((optionId, offset) => new
                {
                    index = cards.Length + offset,
                    option_id = optionId,
                }).ToArray(),
            },
        });
        ChoiceOffers[(player.NetId, choiceId)] = offered;
        LiveChoiceOffers[(player.NetId, choiceId)] = new LiveChoiceOffer("card_reward", offered, reward);
        ObserveAfter(_lastDecisionSequence, "choice.offered");
    }

    public static void ClearUpcomingChoice(Player player, uint? choiceId)
    {
        if (!choiceId.HasValue)
        {
            return;
        }

        ChoiceOffers.Remove((player.NetId, choiceId.Value));
        LiveChoiceOffers.Remove((player.NetId, choiceId.Value));
    }

    private static void OnRunStarted(RunState state)
    {
        ExecuteSafely("closing previous trace", StopSession);
        if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            GD.Print("[STS2PGN] Multiplayer recording is not supported by format version 1");
            return;
        }

        PendingResume? resume = _pendingResume;
        _pendingResume = null;
        try
        {
            _state = state;
            bool persist = !PlaybackService.IsActive
                || PlaybackService.IsHandoffActive
                || _config.RecordPlaybackRuns;
            _session = persist && resume is not null
                ? RecordingSession.TryResume(state, _config, resume.Save)
                : null;
            _session ??= RecordingSession.Start(state, _config, persist);
            RoomStack.Clear();
            SeedRoomStack(state);

            ChoiceOffers.Clear();
            LiveChoiceOffers.Clear();
            ActiveCardRewards.Clear();
            ActiveRewardSets.Clear();
            PendingRestActions.Clear();
            ActionSequences.Clear();
            RunManager.Instance.ActionQueueSet.ActionEnqueued += OnActionEnqueued;
            RunManager.Instance.ActionExecutor.AfterActionExecuted += OnActionAfterExecuted;
            CombatManager.Instance.TurnStarted += OnTurnStarted;
            CombatManager.Instance.TurnEnded += OnTurnEnded;
            CombatManager.Instance.PlayerUnendedTurn += OnPlayerUnendedTurn;
            RunManager.Instance.PlayerChoiceSynchronizer.PlayerChoiceReceived += OnPlayerChoiceReceived;
            RunManager.Instance.RestSiteSynchronizer.BeforePlayerOptionChosen += OnBeforeRestSiteOptionChosen;
            RunManager.Instance.RestSiteSynchronizer.AfterPlayerOptionChosen += OnAfterRestSiteOptionChosen;
            _lastDecisionSequence = _session.RunStartSequence;
            _pendingEndTurnSequence = null;
            _pendingUndoEndTurnSequence = null;
            _pendingEventChoiceSequence = null;
            _pendingEventAdvanceSequence = null;
            _pendingCrystalSphereSequence = null;
            _specialEventState = null;
            if (resume is not null)
            {
                RecordResume(_session, resume);
            }

            GD.Print(_session.Path is null
                ? "[STS2PGN] Playback recorder active without writing a trace (record_playback_runs is false)"
                : _session.IsContinuation
                    ? $"[STS2PGN] Continuing trace {_session.Path}"
                    : $"[STS2PGN] Recording to {_session.Path}");
        }
        catch (Exception exception)
        {
            StopSession();
            ReportError("starting recorder", exception);
        }
    }

    private static void RecordResume(RecordingSession session, PendingResume resume)
    {
        // run.resume is a re-anchoring boundary: the game rewound to its last save
        // checkpoint, so decisions recorded after that checkpoint never happened in the
        // resumed timeline. Consumers re-initialize from this payload exactly as they
        // do from run.start; the following observation duplicates data.state with its
        // canonical digest, mirroring the run.start pattern.
        JsonElement resumeState = session.CaptureState(BuildRecorderState());
        object? sourceSnapshot = resume.SnapshotJson is null ? null : new
        {
            schema_version = resume.Save.SchemaVersion,
            encoding = "sts2-save-json",
            data = System.Text.Json.Nodes.JsonNode.Parse(resume.SnapshotJson),
        };
        long? sequence = TryRecord("run.resume", new
        {
            reloads = resume.Save.NumReloads,
            recorder_version = STS2PgnMod.Version,
            restored_pre_finished_room = resume.Save.PreFinishedRoom is not null,
            source_snapshot = sourceSnapshot,
            state = resumeState,
        });
        if (sequence.HasValue)
        {
            session.Observe(sequence.Value, "run.resume", resumeState);
        }
    }

    private sealed record PendingResume(SerializableRun Save, string? SnapshotJson);

    private static void OnActionEnqueued(GameAction action)
    {
        action.BeforeExecuted += OnActionBeforeExecuted;
    }

    private static void OnActionBeforeExecuted(GameAction action) =>
        ExecuteSafely("capturing game action", () =>
        {
            long? before = _lastDecisionSequence;
            RecordAction(action);
            if (_lastDecisionSequence.HasValue && _lastDecisionSequence != before)
            {
                ActionSequences[action] = _lastDecisionSequence.Value;
            }
        });

    private static void OnActionAfterExecuted(GameAction action) =>
        ExecuteSafely("capturing settled game action", () =>
        {
            if (!ActionSequences.Remove(action, out long sequence))
            {
                return;
            }

            INetAction netAction = action.ToNetAction();
            if (netAction is NetMoveToMapCoordAction
                or NetEndPlayerTurnAction
                or NetUndoEndPlayerTurnAction
                or NetVoteToMoveToNextActAction)
            {
                return;
            }

            ObserveAfter(Math.Max(sequence, _lastDecisionSequence ?? sequence), "action.settled");
        });

    private static void RecordAction(GameAction action)
    {
        INetAction netAction = action.ToNetAction();
        ulong actor = action.OwnerId;
        switch (netAction)
        {
            case NetPlayCardAction play:
                CardModel? playedCard = play.card.ToCardModelOrNull();
                TryRecord("combat.play_card", new
                {
                    combat_card_index = play.card.CombatCardIndex,
                    card_model = play.modelId.ToString(),
                    card = PortablePayloads.CardFingerprint(playedCard),
                    target_id = play.targetId,
                    target = CombatTarget(play.targetId),
                }, actor);
                break;
            case NetUsePotionAction potion:
                TryRecord("combat.use_potion", new
                {
                    potion_slot = potion.potionIndex,
                    potion_model = PotionModel(actor, potion.potionIndex),
                    target_id = potion.targetId,
                    target = CombatTarget(potion.targetId),
                    target_player = PlayerSlot(potion.targetPlayerId),
                    in_combat = potion.enqueuedInCombat,
                }, actor);
                break;
            case NetDiscardPotionGameAction discard:
                TryRecord("combat.discard_potion", new
                {
                    potion_slot = discard.potionSlotIndex,
                    potion_model = PotionModel(actor, discard.potionSlotIndex),
                    in_combat = discard.wasEnqueuedInCombat,
                }, actor);
                break;
            case NetEndPlayerTurnAction endTurn:
                _pendingEndTurnSequence = TryRecord("combat.end_turn", new { turn = endTurn.turnNumber }, actor);
                break;
            case NetUndoEndPlayerTurnAction undo:
                _pendingUndoEndTurnSequence = TryRecord("combat.undo_end_turn", new { turn = undo.turnNumber }, actor);
                break;
            case NetMoveToMapCoordAction move:
                TryRecord("map.choose", new { col = move.destination.col, row = move.destination.row }, actor);
                break;
            case NetPickRelicAction relic when relic.relicIndex.HasValue:
                var offeredRelics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
                TryRecord("treasure.choose_relic", new
                {
                    relic_index = relic.relicIndex.Value,
                    relic_model = offeredRelics is not null && relic.relicIndex.Value < offeredRelics.Count
                        ? offeredRelics[relic.relicIndex.Value].Id.ToString()
                        : null,
                    offered_relics = offeredRelics?.Select((offered, index) => new
                    {
                        index,
                        model_id = offered.Id.ToString(),
                    }).ToArray(),
                }, actor);
                break;
            case NetPickRelicAction:
                TryRecord("treasure.skip_relic", new { }, actor);
                break;
            case NetVoteToMoveToNextActAction:
                TryRecord("act.next", new { }, actor);
                break;
            case NetVoteForMapCoordAction:
                // The accepted NetMoveToMapCoordAction is the authoritative single-player decision.
                break;
            default:
                if (action.RecordableToReplay && ActionQueueSet.IsGameActionPlayerDriven(action))
                {
                    TryRecord("game.unknown_action", new
                    {
                        action_type = action.GetType().FullName,
                        net_action_type = netAction.GetType().FullName,
                        value = netAction.ToString(),
                    }, actor);
                }
                break;
        }
    }

    private static void OnPlayerChoiceReceived(Player player, uint choiceId, NetPlayerChoiceResult choice) =>
        ExecuteSafely("capturing player choice", () => RecordPlayerChoice(player, choiceId, choice));

    private static void RecordPlayerChoice(Player player, uint choiceId, NetPlayerChoiceResult choice)
    {
        ChoiceOffers.Remove((player.NetId, choiceId), out JsonElement offered);
        LiveChoiceOffers.Remove((player.NetId, choiceId));
        JsonElement? offeredChoice = offered.ValueKind == JsonValueKind.Undefined ? null : offered;
        string kind = choice.type switch
        {
            PlayerChoiceType.CanonicalCard or PlayerChoiceType.CombatCard or PlayerChoiceType.DeckCard or PlayerChoiceType.MutableCard => "choice.cards",
            PlayerChoiceType.Player => "choice.player",
            PlayerChoiceType.Index => "choice.index",
            _ => "choice.unknown",
        };
        object data = choice.type switch
        {
            PlayerChoiceType.CanonicalCard => new
            {
                choice_id = choiceId,
                choice_type = "canonical_card",
                cards = choice.canonicalCards?.Select(PortablePayloads.CardFingerprint).ToArray(),
                offered = offeredChoice,
            },
            PlayerChoiceType.CombatCard => new
            {
                choice_id = choiceId,
                choice_type = "combat_card",
                cards = choice.combatCards?.Select(card => new
                {
                    combat_card_index = card.CombatCardIndex,
                    fingerprint = PortablePayloads.CardFingerprint(card.ToCardModelOrNull()),
                }).ToArray(),
                offered = offeredChoice,
            },
            PlayerChoiceType.DeckCard => new
            {
                choice_id = choiceId,
                choice_type = "deck_card",
                cards = choice.deckCards?.Select(card => new
                {
                    deck_index = card.DeckIndex,
                    fingerprint = card.DeckIndex < (uint)player.Deck.Cards.Count
                        ? PortablePayloads.CardFingerprint(player.Deck.Cards[(int)card.DeckIndex])
                        : null,
                }).ToArray(),
                offered = offeredChoice,
            },
            PlayerChoiceType.MutableCard => new
            {
                choice_id = choiceId,
                choice_type = "mutable_card",
                owner = PlayerSlot(choice.mutableCardOwner),
                cards = choice.mutableCards?.Select(PortablePayloads.CardFingerprint).ToArray(),
                offered = offeredChoice,
            },
            PlayerChoiceType.Player => new
            {
                choice_id = choiceId,
                choice_type = "player",
                player = PlayerSlot(choice.playerId),
                offered = offeredChoice,
            },
            PlayerChoiceType.Index => new
            {
                choice_id = choiceId,
                choice_type = "index",
                indexes = choice.indexes,
                offered = offeredChoice,
            },
            _ => new
            {
                choice_id = choiceId,
                choice_type = PortablePayloads.Symbol(choice.type),
                offered = offeredChoice,
            },
        };
        TryRecord(kind, data, player.NetId);
    }

    private static void OnBeforeRestSiteOptionChosen(RestSiteOption option, ulong playerId)
    {
        long? sequence = TryRecord("rest.choose", new
        {
            option_id = option.OptionId,
        }, playerId);
        if (sequence.HasValue)
        {
            PendingRestActions[playerId] = sequence.Value;
        }
    }

    private static void OnAfterRestSiteOptionChosen(RestSiteOption option, bool succeeded, ulong playerId)
    {
        if (!PendingRestActions.Remove(playerId, out long sequence))
        {
            return;
        }

        long? resolution = TryRecord("rest.resolve", new
        {
            action_sequence = sequence,
            option_id = option.OptionId,
            success = succeeded,
        }, playerId);
        ObserveAfter(resolution, "rest.settled");
    }

    private static string? PlayerSlot(ulong? playerId)
    {
        if (!playerId.HasValue || _state is null)
        {
            return null;
        }

        int index = _state.Players.ToList().FindIndex(player => player.NetId == playerId.Value);
        return index >= 0 ? $"p{index}" : null;
    }

    private static void OnRoomEntered()
    {
        var room = _state?.CurrentRoom;
        if (room is null)
        {
            return;
        }

        RoomTraceData data = ToRoomTrace(room, _state!.CurrentRoomCount);
        RoomStack.Add(data);
        long? sequence = TryRecord("room.enter", data);
        ObserveAfter(sequence, "room.enter");
    }

    private static void OnRoomExited()
    {
        RoomTraceData? data = RoomStack.Count > 0 ? RoomStack[^1] : null;
        if (RoomStack.Count > 0)
        {
            RoomStack.RemoveAt(RoomStack.Count - 1);
        }

        if (data?.Model?.EndsWith("CRYSTAL_SPHERE", StringComparison.Ordinal) == true)
        {
            _specialEventState = null;
        }

        long? sequence = TryRecord("room.exit", data);
        ObserveAfter(sequence, "room.exit");
    }

    private static void OnActEntered()
    {
        long? sequence = TryRecord("act.enter", new { act = _state?.CurrentActIndex });
        ObserveAfter(sequence, "act.enter");
    }

    private static void SeedRoomStack(RunState state)
    {
        if (CurrentRoomsField?.GetValue(state) is IEnumerable<AbstractRoom> rooms)
        {
            RoomStack.AddRange(rooms.Select((room, index) => ToRoomTrace(room, index + 1)));
            return;
        }

        if (state.BaseRoom is { } baseRoom)
        {
            RoomStack.Add(ToRoomTrace(baseRoom, 1));
        }

        if (state.CurrentRoom is { } currentRoom && !ReferenceEquals(currentRoom, state.BaseRoom))
        {
            RoomStack.Add(ToRoomTrace(currentRoom, state.CurrentRoomCount));
        }
    }

    private static RoomTraceData ToRoomTrace(AbstractRoom room, int stackDepth) => new(
        room.Id,
        room.RoomType.ToString().ToUpperInvariant(),
        room.ModelId?.ToString(),
        stackDepth);

    private static void OnTurnStarted(CombatState combat) => ExecuteSafely(
        "capturing combat turn start",
        () =>
        {
            string context = combat.CurrentSide == CombatSide.Player
                ? "combat.player_turn.start"
                : "combat.enemy_turn.start";
            ObserveAfter(_pendingEndTurnSequence ?? _lastDecisionSequence, context);
            if (combat.CurrentSide == CombatSide.Player)
            {
                _pendingEndTurnSequence = null;
            }
        });

    private static void OnTurnEnded(CombatState combat) => ExecuteSafely(
        "capturing combat turn end",
        () => ObserveAfter(_pendingEndTurnSequence ?? _lastDecisionSequence, "combat.turn.end"));

    private static void OnPlayerUnendedTurn(Player player) => ExecuteSafely(
        "capturing restored player turn",
        () =>
        {
            ObserveAfter(_pendingUndoEndTurnSequence, "combat.undo_end_turn.settled");
            _pendingUndoEndTurnSequence = null;
        });

    private static void ObserveAfter(long? sequence, string context)
    {
        if (!sequence.HasValue || _session is not { } session)
        {
            return;
        }

        JsonElement recorderState = BuildRecorderState();
        JsonElement state = session.CaptureState(recorderState);
        session.Observe(sequence.Value, context, state);
        Settled?.Invoke(sequence.Value, context);
    }

    private static JsonElement BuildRecorderState() => JsonSerializer.SerializeToElement(new
    {
        rooms = RoomStack.ToArray(),
        player_choices = ChoiceOffers
            .OrderBy(pair => PlayerSlot(pair.Key.PlayerId), StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.ChoiceId)
            .Select(pair => new
            {
                id = $"{PlayerSlot(pair.Key.PlayerId)}:{pair.Key.ChoiceId}",
                player = PlayerSlot(pair.Key.PlayerId),
                choice_id = pair.Key.ChoiceId,
                offered = pair.Value,
            }).ToArray(),
        rewards = ActiveRewardSets
            .OrderBy(pair => PlayerSlot(pair.Key.PlayerId), StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.SetId)
            .Select(pair => new
            {
                id = $"{PlayerSlot(pair.Key.PlayerId)}:{pair.Key.SetId}",
                player = PlayerSlot(pair.Key.PlayerId),
                set_id = pair.Key.SetId,
                room = pair.Value.Room is null ? null : new
                {
                    id = pair.Value.Room.Id,
                    type = PortablePayloads.Symbol(pair.Value.Room.RoomType),
                    model_id = pair.Value.Room.ModelId?.ToString(),
                },
                disallow_skipping = pair.Value.DisallowSkipping,
                rewards = pair.Value.Rewards.Select((reward, index) => new
                {
                    index,
                    selected = reward.SuccessfullySelected,
                    fingerprint = PortablePayloads.RewardFingerprint(reward),
                }).ToArray(),
            }).ToArray(),
        special_event = _specialEventState,
    });

    private static string? PotionModel(ulong playerId, uint index)
    {
        Player? player = _state?.Players.FirstOrDefault(candidate => candidate.NetId == playerId);
        return player is not null && index < (uint)player.PotionSlots.Count
            ? player.PotionSlots[(int)index]?.Id.ToString()
            : null;
    }

    private static object? CombatTarget(uint? combatId)
    {
        if (!combatId.HasValue)
        {
            return null;
        }

        var creature = CombatManager.Instance.DebugOnlyGetState()?.GetCreature(combatId);
        return new
        {
            combat_id = combatId.Value,
            model_id = creature?.ModelId.ToString(),
        };
    }

    private static void StopSession()
    {
        if (_session is null)
        {
            _state = null;
            RoomStack.Clear();
            ChoiceOffers.Clear();
            LiveChoiceOffers.Clear();
            ActiveCardRewards.Clear();
            ActiveRewardSets.Clear();
            PendingRestActions.Clear();
            ActionSequences.Clear();
            _lastDecisionSequence = null;
            _pendingEndTurnSequence = null;
            _pendingUndoEndTurnSequence = null;
            _pendingEventChoiceSequence = null;
            _pendingEventAdvanceSequence = null;
            _pendingCrystalSphereSequence = null;
            _specialEventState = null;
            return;
        }

        try
        {
            RunManager.Instance.ActionQueueSet.ActionEnqueued -= OnActionEnqueued;
            RunManager.Instance.ActionExecutor.AfterActionExecuted -= OnActionAfterExecuted;
            CombatManager.Instance.TurnStarted -= OnTurnStarted;
            CombatManager.Instance.TurnEnded -= OnTurnEnded;
            CombatManager.Instance.PlayerUnendedTurn -= OnPlayerUnendedTurn;
            RunManager.Instance.PlayerChoiceSynchronizer.PlayerChoiceReceived -= OnPlayerChoiceReceived;
            RunManager.Instance.RestSiteSynchronizer.BeforePlayerOptionChosen -= OnBeforeRestSiteOptionChosen;
            RunManager.Instance.RestSiteSynchronizer.AfterPlayerOptionChosen -= OnAfterRestSiteOptionChosen;
        }
        catch
        {
            // A partially initialized or already cleaned-up RunManager has nothing left to unsubscribe.
        }

        _session.Dispose();
        _session = null;
        _state = null;
        RoomStack.Clear();
        ChoiceOffers.Clear();
        LiveChoiceOffers.Clear();
        ActiveCardRewards.Clear();
        ActiveRewardSets.Clear();
        PendingRestActions.Clear();
        ActionSequences.Clear();
        _lastDecisionSequence = null;
        _pendingEndTurnSequence = null;
        _pendingUndoEndTurnSequence = null;
        _pendingEventChoiceSequence = null;
        _pendingEventAdvanceSequence = null;
        _pendingCrystalSphereSequence = null;
        _specialEventState = null;
    }

    private static ulong? LocalPlayerId() => _state?.Players.FirstOrDefault()?.NetId;

    private static void ReportError(string operation, Exception exception) =>
        GD.PrintErr($"[STS2PGN] Error while {operation}: {exception}");

    /// <summary>A registered choice offer plus the live models it was built from (cards, bundles, relics, or a CardReward).</summary>
    public sealed record LiveChoiceOffer(string Context, JsonElement Offered, object? Models, bool RecordedInTrace = true);

    private sealed record RoomTraceData(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("stack_depth")] int StackDepth);
}
