namespace STS2PGN.Replay;

public enum ReplayFailureKind
{
    InvalidTrace,
    UnsupportedTrace,
    IllegalAction,
    StateDivergence,
    ResultDivergence,
    IncompleteTrace,
}
