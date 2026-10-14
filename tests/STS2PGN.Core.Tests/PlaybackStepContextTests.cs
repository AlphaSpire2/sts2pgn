using System.Text.Json;
using STS2PGN.Mod.Playback;
using STS2PGN.Scripting;

namespace STS2PGN.Tests;

public sealed class PlaybackStepContextTests
{
    [Theory]
    [InlineData(1, 1, 6)]
    [InlineData(6, 1, 11)]
    [InlineData(10, 5, 15)]
    [InlineData(20, 15, 20)]
    public void WindowIncludesCurrentAndUpToFiveOnEachSide(int position, int first, int last)
    {
        string[] actions = Enumerable.Range(1, 20).Select(value => $"Action {value}").ToArray();

        IReadOnlyList<PlaybackActionRow> rows = PlaybackStepContext.Window(actions, position);

        Assert.Equal(Enumerable.Range(first, last - first + 1), rows.Select(row => row.Position));
        Assert.Equal($"Action {position}", Assert.Single(rows, row => row.Position == position).Description);
    }

    [Fact]
    public void ShortScriptAndPreparingStateDoNotInventActions()
    {
        Assert.Empty(PlaybackStepContext.Window([], 1));
        Assert.Empty(PlaybackStepContext.Window(["End turn"], 0));
        Assert.Empty(PlaybackStepContext.Window(["End turn"], 2));
        Assert.Equal(new PlaybackActionRow(1, "End turn"),
            Assert.Single(PlaybackStepContext.Window(["End turn"], 1)));
    }

    [Fact]
    public void PositionJumpsKeepScriptContextIncludingGroupedChoices()
    {
        var record = TestRecordFactory.CreateScriptBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("event.choose", """{"actor":"p0","data":{"event_id":"EVENT.TEST","option_index":0,"option_id":"test"}}""")
            .AppendJson("choice.cards", """{"actor":"p0","data":{"choice_id":1,"cards":[{"deck_index":0,"fingerprint":{"model_id":"CARD.STRIKE"}}]}}""")
            .AppendJson("choice.cards", """{"actor":"p0","data":{"choice_id":1,"cards":[{"deck_index":0,"fingerprint":{"model_id":"CARD.STRIKE"}},{"deck_index":1,"fingerprint":{"model_id":"CARD.DEFEND"}}]}}""")
            .AppendJson("map.choose", TestRecordFactory.ScriptMapChoose)
            .Build();
        DecisionScript script = DecisionScript.FromRecord(record);
        Assert.True(script.Decisions[1].ContinuedByNext);
        string[] actions = script.Decisions.Select(decision => PlaybackStepContext.Describe(decision.Kind, decision.Data)).ToArray();

        IReadOnlyList<PlaybackActionRow> rows = PlaybackStepContext.Window(actions, 3);

        Assert.Equal([1, 2, 3, 4], rows.Select(row => row.Position));
        Assert.Equal("Answer selection: CARD.STRIKE, CARD.DEFEND", rows[2].Description);
    }

    [Theory]
    [InlineData("combat.play_card", """{"card_model":"CARD.STRIKE","target":{"model_id":"MONSTER.JAW_WORM"}}""", "Play card: CARD.STRIKE → MONSTER.JAW_WORM")]
    [InlineData("combat.use_potion", """{"potion_model":"POTION.FIRE"}""", "Use potion: POTION.FIRE")]
    [InlineData("map.choose", """{"col":2,"row":3}""", "Travel to next room: (2, 3)")]
    [InlineData("rest.choose", """{"option_id":"smith"}""", "Choose rest action: smith")]
    [InlineData("choice.index", """{"indexes":[0,2]}""", "Answer selection: 0, 2")]
    [InlineData("event.choose", """{"option_id":"first\nsecond\tthird"}""", "Choose event option: first second third")]
    [InlineData("combat.play_card", "{}", "Play card")]
    [InlineData("combat.use_potion", "null", "Use potion")]
    [InlineData("future.action", "{}", "future.action")]
    public void DescriptionsShowAvailableDetailsAndStayOnOneLine(string kind, string json, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(expected, PlaybackStepContext.Describe(kind, document.RootElement));
    }
}
