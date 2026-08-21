using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using STS2PGN.Mod.Recording;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback.Dispatch;

/// <summary>reward.claim, reward.skip, treasure.choose_relic, treasure.skip_relic.</summary>
internal sealed class RewardDispatcher : IDecisionDispatcher
{
    public bool Handles(string kind) => kind is "reward.claim" or "reward.skip" or "treasure.choose_relic" or "treasure.skip_relic";

    public bool IsReady(ScriptDecision decision) => decision.Kind switch
    {
        "reward.claim" or "reward.skip" => LiveGame.TopOverlay is NRewardsScreen && LiveGame.LocalPlayer is { } me && RecorderService.ActiveRewardSetsFor(me.NetId).Any(),
        "treasure.choose_relic" => RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is not null && RelicHolders().Count > 0,
        "treasure.skip_relic" => RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is not null
            && (RelicHolders().Count > 0 || TreasureSkipButton() is not null),
        _ => false,
    };

    /// <summary>
    /// The treasure chest is opened by an unrecorded click. Before it opens, the relic offer already
    /// exists on the synchronizer, but the screen has no relic holders: a direct <c>PickRelicLocally</c>
    /// is accepted (and recorded) while <c>NTreasureRoomRelicCollection.AnimateRelicAwards</c>, which
    /// is what actually obtains the relic and re-enables Proceed, fails on the empty holder list.
    /// Click the chest the way a player would and wait for the holders to appear.
    /// </summary>
    public bool TryAdvanceSurface(ScriptDecision decision)
    {
        if (decision.Kind is not ("treasure.choose_relic" or "treasure.skip_relic")
            || LiveGame.TopOverlay is not null
            || NRun.Instance?.TreasureRoom is not { } room
            || RelicCollection(room) is { Visible: true })
        {
            return false;
        }

        NButton? chest = room.GetNodeOrNull<NButton>("%Chest");
        if (!LiveGame.IsUsable(chest))
        {
            return false;
        }

        chest!.ForceClick();
        return true;
    }

    public VerifyResult Verify(ScriptDecision decision) => decision.Kind switch
    {
        "reward.claim" => VerifyClaim(decision),
        "reward.skip" => VerifySkip(decision),
        "treasure.choose_relic" => VerifyRelic(decision),
        _ => VerifyResult.Success,
    };

    /// <summary>
    /// Rewards are taken through the loot screen's own buttons, not the synchronizer: only
    /// <c>NRewardButton.GetReward</c> emits <c>RewardClaimed</c>, which is what makes
    /// <c>NRewardsScreen</c> drop the button and (for a non-terminal screen, e.g. an event's loot)
    /// remove itself once every reward is gone. A direct synchronizer call leaves the screen up
    /// with stale buttons and the run never reaches the next decision.
    /// </summary>
    public Task SubmitAsync(ScriptDecision decision)
    {
        switch (decision.Kind)
        {
            case "reward.claim":
            {
                Reward reward = ResolveReward(decision)!;
                if (!ClickRewardButton(reward))
                {
                    _ = RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward);
                }

                break;
            }

            case "reward.skip":
                if (!ClickSkipButton())
                {
                    RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
                }

                break;
            case "treasure.choose_relic":
            {
                // Click the relic holder (NTreasureRoomRelicCollection.PickRelic → PickRelicLocally),
                // exactly as a player would; the synchronizer call is the fallback.
                ScriptDecisionReader.TryGetInt(decision.Data, "relic_index", out int index);
                NTreasureRoomRelicHolder? holder = RelicHolders().FirstOrDefault(candidate => candidate.Index == index);
                if (LiveGame.IsUsable(holder))
                {
                    holder!.ForceClick();
                }
                else
                {
                    RunManager.Instance.TreasureRoomRelicSynchronizer.PickRelicLocally(index);
                }

                break;
            }

            case "treasure.skip_relic":
                // The Skip-labelled Proceed button also proceeds from the room in singleplayer
                // (that is the following room.proceed decision), so skip through the synchronizer
                // only; the room then re-enables Proceed for room.proceed to click.
                RunManager.Instance.TreasureRoomRelicSynchronizer.SkipRelicLocally();
                break;
        }

