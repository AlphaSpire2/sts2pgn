using System.Text.Json;
using STS2PGN.Scripting;

namespace STS2PGN.Tests;

public class PostVictoryDecisionsTests
{
    [Theory]
    [InlineData("act.next", null, true)]
    [InlineData("room.proceed", null, true)]
    [InlineData("event.choose", "EVENT.THE_ARCHITECT", true)]
    [InlineData("event.advance", "EVENT.THE_ARCHITECT", true)]
    [InlineData("event.choose", "EVENT.NEOW", false)]
    [InlineData("event.choose", null, false)]
    [InlineData("combat.play_card", "EVENT.THE_ARCHITECT", false)]
    [InlineData("map.choose", null, false)]
    public void LegacyTailDoesNotAdmitMoreGameplay(string kind, string? eventId, bool expected)
    {
        // Presentation option identity/length is deliberately irrelevant.
        var data = JsonSerializer.SerializeToElement(new { event_id = eventId, option_id = "arbitrary presentation" });
        Assert.Equal(expected, PostVictoryDecisions.IsPresentation(kind, data));
    }
}
