namespace STS2PGN.Replay;

/// <summary>
/// Path-specific rules for arrays whose elements have stable semantic keys.
/// Other arrays are compared in wire order.
/// </summary>
public sealed class StateComparisonRules
{
    private readonly IReadOnlyDictionary<string, string> _arrayKeys;

    public static StateComparisonRules Default { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/players"] = "slot",
        ["/combat/creatures"] = "combat_id",
    });

    public StateComparisonRules(IReadOnlyDictionary<string, string>? arrayKeys = null)
    {
        _arrayKeys = new Dictionary<string, string>(
            arrayKeys ?? new Dictionary<string, string>(),
            StringComparer.Ordinal);
    }

    internal bool TryGetArrayKey(string path, out string keyProperty) =>
        _arrayKeys.TryGetValue(path, out keyProperty!);
}