        return Task.CompletedTask;
    }

    private static readonly AccessTools.FieldRef<NRewardsScreen, bool> IsTerminalScreen =
        AccessTools.FieldRefAccess<NRewardsScreen, bool>("_isTerminal");

    private static bool ClickRewardButton(Reward reward)
    {
        if (LiveGame.TopOverlay is not NRewardsScreen screen)
        {
            return false;
        }

        NRewardButton? button = LiveGame.FindAll<NRewardButton>(screen).FirstOrDefault(candidate => ReferenceEquals(candidate.Reward, reward));
        if (!LiveGame.IsUsable(button))
        {
            return false;
        }

        button!.ForceClick();
        return true;
    }

    /// <summary>
    /// The Skip button of a non-terminal loot screen is exactly <c>SkipLocalRewardsSet</c> plus
    /// closing the screen. On a terminal screen the same button proceeds from the room instead
    /// (recorded as <c>room.proceed</c>), so a scripted <c>reward.skip</c> there falls back to the synchronizer.
    /// </summary>
    private static bool ClickSkipButton()
    {
        if (LiveGame.TopOverlay is not NRewardsScreen screen || IsTerminalScreen(screen))
        {
            return false;
        }

        NProceedButton? button = LiveGame.FindAll<NProceedButton>(screen).FirstOrDefault(candidate => candidate.IsSkip);
        if (!LiveGame.IsUsable(button))
        {
            return false;
        }

        button!.ForceClick();
        return true;
    }

    private static RewardsSet? ResolveSet(ScriptDecision decision)
    {
        Player? me = LiveGame.LocalPlayer;
        if (me is null || !ScriptDecisionReader.TryGetInt(decision.Data, "set_id", out int setId))
        {
            return null;
        }

        return RecorderService.TryGetActiveRewardSet(me.NetId, setId, out RewardsSet set) ? set : null;
    }

    private static Reward? ResolveReward(ScriptDecision decision)
    {
        RewardsSet? set = ResolveSet(decision);
        if (set is null || !ScriptDecisionReader.TryGetInt(decision.Data, "reward_index", out int index) || index < 0 || index >= set.Rewards.Count)
        {
            return null;
        }

        return set.Rewards[index];
    }

    private static object SetListing()
    {
        Player? me = LiveGame.LocalPlayer;
        return me is null ? Array.Empty<object>() : RecorderService.ActiveRewardSetsFor(me.NetId).Select(set => new
        {
            set_id = set.Id,
            disallow_skipping = set.DisallowSkipping,
            rewards = set.Rewards.Select((reward, index) => new
            {
                reward_index = index,
                selected = reward.SuccessfullySelected,
                reward = PortablePayloads.RewardFingerprint(reward),
            }).ToArray(),
        }).ToArray();
    }

    private static VerifyResult VerifyClaim(ScriptDecision decision)
    {
        object offered = SetListing();
        if (ResolveSet(decision) is null)
        {
            return VerifyResult.Mismatch("the scripted reward set is not active", decision.Data, offered);
        }

        Reward? reward = ResolveReward(decision);
        if (reward is null)
        {
            return VerifyResult.Mismatch("the scripted reward index does not exist in the set", decision.Data, offered);
        }

        if (reward.SuccessfullySelected)
        {
            return VerifyResult.Mismatch("the scripted reward was already claimed", decision.Data, offered);
        }

        if (decision.Data.TryGetProperty("reward", out var expected)
            && !LiveGame.FingerprintMatches(expected, PortablePayloads.RewardFingerprint(reward)))
        {
            return VerifyResult.Mismatch("the reward at the scripted index has a different fingerprint", decision.Data, offered);
        }

        return VerifyResult.Success;
    }

    private static VerifyResult VerifySkip(ScriptDecision decision)
    {
        object offered = SetListing();
        RewardsSet? set = ResolveSet(decision);
        if (set is null)
        {
            return VerifyResult.Mismatch("the scripted reward set is not active", decision.Data, offered);
        }

        return set.DisallowSkipping
            ? VerifyResult.Mismatch("the reward set cannot be skipped", decision.Data, offered)
            : VerifyResult.Success;
    }

    private static NTreasureRoomRelicCollection? RelicCollection(NTreasureRoom room) =>
        room.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");

    /// <summary>Visible, enabled relic holders of the opened chest (empty until the chest is open).</summary>
    private static List<NTreasureRoomRelicHolder> RelicHolders()
    {
        if (NRun.Instance?.TreasureRoom is not { } room || RelicCollection(room) is not { Visible: true } collection)
        {
            return [];
        }

        return LiveGame.FindAll<NTreasureRoomRelicHolder>(collection).Where(LiveGame.IsUsable).ToList();
    }

    private static NProceedButton? TreasureSkipButton()
    {
        NProceedButton? button = NRun.Instance?.TreasureRoom?.ProceedButton;
        return LiveGame.IsUsable(button) && button!.IsSkip ? button : null;
    }

    private static VerifyResult VerifyRelic(ScriptDecision decision)
    {
        IReadOnlyList<RelicModel> relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics!;
        object offered = relics.Select((relic, index) => new { index, model_id = relic.Id.ToString() }).ToArray();
        if (decision.Data.TryGetProperty("offered_relics", out var expectedOffer) && !LiveGame.FingerprintMatches(expectedOffer, offered))
        {
            return VerifyResult.Mismatch("the offered relics differ from the script", decision.Data, offered);
        }

        if (!ScriptDecisionReader.TryGetInt(decision.Data, "relic_index", out int index) || index < 0 || index >= relics.Count
            || !string.Equals(relics[index].Id.ToString(), ScriptDecisionReader.GetString(decision.Data, "relic_model"), StringComparison.Ordinal))
        {
            return VerifyResult.Mismatch("the scripted relic is not at that index", decision.Data, offered);
        }

        return VerifyResult.Success;
    }
}
