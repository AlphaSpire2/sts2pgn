using STS2PGN.Model;

namespace STS2PGN.Replay;

/// <summary>
/// Ordered, game-independent view of every trace record.
/// </summary>
public sealed class ReplayTimeline : IReadOnlyList<ReplayStep>
{
    private static readonly IReadOnlySet<string> LifecycleKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "act.enter",
        "room.enter",
        "room.exit",
        "run.resume",
    };

    private readonly ReplayStep[] _steps;

    public int Count => _steps.Length;
    public ReplayStep this[int index] => _steps[index];

    public ReplayTimeline(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _steps = record.Entries.Select(entry => new ReplayStep(entry)).ToArray();
    }

    public IEnumerator<ReplayStep> GetEnumerator() => ((IEnumerable<ReplayStep>)_steps).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _steps.GetEnumerator();

    internal static ReplayRecordRole Classify(string kind)
    {
        if (kind == "run.start")
        {
            return ReplayRecordRole.Initialization;
        }

        if (kind == "state.observe")
        {
            return ReplayRecordRole.Observation;
        }

        if (kind == "run.finish")
        {
            return ReplayRecordRole.Terminal;
        }

        if (kind.EndsWith(".resolve", StringComparison.Ordinal))
        {
            return ReplayRecordRole.Resolution;
        }

        return LifecycleKinds.Contains(kind) ? ReplayRecordRole.Lifecycle : ReplayRecordRole.Action;
    }
}
