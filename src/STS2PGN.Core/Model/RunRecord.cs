using System.Collections.ObjectModel;

namespace STS2PGN.Model;

/// <summary>
/// An immutable parsed or constructed STS2PGN trace.
/// </summary>
public sealed class RunRecord
{
    private readonly ReadOnlyCollection<RunRecordEntry> _entries;

    public RunMetadata Metadata { get; }
    public IReadOnlyList<RunRecordEntry> Entries => _entries;
    public RunResult Result { get; }
    public bool RecoveredTruncatedFinalLine { get; }

    public RunRecord(
        RunMetadata metadata,
        IEnumerable<RunRecordEntry> entries,
        RunResult result = RunResult.Incomplete,
        bool recoveredTruncatedFinalLine = false)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(entries);

        Metadata = metadata;
        _entries = Array.AsReadOnly(entries.ToArray());
        Result = result;
        RecoveredTruncatedFinalLine = recoveredTruncatedFinalLine;
    }
}
