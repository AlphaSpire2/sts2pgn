using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Model;

namespace STS2PGN.Mod.Recording;

internal static class RecorderService
{
    private static RecordingSession? _session;
    private static RunState? _state;
    private static RecorderConfig _config = new(RecorderConfig.DefaultOutputDirectory);
    private static readonly List<RoomTraceData> RoomStack = [];
    private static readonly Dictionary<(ulong PlayerId, uint ChoiceId), JsonElement> ChoiceOffers = [];
    private static readonly Dictionary<ulong, CardReward> ActiveCardRewards = [];
    private static readonly Dictionary<ulong, long> PendingRestActions = [];
    private static bool _nextRunIsResume;

    public static void Initialize()
    {
        _config = RecorderConfig.Load();
        RunManager manager = RunManager.Instance;
        manager.RunStarted += OnRunStarted;
        manager.RoomEntered += OnRoomEntered;
        manager.RoomExited += OnRoomExited;
        manager.ActEntered += () => TryRecord("act.enter", new { act = _state?.CurrentActIndex });
    }

    public static void Finish(bool isVictory, bool isAbandoned)
    {
        RunResult result = isAbandoned ? RunResult.Abandoned : isVictory ? RunResult.Victory : RunResult.Defeat;
        try
        {
            _session?.Complete(result);
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

    public static void MarkNextRunAsResume() => _nextRunIsResume = true;

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
            TryRecord("event.choose", new
            {
                event_id = currentEvent?.Id.ToString(),
                option_index = optionIndex,
                option_id = option.TextKey,
                is_proceed = option.IsProceed,
            }, LocalPlayerId());
        });
    }

    public static void RecordCrystalSphereTool(string tool) =>
        TryRecord("event.crystal_sphere.set_tool", new { tool }, LocalPlayerId());

    public static void RecordCrystalSphereClick(int x, int y, string tool, int remainingBefore) =>
        TryRecord("event.crystal_sphere.click", new { x, y, tool, remaining_before = remainingBefore }, LocalPlayerId());

    public static void RecordCrystalSphereProceed() =>
        TryRecord("event.crystal_sphere.proceed", new { }, LocalPlayerId());

    public static void RecordRoomProceed()
    {
        ExecuteSafely("capturing room proceed", () =>
        {
            if (_state?.CurrentRoom?.ModelId?.Entry != "CRYSTAL_SPHERE")
            {
                TryRecord("room.proceed", new { }, LocalPlayerId());
            }
        });
    }

    public static void RecordEventAdvance() => ExecuteSafely("capturing event dialogue", () =>
        TryRecord("event.advance", new
        {
            event_id = RunManager.Instance.EventSynchronizer.Events.FirstOrDefault()?.Id.ToString(),
        }, LocalPlayerId()));

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
            return _session?.Record(kind, data, actorId);
        }
        catch (Exception exception)
        {
            ReportError($"recording {kind}", exception);
            return null;
        }
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

        TryRecord("reward.resolve", new
        {
            action_sequence = actionSequence,
            success = succeeded,
        }, playerId);
    }

    public static void RecordRewardSkip(RewardsSet set) =>
        TryRecord("reward.skip", new
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
            });
        }

        return sequence;
    }

    public static void ResolveShopCardRemoval(long? actionSequence, bool succeeded) =>
        ExecuteSafely("recording shop card removal outcome", () =>
        {
            if (actionSequence.HasValue)
            {
                TryRecord("shop.remove_card.resolve", new
                {
                    action_sequence = actionSequence.Value,
                    success = succeeded,
                }, LocalPlayerId());
            }
        });

    public static uint? RegisterUpcomingChoice(Player player, string context, object options)
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
        ChoiceOffers[(player.NetId, choiceId)] = JsonSerializer.SerializeToElement(new
        {
            context,
            options,
        });
        return choiceId;
    }

    public static void RegisterReservedChoice(Player player, uint choiceId)
    {
        if (!ActiveCardRewards.TryGetValue(player.NetId, out CardReward? reward))
        {
            return;
        }

        ChoiceOffers[(player.NetId, choiceId)] = JsonSerializer.SerializeToElement(new
        {
            context = "card_reward",
            options = new
            {
                cards = reward.Cards.Select((card, index) => new
                {
                    index,
                    fingerprint = PortablePayloads.CardFingerprint(card),
                }).ToArray(),
                alternatives = new[]
                {
                    reward.CanSkip ? "skip" : null,
                    reward.CanReroll ? "reroll" : null,
                }.Where(value => value is not null).ToArray(),
            },
        });
    }

    public static void ClearUpcomingChoice(Player player, uint? choiceId)
    {
        if (!choiceId.HasValue)
        {
            return;
        }

        ChoiceOffers.Remove((player.NetId, choiceId.Value));
    }

    private static void OnRunStarted(RunState state)
    {
        ExecuteSafely("closing previous trace", StopSession);
        if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            GD.Print("[STS2PGN] Multiplayer recording is not supported by format version 1");
            return;
        }

        try
        {
            _state = state;
            _session = RecordingSession.Start(state, _config);
            RoomStack.Clear();
            ChoiceOffers.Clear();
            ActiveCardRewards.Clear();
            PendingRestActions.Clear();
            RunManager.Instance.ActionQueueSet.ActionEnqueued += OnActionEnqueued;
            RunManager.Instance.PlayerChoiceSynchronizer.PlayerChoiceReceived += OnPlayerChoiceReceived;
            RunManager.Instance.RestSiteSynchronizer.BeforePlayerOptionChosen += OnBeforeRestSiteOptionChosen;
            RunManager.Instance.RestSiteSynchronizer.AfterPlayerOptionChosen += OnAfterRestSiteOptionChosen;
            if (_nextRunIsResume)
            {
                TryRecord("run.resume", new { });
            }

            _nextRunIsResume = false;
            GD.Print($"[STS2PGN] Recording to {_session.Path}");
        }
        catch (Exception exception)
        {
            _nextRunIsResume = false;
            StopSession();
            ReportError("starting recorder", exception);
        }
    }

    private static void OnActionEnqueued(GameAction action)
    {
        action.BeforeExecuted += OnActionBeforeExecuted;
    }

    private static void OnActionBeforeExecuted(GameAction action) =>
        ExecuteSafely("capturing game action", () => RecordAction(action));

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
                }, actor);
                break;
            case NetUsePotionAction potion:
                TryRecord("combat.use_potion", new
                {
                    potion_slot = potion.potionIndex,
                    potion_model = PotionModel(actor, potion.potionIndex),
                    target_id = potion.targetId,
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
                TryRecord("combat.end_turn", new { turn = endTurn.turnNumber }, actor);
                break;
            case NetUndoEndPlayerTurnAction undo:
                TryRecord("combat.undo_end_turn", new { turn = undo.turnNumber }, actor);
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

        TryRecord("rest.resolve", new
        {
            action_sequence = sequence,
            option_id = option.OptionId,
            success = succeeded,
        }, playerId);
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

        RoomTraceData data = new(
            room.Id,
            room.RoomType.ToString().ToUpperInvariant(),
            room.ModelId?.ToString(),
            _state!.CurrentRoomCount);
        RoomStack.Add(data);
        TryRecord("room.enter", data);
    }

    private static void OnRoomExited()
    {
        RoomTraceData? data = RoomStack.Count > 0 ? RoomStack[^1] : null;
        if (RoomStack.Count > 0)
        {
            RoomStack.RemoveAt(RoomStack.Count - 1);
        }

        TryRecord("room.exit", data);
    }

    private static string? PotionModel(ulong playerId, uint index)
    {
        Player? player = _state?.Players.FirstOrDefault(candidate => candidate.NetId == playerId);
        return player is not null && index < (uint)player.PotionSlots.Count
            ? player.PotionSlots[(int)index]?.Id.ToString()
            : null;
    }

    private static void StopSession()
    {
        if (_session is null)
        {
            _state = null;
            return;
        }

        try
        {
            RunManager.Instance.ActionQueueSet.ActionEnqueued -= OnActionEnqueued;
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
        ActiveCardRewards.Clear();
        PendingRestActions.Clear();
    }

    private static ulong? LocalPlayerId() => _state?.Players.FirstOrDefault()?.NetId;

    private static void ReportError(string operation, Exception exception) =>
        GD.PrintErr($"[STS2PGN] Error while {operation}: {exception}");

    private sealed record RoomTraceData(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("stack_depth")] int StackDepth);
}
