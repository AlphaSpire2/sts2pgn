using STS2PGN.Mod.Playback;

namespace STS2PGN.Tests;

public sealed class PlaybackStepGateTests
{
    [Fact]
    public async Task FirstDecisionWaitsAndBusyPressesDoNotQueueTheNextDecision()
    {
        using PlaybackStepGate gate = new();
        Assert.False(gate.Advance());
        Task first = gate.WaitAsync(1, 3, "Play card", CancellationToken.None);
        Assert.False(first.IsCompleted);
        Assert.True(gate.IsWaiting);
        Assert.Equal(1, gate.Position);
        Assert.Equal(3, gate.Total);
        Assert.Equal("Play card", gate.NextAction);
        Assert.True(gate.Advance());
        Assert.False(gate.IsWaiting);
        Assert.False(gate.Advance());
        await first.WaitAsync(TimeSpan.FromSeconds(2));

        Task second = gate.WaitAsync(2, 3, "Answer selection", CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.True(gate.Advance());
        await second.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NestedChoiceRequiresAnotherPressWithoutWaitingForItsParentToFinish()
    {
        using PlaybackStepGate gate = new();
        List<string> events = [];
        TaskCompletionSource choiceOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource choiceAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ParentAction()
        {
            events.Add("card submitted");
            choiceOpened.SetResult();
            await choiceAnswered.Task;
            events.Add("card settled");
        }
        async Task Drive()
        {
            await gate.WaitAsync(1, 2, "Play card", CancellationToken.None);
            Task parent = ParentAction();
            await choiceOpened.Task;
            await gate.WaitAsync(2, 2, "Answer selection", CancellationToken.None);
            events.Add("choice submitted");
            choiceAnswered.SetResult();
            await parent;
        }

        TaskCompletionSource waitingForChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Changed += () =>
        {
            if (gate.IsWaiting && gate.Position == 2) waitingForChoice.TrySetResult();
        };
        Task drive = Drive();
        Assert.Empty(events);
        Assert.True(gate.Advance());
        await waitingForChoice.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "card submitted" }, events);
        Assert.False(drive.IsCompleted);
        Assert.True(gate.Advance());
        await drive.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "card submitted", "choice submitted", "card settled" }, events);
    }

    [Fact]
    public async Task CancellationUnblocksAnIndefinitePauseWithoutSubmitting()
    {
        using PlaybackStepGate gate = new();
        using CancellationTokenSource cancellation = new();
        bool submitted = false;
        async Task Drive()
        {
            await gate.WaitAsync(1, 1, "End turn", cancellation.Token);
            submitted = true;
        }

        Task drive = Drive();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drive.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(submitted);
        Assert.False(gate.IsWaiting);
        Assert.False(gate.Advance());
    }

    [Fact]
    public async Task DisposalReleasesWaitAndCannotLeaveAReusableStep()
    {
        PlaybackStepGate gate = new();
        Task wait = gate.WaitAsync(1, 1, "Play card", CancellationToken.None);
        gate.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gate.Advance());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            gate.WaitAsync(1, 1, "Play card", CancellationToken.None));
    }

    [Fact]
    public async Task CancelledSessionCannotBecomeReadyForInput()
    {
        using PlaybackStepGate gate = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.WaitAsync(1, 1, "Play card", cancellation.Token));
        Assert.False(gate.IsWaiting);
    }

    [Fact]
    public async Task ConcurrentWaitIsRejectedWithoutReplacingThePendingDecision()
    {
        using PlaybackStepGate gate = new();
        Task first = gate.WaitAsync(1, 2, "Play card", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.WaitAsync(2, 2, "End turn", CancellationToken.None));
        Assert.Equal(1, gate.Position);
        Assert.True(gate.Advance());
        await first.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
