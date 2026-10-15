using STS2PGN.Mod.Recording;

namespace STS2PGN.Tests;

public sealed class AcceptedCardPlayTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Accepted_play_is_recorded_before_costs_and_nested_choices(bool delayed, int cost)
    {
        var capture = new AcceptedCardPlay();
        object session = new(), action = new(), card = new();
        var targetReady = new TaskCompletionSource();
        var choiceReady = new TaskCompletionSource();
        var choiceAnswered = new TaskCompletionSource();
        List<string> trace = [];
        int energy = cost;

        async Task ExecuteAcceptedPlay()
        {
            if (delayed) await targetReady.Task;
            Assert.True(capture.Accept(session, card, recordedAction =>
            {
                Assert.Same(action, recordedAction);
                Assert.Equal(cost, energy);
                trace.Add("combat.play_card");
            }));
            energy -= cost;
            Assert.False(capture.Accept(session, card, _ => Assert.Fail("duplicate play")));
            Assert.False(capture.Accept(session, new object(), _ => Assert.Fail("automatic play")));
            trace.Add("choice.offered");
            choiceReady.SetResult();
            await choiceAnswered.Task;
            trace.Add("choice.cards");
        }

        // The patched async method captures the scope, then the finalizer
        // restores the caller as soon as that method returns its Task.
        Task execution;
        using (capture.Enter(session, action, card)) execution = ExecuteAcceptedPlay();
        Assert.False(capture.Accept(session, card, _ => Assert.Fail("unrelated caller")));
        if (delayed) Assert.Empty(trace);
        targetReady.SetResult();
        await choiceReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["combat.play_card", "choice.offered"], trace);
        Assert.False(execution.IsCompleted);
        choiceAnswered.SetResult();
        await execution;
        trace.Add("action.settled");
        Assert.Equal(["combat.play_card", "choice.offered", "choice.cards", "action.settled"], trace);
        Assert.Equal(0, energy);
    }

    [Fact]
    public void Rejected_attempt_emits_nothing_and_does_not_block_a_later_play_of_the_same_card()
    {
        var capture = new AcceptedCardPlay();
        object session = new(), card = new(), rejected = new(), accepted = new();
        List<object> recorded = [];
        using (capture.Enter(session, rejected, card))
        {
            // Rejection returns without reaching resource spending.
            Assert.Empty(recorded);
        }
        Assert.False(capture.Accept(session, card, recorded.Add));
        using (capture.Enter(session, accepted, card))
            Assert.True(capture.Accept(session, card, recorded.Add));
        Assert.Equal([accepted], recorded);
    }

    [Fact]
    public void Unrelated_card_session_and_unrecorded_nested_execution_cannot_claim_a_play()
    {
        var capture = new AcceptedCardPlay();
        object session = new(), action = new(), card = new();
        using (capture.Enter(session, action, card))
        {
            Assert.False(capture.Accept(new object(), card, _ => Assert.Fail("wrong session")));
            Assert.False(capture.Accept(session, new object(), _ => Assert.Fail("wrong card")));
            using (capture.Enter(null, new object(), card))
                Assert.False(capture.Accept(session, card, _ => Assert.Fail("unrecorded action")));
            using (capture.Enter(session, new object(), null))
                Assert.False(capture.Accept(session, card, _ => Assert.Fail("unresolved card")));
            Assert.True(capture.Accept(session, card, recorded => Assert.Same(action, recorded)));
        }
    }

    [Fact]
    public async Task Overlapping_executions_of_the_same_card_keep_their_own_action_identity()
    {
        var capture = new AcceptedCardPlay();
        object session = new(), card = new(), first = new(), second = new();
        var ready = new TaskCompletionSource();
        List<object> recorded = [];
        async Task Delayed()
        {
            await ready.Task;
            Assert.True(capture.Accept(session, card, recorded.Add));
        }
        Task pending;
        using (capture.Enter(session, first, card)) pending = Delayed();
        using (capture.Enter(session, second, card))
            Assert.True(capture.Accept(session, card, recorded.Add));
        ready.SetResult();
        await pending;
        Assert.Equal([second, first], recorded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Teardown_invalidates_pending_execution_even_when_the_session_is_reused(bool newSession)
    {
        var capture = new AcceptedCardPlay();
        object session = new(), action = new(), card = new();
        var ready = new TaskCompletionSource();
        async Task<bool> Delayed()
        {
            await ready.Task;
            return capture.Accept(session, card, _ => Assert.Fail("stale action"));
        }
        Task<bool> pending;
        using (capture.Enter(session, action, card)) pending = Delayed();
        capture.Clear();
        object nextSession = newSession ? new object() : session;
        using (capture.Enter(nextSession, action, card))
        {
            ready.SetResult();
            Assert.False(await pending);
            Assert.True(capture.Accept(nextSession, card, _ => { }));
        }
    }

    [Fact]
    public void Reentrant_or_failed_recording_cannot_emit_a_duplicate()
    {
        var capture = new AcceptedCardPlay();
        object session = new(), action = new(), card = new();
        using (capture.Enter(session, action, card))
        {
            Assert.Throws<InvalidOperationException>(() => capture.Accept(session, card, _ =>
            {
                Assert.False(capture.Accept(session, card, _ => Assert.Fail("reentrant capture")));
                throw new InvalidOperationException("write failed");
            }));
            Assert.False(capture.Accept(session, card, _ => Assert.Fail("duplicate after failure")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_execution_does_not_leak_into_the_caller(bool cancelled)
    {
        var capture = new AcceptedCardPlay();
        object session = new(), action = new(), card = new();
        var ready = new TaskCompletionSource();
        async Task Execute()
        {
            await ready.Task;
            capture.Accept(session, card, _ => Assert.Fail("failed execution"));
        }
        Task pending;
        using (capture.Enter(session, action, card)) pending = Execute();
        if (cancelled) ready.SetCanceled();
        else ready.SetException(new InvalidOperationException("target lookup failed"));
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.False(capture.Accept(session, card, _ => Assert.Fail("leaked context")));
    }
}
