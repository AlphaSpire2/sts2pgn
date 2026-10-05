using STS2PGN.Mod.Recording;

namespace STS2PGN.Tests;

public class GameplayVictoryTests
{
    [Theory]
    [InlineData(true, 2, 3, true, false, false, true)]
    [InlineData(true, 2, 3, true, true, false, false)]
    [InlineData(true, 2, 3, false, true, true, true)]
    [InlineData(true, 1, 3, true, false, false, false)]
    [InlineData(false, 2, 3, true, false, false, false)]
    [InlineData(true, 3, 4, true, false, false, true)]
    [InlineData(true, 2, 3, false, false, false, false)]
    public void OnlyTheLastRequiredBossEndsGameplay(bool boss, int act, int count,
        bool atBoss, bool second, bool atSecond, bool expected) =>
        Assert.Equal(expected, GameplayVictory.IsFinalBoss(boss, act, count, atBoss, second, atSecond));
}
