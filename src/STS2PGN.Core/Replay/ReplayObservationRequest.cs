namespace STS2PGN.Replay;

public sealed record ReplayObservationRequest(
    long Sequence,
    long After,
    string Context,
    int ProjectionVersion,
    string? Scope);
