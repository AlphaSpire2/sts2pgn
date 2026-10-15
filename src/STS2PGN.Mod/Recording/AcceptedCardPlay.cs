namespace STS2PGN.Mod.Recording;

/// <summary>
/// Correlates resource spending with one explicit play execution. The scope
/// follows target resolution across awaits, without capturing unrelated or
/// automatic plays. Only reaching the acceptance boundary emits a decision.
/// </summary>
internal sealed class AcceptedCardPlay
{
    private readonly AsyncLocal<Attempt?> _executing = new();
    private object _generation = new();

    public IDisposable Enter(object? session, object action, object? card)
    {
        Attempt? previous = _executing.Value;
        _executing.Value = session is null || card is null
            ? null
            : new Attempt(_generation, session, action, card);
        return new Scope(() => _executing.Value = previous);
    }

    public bool Accept(object? session, object card, Action<object> record)
    {
        Attempt? attempt = _executing.Value;
        if (attempt is null || attempt.Accepted
            || !ReferenceEquals(attempt.Generation, _generation)
            || !ReferenceEquals(attempt.Session, session)
            || !ReferenceEquals(attempt.Card, card))
        {
            return false;
        }

        // Recorder callbacks and cost hooks may re-enter. Consume the attempt
        // before emitting it so neither can record the same decision twice.
        attempt.Accepted = true;
        record(attempt.Action);
        return true;
    }

    // Continuations from a closed recording cannot attach to a new session.
    public void Clear() => _generation = new object();

    private sealed class Attempt(object generation, object session, object action, object card)
    {
        public object Generation { get; } = generation;
        public object Session { get; } = session;
        public object Action { get; } = action;
        public object Card { get; } = card;
        public bool Accepted { get; set; }
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            restore();
        }
    }
}
