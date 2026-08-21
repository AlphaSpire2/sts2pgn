namespace STS2PGN.Replay;

/// <summary>
/// Describes how a trace record participates in replay without discarding its original kind or payload.
/// </summary>
public enum ReplayRecordRole
{
    Initialization,

    /// <summary>
    /// A run.resume re-anchor: the real game rewound to its last save checkpoint, so the
    /// simulator must discard its state and re-initialize from this record's payload.
    /// </summary>
    Reinitialization,
    Action,
    Resolution,
    Lifecycle,
    Observation,
    Terminal,
}
