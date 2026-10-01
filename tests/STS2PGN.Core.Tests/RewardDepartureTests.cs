using System.Text.Json;
using STS2PGN.Mod.Recording;
using STS2PGN.Parsing;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class RewardDepartureTests
{
    [Theory]
    [InlineData(0)] // Travel without a recorded map-open click, e.g. via the top bar.
    [InlineData(1)]
    [InlineData(5)]
    public void RewardAndPotionDecisionsStayBeforeOneCommittedDeparture(int mapOpenings)
    {
        object room = new();
        RewardDeparture departure = new();
        departure.Offered(room);
        using StringWriter output = new();
        using Sts2PgnStreamWriter writer = new(output,
            TestRecordFactory.CreateBuilder().SetTag("FormatVersion", "2").Build().Metadata,
            leaveOpen: true);
        long Record(string kind, object data) => writer.Append(kind,
            JsonSerializer.SerializeToElement(new { actor = "p0", data })).Sequence;
        void Observe(long after, object state) => writer.Append("state.observe",
            JsonSerializer.SerializeToElement(StateObservationFactory.Create(after, "action.settled", state)));

        long start = Record("run.start", new { });
        Observe(start, State(75, 27, "speed"));
        Record("reward.claim", new { set_id = 8, reward_index = 2 });
        long skip = Record("choice.index", new { choice_id = 14, indexes = new[] { 3 } });
        Observe(skip, State(75, 27, "speed"));
        long nextSequence = writer.NextSequence;
        for (int i = 0; i < mapOpenings; i++)
        {
            Assert.True(departure.CanDefer(room, combatActive: false, resumesParent: false));
        }
        Assert.Equal(nextSequence, writer.NextSequence);

        long discard = Record("combat.discard_potion", new { potion_slot = 0 });
        Observe(discard, State(75, 27, null));
        Record("reward.claim", new { set_id = 8, reward_index = 2 });
        long take = Record("choice.index", new { choice_id = 15, indexes = new[] { 1 } });
        var afterReward = State(76, 28, null);
        Observe(take, afterReward);
        Assert.True(departure.CommitMapTravel(room, combatActive: false, resumesParent: false));
        long proceed = Record("room.proceed", new { });
        long map = Record("map.choose", new { col = 1, row = 10 });
        Assert.Equal(proceed + 1, map);
        Assert.False(departure.CommitMapTravel(room, combatActive: false, resumesParent: false));

        var decoded = Sts2PgnParser.Parse(output.ToString());
        var validation = Sts2PgnValidator.Validate(decoded);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.ToString())));
        Assert.Single(decoded.Entries, entry => entry.Kind == "room.proceed");
        Assert.Equal(new[] { "run.start", "reward.claim", "choice.index", "combat.discard_potion",
            "reward.claim", "choice.index", "room.proceed", "map.choose" },
            decoded.Entries.Where(entry => entry.Kind != "state.observe").Select(entry => entry.Kind));
        var observations = decoded.Entries.Where(entry => entry.Kind == "state.observe").ToArray();
        Assert.Equal(new[] { start, skip, discard, take },
            observations.Select(entry => entry.Payload.GetProperty("after").GetInt64()));
        Assert.Equal(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(afterReward)),
            CanonicalJson.Serialize(observations[^1].Payload.GetProperty("state")));
    }

    [Theory]
    [InlineData(false, false, false)] // Rewardless fight or a non-combat room.
    [InlineData(true, true, false)]   // Combat still running/starting.
    [InlineData(true, false, true)]   // Parent event must resume immediately.
    public void StatefulProceedsAreNotDeferred(bool offeredRewards, bool combatActive, bool resumesParent)
    {
        object room = new();
        RewardDeparture departure = new();
        if (offeredRewards)
        {
            departure.Offered(room);
        }
        Assert.False(departure.CanDefer(room, combatActive, resumesParent));
        Assert.False(departure.CommitMapTravel(room, combatActive, resumesParent));
    }

    [Fact]
    public void NonResumingPushedCombatUsesTheSameRewardBoundary()
    {
        object childRoom = new();
        RewardDeparture departure = new();
        departure.Offered(childRoom);
        Assert.True(departure.CanDefer(childRoom, combatActive: false, resumesParent: false));
        Assert.True(departure.CommitMapTravel(childRoom, combatActive: false, resumesParent: false));
    }

    [Fact]
    public void RoomChangesAndSessionOrActBoundariesDiscardPendingDepartures()
    {
        object room = new();
        object nextRoom = new();
        RewardDeparture departure = new();
        departure.Offered(room);
        Assert.False(departure.CanDefer(nextRoom, combatActive: false, resumesParent: false));
        departure.Entered(nextRoom);
        Assert.False(departure.CanDefer(room, combatActive: false, resumesParent: false));
        departure.Offered(room);
        departure.Clear(); // Used on stop, resume, room exit, act.next and immediate Proceed.
        Assert.False(departure.CommitMapTravel(room, combatActive: false, resumesParent: false));
    }

    [Fact]
    public void RestoredCombatRewardsOfferedDuringEntrySurviveTheRoomEnteredNotification()
    {
        object restoredRoom = new();
        RewardDeparture departure = new();
        departure.Offered(restoredRoom);
        departure.Entered(restoredRoom);
        Assert.True(departure.CommitMapTravel(restoredRoom, combatActive: false, resumesParent: false));
    }

    private static JsonElement State(int hp, int cards, string? potion) => JsonSerializer.SerializeToElement(new
    {
        scope = "run.core",
        run = new { seed = "TEST", act = 2, floor = 10 },
        acts = new[] { new { index = 2, model_id = "ACT.GLORY" } },
        players = new[] { new { slot = "p0", character_model = "CHARACTER.REGENT", current_hp = hp, cards, potion } },
        rooms = Array.Empty<object>(),
        choices = new { player_choices = Array.Empty<object>(), rewards = Array.Empty<object>() },
        special_event = (object?)null,
        combat = (object?)null,
    });
}
