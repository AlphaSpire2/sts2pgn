using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using STS2PGN.Mod.Recording;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>
/// choice.index and choice.cards. A nested choice is answered by completing the game's own selection
/// screen (pressing the live holders and confirming), which makes the suspended command call
/// <c>PlayerChoiceSynchronizer.SyncLocalChoice</c> itself; the recorder then records the accepted result.
/// </summary>
internal sealed class ChoiceDispatcher : IDecisionDispatcher
{
    /// <summary>Selection screens drop presses within ~350 ms of opening; give them time.</summary>
    public static readonly TimeSpan ScreenSettleDelay = TimeSpan.FromMilliseconds(450);

    public bool Handles(string kind) => kind is "choice.index" or "choice.cards";

    public bool IsReady(ScriptDecision decision)
    {
        Player? me = LiveGame.LocalPlayer;
        if (me is null)
        {
            return false;
        }

        if (IsHandSelection(decision))
        {
            return NPlayerHand.Instance is { IsInCardSelection: true };
        }

        return PendingOffer(me, decision) is { } pending && ScreenFor(pending.Offer) is not null;
    }

    public VerifyResult Verify(ScriptDecision decision)
    {
        Player me = LiveGame.LocalPlayer!;
        if (IsHandSelection(decision))
        {
            return VerifyCardSelections(me, decision);
        }

        RecorderService.LiveChoiceOffer offer = PendingOffer(me, decision)!.Value.Offer;
        if (decision.Kind == "choice.index")
        {
            if (!decision.Data.TryGetProperty("offered", out JsonElement expectedOffer) || !LiveGame.SameJson(expectedOffer, offer.Offered))
            {
                return VerifyResult.Mismatch("the game offers different options than the script expects", decision.Data, offer.Offered);
            }

            int optionCount = OptionCount(offer);
            foreach (int index in ScriptDecisionReader.GetSelectedIndexes(decision.Data))
            {
                if (index < 0 || index >= optionCount)
                {
                    return VerifyResult.Mismatch($"selected index {index} is outside the {optionCount} offered options", decision.Data, offer.Offered);
                }
            }

            return VerifyResult.Success;
        }

        if (decision.Data.TryGetProperty("offered", out JsonElement expectedCardOffer)
            && expectedCardOffer.ValueKind == JsonValueKind.Object
            && offer.RecordedInTrace
            && !LiveGame.SameJson(expectedCardOffer, offer.Offered))
        {
            return VerifyResult.Mismatch("the game offers different cards than the script expects", decision.Data, offer.Offered);
        }

        return VerifyCardSelections(me, decision);
    }

