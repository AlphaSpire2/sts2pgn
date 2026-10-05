namespace STS2PGN.Mod.Recording;

/// <summary>The gameplay boundary excludes all post-boss presentation.</summary>
internal static class GameplayVictory
{
    public static bool IsFinalBoss(bool bossRoom, int act, int actCount,
        bool atBoss, bool hasSecondBoss, bool atSecondBoss) =>
        bossRoom && actCount > 0 && act == actCount - 1
        && (hasSecondBoss ? atSecondBoss : atBoss);
}
