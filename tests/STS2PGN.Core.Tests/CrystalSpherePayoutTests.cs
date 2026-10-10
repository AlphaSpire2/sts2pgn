using STS2PGN.Mod.Recording;

namespace STS2PGN.Tests;

public sealed class CrystalSpherePayoutTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task Final_click_waits_for_generated_offer_but_not_reward_claims(bool delayed, int rewardCount)
    {
        var payout = new CrystalSpherePayout();
        object session = new(), minigame = new();
        int rngCounter = 10;
        int? observedCounter = null;
        payout.Start(session, minigame, 7, () => rngCounter);
        payout.Clicked(session, minigame, 42, remainingBefore: 1);
        var presentation = new TaskCompletionSource();
        var claims = new TaskCompletionSource();
        var offered = new TaskCompletionSource();
        bool rewardsObserved = false;

        async Task CompleteMinigame()
        {
            if (delayed) await presentation.Task;
            rngCounter += rewardCount;
            rewardsObserved = payout.ObserveRewards(session, 7, state =>
            {
                Assert.False(payout.HasCompleted(session, 42));
                observedCounter = (int)state;
            });
            offered.SetResult();
            if (rewardCount != 0) await claims.Task;
        }

        // Mirrors the prefix/original/finalizer boundary: the original async
        // method captures the scope, then the caller's context is restored.
        Task completion;
        using (payout.EnterPayout(session, minigame)) completion = CompleteMinigame();

        Assert.False(payout.ObserveRewards(session, 7, _ => Assert.Fail("unrelated offer")));
        if (delayed)
        {
            Assert.False(payout.HasCompleted(session, 42));
            Assert.Null(observedCounter);
            presentation.SetResult();
        }

        // Await the offer through its own signal, never the reward lifetime.
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rewardsObserved);
        Assert.Equal(10 + rewardCount, observedCounter);
        Assert.True(payout.HasCompleted(session, 42));
        Assert.False(payout.HasCompleted(session, 41));
        Assert.False(payout.HasCompleted(new object(), 42));
        if (rewardCount != 0) Assert.False(completion.IsCompleted);
        claims.SetResult();
        await completion;
    }

    [Fact]
    public void Non_final_click_does_not_arm_payout()
    {
        var payout = new CrystalSpherePayout();
        object session = new(), minigame = new();
        payout.Start(session, minigame, 7, () => new object());
        payout.Clicked(session, minigame, 41, remainingBefore: 2);
        using (payout.EnterPayout(session, minigame))
        {
            Assert.False(payout.ObserveRewards(session, 7, _ => Assert.Fail("not the final click")));
        }
        Assert.False(payout.HasCompleted(session, 41));
    }

    [Fact]
    public void Unrelated_player_board_and_session_cannot_settle_the_final_click()
    {
        var payout = new CrystalSpherePayout();
        object session = new(), minigame = new();
        payout.Start(session, minigame, 7, () => new object());
        payout.Clicked(session, minigame, 42, remainingBefore: 1);
        Assert.False(payout.ObserveRewards(session, 7, _ => Assert.Fail("unscoped")));
        using (payout.EnterPayout(session, minigame))
        {
            Assert.False(payout.ObserveRewards(session, 8, _ => Assert.Fail("another player")));
            Assert.False(payout.ObserveRewards(new object(), 7, _ => Assert.Fail("another session")));
            using (payout.EnterPayout(session, new object()))
            {
                Assert.False(payout.ObserveRewards(session, 7, _ => Assert.Fail("another board")));
            }
            Assert.False(payout.HasCompleted(session, 42));
            Assert.True(payout.ObserveRewards(session, 7, _ => { }));
            Assert.False(payout.ObserveRewards(session, 7, _ => Assert.Fail("duplicate")));
        }
        Assert.True(payout.HasCompleted(session, 42));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Teardown_invalidates_delayed_callbacks_even_when_sequence_is_reused(bool newSession)
    {
        var payout = new CrystalSpherePayout();
        object session = new(), minigame = new();
        payout.Start(session, minigame, 7, () => new object());
        payout.Clicked(session, minigame, 42, remainingBefore: 1);
        var presentation = new TaskCompletionSource();
        async Task<bool> OfferAfterDelay()
        {
            await presentation.Task;
            return payout.ObserveRewards(session, 7, _ => Assert.Fail("stale callback"));
        }
        Task<bool> oldOffer;
        using (payout.EnterPayout(session, minigame)) oldOffer = OfferAfterDelay();

        payout.Clear();
        Assert.False(payout.IsCurrent(session, minigame));
        object nextSession = newSession ? new object() : session;
        object nextMinigame = new();
        payout.Start(nextSession, nextMinigame, 7, () => new object());
        payout.Clicked(nextSession, nextMinigame, 42, remainingBefore: 1);
        presentation.SetResult();
        Assert.False(await oldOffer);
        Assert.False(payout.HasCompleted(nextSession, 42));
        using (payout.EnterPayout(nextSession, nextMinigame))
            Assert.True(payout.ObserveRewards(nextSession, 7, _ => { }));
        Assert.True(payout.HasCompleted(nextSession, 42));
    }

    [Fact]
    public async Task Cancelled_payout_cannot_complete_or_leak_its_scope()
    {
        var payout = new CrystalSpherePayout();
        object session = new(), minigame = new();
        payout.Start(session, minigame, 7, () => new object());
        payout.Clicked(session, minigame, 42, remainingBefore: 1);
        var presentation = new TaskCompletionSource();
        async Task OfferAfterDelay()
        {
            await presentation.Task;
            payout.ObserveRewards(session, 7, _ => Assert.Fail("cancelled payout"));
        }
        Task completion;
        using (payout.EnterPayout(session, minigame)) completion = OfferAfterDelay();
        presentation.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(payout.HasCompleted(session, 42));
        Assert.False(payout.ObserveRewards(session, 7, _ => Assert.Fail("scope leaked")));
    }

    [Fact]
    public void Failed_observation_does_not_release_playback()
    {
        var payout = new CrystalSpherePayout();
        object session = new(), minigame = new();
        payout.Start(session, minigame, 7, () => new object());
        payout.Clicked(session, minigame, 42, remainingBefore: 1);
        using (payout.EnterPayout(session, minigame))
            Assert.Throws<InvalidOperationException>(() =>
                payout.ObserveRewards(session, 7, _ => throw new InvalidOperationException()));
        Assert.False(payout.HasCompleted(session, 42));
    }
}