    public async Task SubmitAsync(ScriptDecision decision)
    {
        Player me = LiveGame.LocalPlayer!;
        if (IsHandSelection(decision))
        {
            NPlayerHand hand = NPlayerHand.Instance!;
            foreach (CardModel card in SelectedCards(me, decision))
            {
                NHandCardHolder holder = hand.ActiveHolders.First(candidate => ReferenceEquals(candidate.CardModel, card));
                holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            }

            await Task.Delay(PlaybackWait.PollInterval);
            ClickIfUsable(hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton"));
            return;
        }

        RecorderService.LiveChoiceOffer offer = PendingOffer(me, decision)!.Value.Offer;
        Node screen = (Node)ScreenFor(offer)!;
        IReadOnlyList<int> indexes = ScriptDecisionReader.GetSelectedIndexes(decision.Data);
        switch (offer.Models)
        {
            case CardReward reward when screen is NCardRewardSelectionScreen rewardScreen:
            {
                List<CardModel> cards = reward.Cards.ToList();
                int index = indexes[0];
                if (index < cards.Count)
                {
                    NCardHolder holder = rewardScreen.GetCardHolder(cards[index]);
                    holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                }
                else
                {
                    LiveGame.FindAll<NCardRewardAlternativeButton>(rewardScreen)[index - cards.Count].ForceClick();
                }

                break;
            }

            case IReadOnlyList<IReadOnlyList<CardModel>> bundles when screen is NChooseABundleSelectionScreen bundleScreen:
            {
                IReadOnlyList<CardModel> bundle = bundles[indexes[0]];
                NCardBundle node = LiveGame.FindAll<NCardBundle>(bundleScreen).First(candidate => candidate.Bundle.SequenceEqual(bundle));
                node.Hitbox.ForceClick();
                await PlaybackWait.UntilAsync(
                    () => LiveGame.IsUsable(bundleScreen.GetNodeOrNull<NConfirmButton>("%Confirm")),
                    TimeSpan.FromSeconds(5),
                    "the bundle confirm button",
                    CancellationToken.None);
                bundleScreen.GetNodeOrNull<NConfirmButton>("%Confirm")!.ForceClick();
                break;
            }

            case IReadOnlyList<RelicModel> relics when screen is NChooseARelicSelection relicScreen:
            {
                if (indexes.Count == 0)
                {
                    relicScreen.GetNodeOrNull<NClickableControl>("SkipButton")!.ForceClick();
                    break;
                }

                RelicModel relic = relics[indexes[0]];
                LiveGame.FindAll<NRelicBasicHolder>(relicScreen).First(candidate => ReferenceEquals(candidate.Relic.Model, relic)).ForceClick();
                break;
            }

            case IReadOnlyList<CardModel> cards when screen is NChooseACardSelectionScreen chooseScreen:
            {
                if (indexes.Count == 0)
                {
                    chooseScreen.GetNodeOrNull<NClickableControl>("SkipButton")!.ForceClick();
                    break;
                }

                CardModel card = cards[indexes[0]];
                NGridCardHolder holder = LiveGame.FindAll<NGridCardHolder>(chooseScreen).First(candidate => ReferenceEquals(candidate.CardModel, card));
                holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                break;
            }

            case IReadOnlyList<CardModel> cards when screen is NCardGridSelectionScreen gridScreen:
            {
                IEnumerable<CardModel> selected = decision.Kind == "choice.index"
                    ? indexes.Select(index => cards[index])
                    : SelectedCards(me, decision);
                NCardGrid grid = LiveGame.FindFirst<NCardGrid>(gridScreen)
                    ?? throw new InvalidOperationException("The selection screen has no card grid.");
                bool any = false;
                foreach (CardModel card in selected)
                {
                    NGridCardHolder holder = grid.CurrentlyDisplayedCardHolders.First(candidate => ReferenceEquals(candidate.CardModel, card));
                    grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
                    any = true;
                }

                await Task.Delay(PlaybackWait.PollInterval);
                if (ReferenceEquals(LiveGame.TopOverlay, gridScreen))
                {
                    if (any)
                    {
                        ConfirmGrid(gridScreen);
                    }
                    else
                    {
                        CancelGrid(gridScreen);
                    }
                }

                break;
            }

            default:
                throw new InvalidOperationException($"No selection screen handler for offer context '{offer.Context}' on {screen.GetType().Name}.");
        }
    }

    private static bool IsHandSelection(ScriptDecision decision) =>
        decision.Kind == "choice.cards"
        && ScriptDecisionReader.GetString(decision.Data, "choice_type") is "combat_card" or "mutable_card"
        && NPlayerHand.Instance is { IsInCardSelection: true };

    private static ((ulong PlayerId, uint ChoiceId) Key, RecorderService.LiveChoiceOffer Offer)? PendingOffer(Player me, ScriptDecision decision)
    {
        var pending = RecorderService.PendingLiveOffers(me.NetId).ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        if (ScriptDecisionReader.TryGetInt(decision.Data, "choice_id", out int choiceId))
        {
            var exact = pending.FirstOrDefault(pair => pair.Key.ChoiceId == (uint)choiceId);
            if (exact.Value is not null)
            {
                return (exact.Key, exact.Value);
            }
        }

        var latest = pending.OrderByDescending(pair => pair.Key.ChoiceId).First();
        return (latest.Key, latest.Value);
    }

    private static object? ScreenFor(RecorderService.LiveChoiceOffer offer)
    {
        object? top = LiveGame.TopOverlay;
        return (offer.Models, top) switch
        {
            (CardReward, NCardRewardSelectionScreen) => top,
            (IReadOnlyList<IReadOnlyList<CardModel>>, NChooseABundleSelectionScreen) => top,
            (IReadOnlyList<RelicModel>, NChooseARelicSelection) => top,
            (IReadOnlyList<CardModel>, NChooseACardSelectionScreen) => top,
            (IReadOnlyList<CardModel>, NCardGridSelectionScreen) => top,
            _ => null,
        };
    }

    private static int OptionCount(RecorderService.LiveChoiceOffer offer) => offer.Models switch
    {
        CardReward reward => reward.Cards.Count() + AlternativeCount(offer.Offered),
        IReadOnlyList<IReadOnlyList<CardModel>> bundles => bundles.Count,
        IReadOnlyList<RelicModel> relics => relics.Count,
        IReadOnlyList<CardModel> cards => cards.Count,
        _ => 0,
    };

    private static int AlternativeCount(JsonElement offered) =>
        offered.TryGetProperty("options", out JsonElement options)
        && options.ValueKind == JsonValueKind.Object
        && options.TryGetProperty("alternatives", out JsonElement alternatives)
        && alternatives.ValueKind == JsonValueKind.Array
            ? alternatives.GetArrayLength()
            : 0;

    private static IEnumerable<CardModel> SelectedCards(Player me, ScriptDecision decision)
    {
        foreach (ScriptCardSelection selection in ScriptDecisionReader.GetCardSelections(decision.Data))
        {
            yield return ResolveCard(me, selection) ?? throw new InvalidOperationException("A scripted card could not be resolved.");
        }
    }

    private static CardModel? ResolveCard(Player me, ScriptCardSelection selection)
    {
        if (selection.DeckIndex is { } deckIndex)
        {
            return deckIndex >= 0 && deckIndex < me.Deck.Cards.Count ? me.Deck.Cards[deckIndex] : null;
        }

        if (selection.CombatCardIndex is { } combatIndex && combatIndex >= 0
            && NetCombatCardDb.Instance.TryGetCard((uint)combatIndex, out CardModel? card))
        {
            return card;
        }

        return null;
    }

    private static VerifyResult VerifyCardSelections(Player me, ScriptDecision decision)
    {
        IReadOnlyList<ScriptCardSelection> selections = ScriptDecisionReader.GetCardSelections(decision.Data);
        object offered = new
        {
            deck = me.Deck.Cards.Select((card, index) => new { deck_index = index, fingerprint = PortablePayloads.CardFingerprint(card) }).ToArray(),
            hand = me.PlayerCombatState?.Hand.Cards.Select(card => new
            {
                combat_card_index = NetCombatCardDb.Instance.TryGetCardId(card, out uint id) ? id : (uint?)null,
                fingerprint = PortablePayloads.CardFingerprint(card),
            }).ToArray(),
        };
        foreach (ScriptCardSelection selection in selections)
        {
            CardModel? card = ResolveCard(me, selection);
            if (card is null)
            {
                return VerifyResult.Mismatch("a scripted card index does not exist", decision.Data, offered);
            }

            if (!LiveGame.FingerprintMatches(selection.Fingerprint, PortablePayloads.CardFingerprint(card)))
            {
                return VerifyResult.Mismatch("the card at a scripted index has a different fingerprint", decision.Data, offered);
            }
        }

        return VerifyResult.Success;
    }

    private static void ConfirmGrid(NCardGridSelectionScreen screen)
    {
        foreach (string container in new[] { "%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer", "%EnchantSinglePreviewContainer", "%EnchantMultiPreviewContainer", "%PreviewContainer" })
        {
            Control? preview = screen.GetNodeOrNull<Control>(container);
            if (preview is { Visible: true }
                && (ClickIfUsable(preview.GetNodeOrNull<NConfirmButton>("Confirm")) || ClickIfUsable(screen.GetNodeOrNull<NConfirmButton>("%PreviewConfirm"))))
            {
                return;
            }
        }

        if (ClickIfUsable(screen.GetNodeOrNull<NConfirmButton>("%Confirm")) || ClickIfUsable(screen.GetNodeOrNull<NConfirmButton>("Confirm")))
        {
            return;
        }

        NConfirmButton? fallback = LiveGame.FindAll<NConfirmButton>(screen).FirstOrDefault(LiveGame.IsUsable);
        if (!ClickIfUsable(fallback))
        {
            throw new InvalidOperationException("No usable confirm button on the selection screen.");
        }
    }

    private static void CancelGrid(NCardGridSelectionScreen screen)
    {
        if (ClickIfUsable(screen.GetNodeOrNull<NBackButton>("%Close"))
            || ClickIfUsable(LiveGame.FindAll<NBackButton>(screen).FirstOrDefault(LiveGame.IsUsable)))
        {
            return;
        }

        throw new InvalidOperationException("No usable cancel button on the selection screen.");
    }

    private static bool ClickIfUsable(NClickableControl? control)
    {
        if (!LiveGame.IsUsable(control))
        {
            return false;
        }

        control!.ForceClick();
        return true;
    }
}
