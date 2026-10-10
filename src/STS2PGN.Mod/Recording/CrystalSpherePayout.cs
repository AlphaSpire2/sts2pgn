namespace STS2PGN.Mod.Recording;

/// <summary>
/// Correlates a final divination with its generated reward offer. The async scope
/// follows that minigame's payout across awaits without attributing unrelated
/// main-thread observations or another player's rewards to the click.
/// </summary>
internal sealed class CrystalSpherePayout
{
    private readonly AsyncLocal<Board?> _offering = new();
    private Board? _board;

    public void Start(object session, object minigame, ulong playerId, Func<object> project) =>
        _board = new Board(session, minigame, playerId, project);

    public bool IsCurrent(object? session, object minigame) =>
        _board is { } board && ReferenceEquals(board.Session, session)
            && ReferenceEquals(board.Minigame, minigame);

    public void Clicked(object? session, object minigame, long sequence, int remainingBefore)
    {
        if (IsCurrent(session, minigame) && remainingBefore == 1)
        {
            _board!.FinalClick = sequence;
            _board.CompletedClick = null;
        }
    }

    public IDisposable EnterPayout(object? session, object minigame)
    {
        Board? previous = _offering.Value;
        _offering.Value = IsCurrent(session, minigame) ? _board : null;
        return new Scope(() => _offering.Value = previous);
    }

    public bool ObserveRewards(object? session, ulong playerId, Action<object> observe)
    {
        Board? board = _offering.Value;
        if (board is null || !ReferenceEquals(board, _board)
            || !ReferenceEquals(board.Session, session) || board.PlayerId != playerId
            || board.FinalClick is not { } sequence || board.CompletedClick.HasValue)
        {
            return false;
        }

        // Reward generation may consume the minigame RNG. Refresh the projection
        // at the offer, and only release playback after the observation succeeds.
        observe(board.Project());
        board.CompletedClick = sequence;
        return true;
    }

    public bool HasCompleted(object? session, long sequence) =>
        _board is { } board && ReferenceEquals(board.Session, session)
            && board.CompletedClick == sequence;

    // Old async scopes may still unwind, but cannot match a new board or session.
    public void Clear() => _board = null;

    private sealed class Board(object session, object minigame, ulong playerId, Func<object> project)
    {
        public object Session { get; } = session;
        public object Minigame { get; } = minigame;
        public ulong PlayerId { get; } = playerId;
        public Func<object> Project { get; } = project;
        public long? FinalClick { get; set; }
        public long? CompletedClick { get; set; }
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
