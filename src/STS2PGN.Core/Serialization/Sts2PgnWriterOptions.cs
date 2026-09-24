using STS2PGN.Validation;

namespace STS2PGN.Serialization;

public sealed record Sts2PgnWriterOptions
{
    public bool ValidateBeforeWriting { get; init; } = true;
    public bool IncludeTerminalResult { get; init; } = true;
    /// <summary>Use smaller state deltas for version-two files. Version-one output is always full.</summary>
    public bool EncodeStateDeltas { get; init; } = true;

    /// <summary>
    /// Profile used by the pre-write validation. When null, the profile is inferred from the
    /// metadata: use <c>Profile</c>, falling back to <c>Producer</c> for legacy scripts.
    /// </summary>
    public Sts2PgnValidationMode? ValidationMode { get; init; }
}
