namespace STS2PGN.Validation;

/// <summary>
/// Selects which Format version 1 profile a trace is validated against.
/// </summary>
public enum Sts2PgnValidationMode
{
    /// <summary>An authoritative recording produced by the in-game recorder (carries RecorderVersion).</summary>
    Recording,

    /// <summary>A decision script proposed by a simulator (carries Producer, never RecorderVersion).</summary>
    Script,
}
