using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Runs;

namespace STS2PGN.Mod.Recording;

/// <summary>
/// Keeps generation-time discovery tied to the new run, never to the next session.
/// Loaded runs have no entry: their current profile cannot reconstruct the past.
/// </summary>
internal static class RunProfileCapture
{
    private static readonly ConditionalWeakTable<RunState, string[]> DiscoveredActs = new();

    public static void Remember(RunState run, string[] discoveredActs) =>
        DiscoveredActs.Add(run, discoveredActs);

    public static string[]? Get(RunState run) =>
        DiscoveredActs.TryGetValue(run, out string[]? discoveredActs) ? discoveredActs : null;
}
