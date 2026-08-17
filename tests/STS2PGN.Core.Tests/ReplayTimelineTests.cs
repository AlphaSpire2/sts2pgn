using STS2PGN.Model;
using STS2PGN.Replay;

namespace STS2PGN.Tests;

public sealed class ReplayTimelineTests
{
    [Fact]
    public void TimelinePreservesAndClassifiesEveryRecord()
    {
        RunRecord record = TestRecordFactory.CreateBuilder("DEFEAT")
            .AppendJson("run.start", "{}")
            .AppendJson("room.enter", "{}")
            .AppendJson("map.choose", "{\"actor\":\"p0\",\"data\":{}}")
            .AppendJson("reward.resolve", "{\"actor\":\"p0\",\"data\":{}}")
            .AppendJson("future.action", "{\"extension\":true}")
            .AppendJson("state.observe", "{}")
            .AppendJson("run.finish", "{}")
            .Build(RunResult.Defeat);

        ReplayTimeline timeline = new(record);

        Assert.Equal(
            [
                ReplayRecordRole.Initialization,
                ReplayRecordRole.Lifecycle,
                ReplayRecordRole.Action,
                ReplayRecordRole.Resolution,
                ReplayRecordRole.Action,
                ReplayRecordRole.Observation,
                ReplayRecordRole.Terminal,
            ],
            timeline.Select(step => step.Role));
        Assert.Equal(record.Entries.Select(entry => entry.Kind), timeline.Select(step => step.Kind));
        Assert.Equal(
            "{\"extension\":true}",
            timeline.Single(step => step.Kind == "future.action").Payload.GetRawText());
    }
}
