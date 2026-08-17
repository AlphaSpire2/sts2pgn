namespace STS2PGN.Replay;

/// <summary>
/// Describes how a trace record participates in replay without discarding its original kind or payload.
/// </summary>
public enum ReplayRecordRole
{
    Initialization,
    Action,
    Resolution,
    Lifecycle,
    Observation,
    Terminal,
}
