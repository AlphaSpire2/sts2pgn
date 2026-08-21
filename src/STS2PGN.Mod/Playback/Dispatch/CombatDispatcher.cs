using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Mod.Recording;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>combat.play_card, combat.use_potion, combat.discard_potion, combat.end_turn.</summary>
internal sealed class CombatDispatcher : IDecisionDispatcher
{
    public bool Handles(string kind) => kind is "combat.play_card" or "combat.use_potion" or "combat.discard_potion" or "combat.end_turn";

    public bool IsReady(ScriptDecision decision)
    {
        Player? me = LiveGame.LocalPlayer;
        if (me is null)
        {
            return false;
        }

        bool inCombat = ScriptDecisionReader.TryGetBool(decision.Data, "in_combat", out bool flag) ? flag : true;
        if (decision.Kind is "combat.use_potion" or "combat.discard_potion" && !inCombat)
        {
            return !CombatManager.Instance.IsInProgress;
        }

        CombatState? combat = LiveGame.Combat;
        return combat is not null
            && combat.CurrentSide == CombatSide.Player
            && me.PlayerCombatState is not null
            && !CombatManager.Instance.IsPlayerReadyToEndTurn(me)
            && !CombatManager.Instance.IsStarting
            && !CombatManager.Instance.IsOverOrEnding
            && NPlayerHand.Instance is { InCardPlay: false, IsInCardSelection: false };
    }

    public VerifyResult Verify(ScriptDecision decision) => decision.Kind switch
    {
        "combat.play_card" => VerifyPlay(decision),
        "combat.use_potion" or "combat.discard_potion" => VerifyPotion(decision),
        "combat.end_turn" => VerifyEndTurn(decision),
        _ => VerifyResult.Mismatch($"unsupported kind {decision.Kind}", null, null),
    };

    public Task SubmitAsync(ScriptDecision decision)
    {
        Player me = LiveGame.LocalPlayer!;
        switch (decision.Kind)
        {
            case "combat.play_card":
            {
                CardModel card = ResolveHandCard(me, decision)!;
                Creature? target = ResolveTarget(decision);
                if (!card.TryManualPlay(target))
                {
                    throw new InvalidOperationException($"The game refused to play {card.Id} on {target?.ModelId.ToString() ?? "no target"}.");
                }

                break;
            }

            case "combat.use_potion":
            {
                ScriptDecisionReader.TryGetInt(decision.Data, "potion_slot", out int slot);
                PotionModel potion = me.PotionSlots[slot]!;
                potion.EnqueueManualUse(ResolveTarget(decision));
                break;
            }

            case "combat.discard_potion":
            {
                ScriptDecisionReader.TryGetInt(decision.Data, "potion_slot", out int slot);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new DiscardPotionGameAction(me, (uint)slot, CombatManager.Instance.IsInProgress));
                break;
            }

            case "combat.end_turn":
                PlayerCmd.EndTurn(me, canBackOut: false);
                break;
        }

        return Task.CompletedTask;
    }

    private static VerifyResult VerifyPlay(ScriptDecision decision)
    {
        Player me = LiveGame.LocalPlayer!;
        object offeredHand = HandListing(me);
        CardModel? card = ResolveHandCard(me, decision);
        if (card is null)
        {
            return VerifyResult.Mismatch("the scripted combat card is not in hand", decision.Data, offeredHand);
        }

        string? expectedModel = ScriptDecisionReader.GetString(decision.Data, "card_model");
        if (!string.Equals(expectedModel, card.Id.ToString(), StringComparison.Ordinal)
            || (decision.Data.TryGetProperty("card", out var expectedCard) && !LiveGame.FingerprintMatches(expectedCard, PortablePayloads.CardFingerprint(card))))
        {
            return VerifyResult.Mismatch(
                "the card at the scripted hand index has a different identity",
                decision.Data,
                new { combat_card_index = NetCombatCardDb.Instance.GetCardId(card), card_model = card.Id.ToString(), card = PortablePayloads.CardFingerprint(card), hand = offeredHand });
        }

        return VerifyTarget(decision);
    }

    private static VerifyResult VerifyPotion(ScriptDecision decision)
    {
        Player me = LiveGame.LocalPlayer!;
        object offered = me.PotionSlots.Select((potion, index) => new { potion_slot = index, potion_model = potion?.Id.ToString() }).ToArray();
        if (!ScriptDecisionReader.TryGetInt(decision.Data, "potion_slot", out int slot) || slot < 0 || slot >= me.PotionSlots.Count)
        {
            return VerifyResult.Mismatch("the scripted potion slot does not exist", decision.Data, offered);
        }

        string? expected = ScriptDecisionReader.GetString(decision.Data, "potion_model");
        string? live = me.PotionSlots[slot]?.Id.ToString();
        if (live is null || !string.Equals(expected, live, StringComparison.Ordinal))
        {
            return VerifyResult.Mismatch("the potion in the scripted slot differs", decision.Data, offered);
        }

        return decision.Kind == "combat.use_potion" ? VerifyTarget(decision) : VerifyResult.Success;
    }

    private static VerifyResult VerifyEndTurn(ScriptDecision decision)
    {
        Player me = LiveGame.LocalPlayer!;
        int liveTurn = me.PlayerCombatState?.TurnNumber ?? -1;
        if (!ScriptDecisionReader.TryGetInt(decision.Data, "turn", out int turn) || turn != liveTurn)
        {
            return VerifyResult.Mismatch("the scripted turn number differs from the live turn", decision.Data, new { turn = liveTurn });
        }

        return VerifyResult.Success;
    }

    private static VerifyResult VerifyTarget(ScriptDecision decision)
    {
        bool hasTargetId = decision.Data.TryGetProperty("target_id", out var targetId) && targetId.ValueKind == System.Text.Json.JsonValueKind.Number;
        ScriptTarget? expected = ScriptDecisionReader.GetTarget(decision.Data);
        if (!hasTargetId || expected is null)
        {
            return VerifyResult.Success;
        }

        CombatState? combat = LiveGame.Combat;
        Creature? creature = combat?.GetCreature(expected.CombatId);
        object offered = combat?.Creatures.Select(candidate => new { combat_id = candidate.CombatId, model_id = candidate.ModelId.ToString() }).ToArray() ?? [];
        if (creature is null || !string.Equals(creature.ModelId.ToString(), expected.ModelId, StringComparison.Ordinal))
        {
            return VerifyResult.Mismatch("the scripted target is not the creature the game has at that combat id", expected, offered);
        }

        return VerifyResult.Success;
    }

    private static CardModel? ResolveHandCard(Player me, ScriptDecision decision)
    {
        if (!ScriptDecisionReader.TryGetInt(decision.Data, "combat_card_index", out int index) || index < 0
            || !NetCombatCardDb.Instance.TryGetCard((uint)index, out CardModel? card)
            || card is null
            || me.PlayerCombatState is null
            || !me.PlayerCombatState.Hand.Cards.Contains(card))
        {
            return null;
        }

        return card;
    }

    private static Creature? ResolveTarget(ScriptDecision decision)
    {
        ScriptTarget? target = ScriptDecisionReader.GetTarget(decision.Data);
        return target is null ? null : LiveGame.Combat?.GetCreature(target.CombatId);
    }

    private static object HandListing(Player me) =>
        me.PlayerCombatState?.Hand.Cards.Select(card => new
        {
            combat_card_index = NetCombatCardDb.Instance.TryGetCardId(card, out uint id) ? id : (uint?)null,
            card_model = card.Id.ToString(),
            card = PortablePayloads.CardFingerprint(card),
        }).ToArray() ?? [];
}
