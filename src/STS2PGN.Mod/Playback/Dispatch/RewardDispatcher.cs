using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;
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
        _ => RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is not null,
    };

    public VerifyResult Verify(ScriptDecision decision) => decision.Kind switch
    {
        "reward.claim" => VerifyClaim(decision),
        "reward.skip" => VerifySkip(decision),
        "treasure.choose_relic" => VerifyRelic(decision),
        _ => VerifyResult.Success,
    };

    public Task SubmitAsync(ScriptDecision decision)
    {
        switch (decision.Kind)
        {
            case "reward.claim":
                _ = RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(ResolveReward(decision)!);
                break;
            case "reward.skip":
                RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
                break;
            case "treasure.choose_relic":
                ScriptDecisionReader.TryGetInt(decision.Data, "relic_index", out int index);
                RunManager.Instance.TreasureRoomRelicSynchronizer.PickRelicLocally(index);
                break;
            case "treasure.skip_relic":
                RunManager.Instance.TreasureRoomRelicSynchronizer.SkipRelicLocally();
                break;
        }

        return Task.CompletedTask;
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
