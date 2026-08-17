namespace STS2PGN.Replay;

public sealed record ReplayFailure(
    ReplayFailureKind Kind,
    long? Sequence,
    string Message,
    ReplayDivergenceReport? Divergence = null);
