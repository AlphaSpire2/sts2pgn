namespace STS2PGN.Mod.Playback;

/// <summary>A single pending step, with no buffered clicks and no playback timeout.</summary>
internal sealed class PlaybackStepGate : IDisposable
{
    private TaskCompletionSource? _pending;
    private bool _disposed;
    private bool _waitActive;

    public bool IsWaiting => _pending is not null;
    public int Position { get; private set; }
    public int Total { get; private set; }
    public string NextAction { get; private set; } = "Preparing playback";
    public event Action? Changed;

    public async Task WaitAsync(int position, int total, string nextAction, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellation.ThrowIfCancellationRequested();
        if (_waitActive)
        {
            throw new InvalidOperationException("A playback step is already waiting.");
        }

        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Position = position;
        Total = total;
        NextAction = nextAction;
        _waitActive = true;
        _pending = pending;
        using CancellationTokenRegistration registration = cancellation.Register(() => pending.TrySetCanceled(cancellation));
        try
        {
            Changed?.Invoke();
            await pending.Task;
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation.ThrowIfCancellationRequested();
        }
        finally
        {
            _pending = null;
            _waitActive = false;
            Changed?.Invoke();
        }
    }

    public bool Advance()
    {
        if (_disposed || _pending is not { } pending)
        {
            return false;
        }

        // Disable the button synchronously, before the driver resumes on the game thread.
        _pending = null;
        bool advanced = pending.TrySetResult();
        Changed?.Invoke();
        return advanced;
    }

    public void Dispose()
    {
        _disposed = true;
        _pending?.TrySetCanceled();
        _pending = null;
    }
}
