using System.Text.Json;
using System.Text.Json.Nodes;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Replay;
using STS2PGN.Serialization;
using STS2PGN.Validation;

namespace STS2PGN.Tests;

public sealed class StateDeltaTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sts2pgn-delta-").FullName;

    [Fact]
    public void IndependentlySpecifiedExampleDecodesWithoutUsingTheEncoder()
    {
        string wire = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "delta-minimal.sts2pgn"));
        RunRecord decoded = Sts2PgnParser.Parse(wire);
        Assert.True(Sts2PgnValidator.Validate(decoded).IsValid);
        Assert.Equal(new[] { "run.start", "state.observe", "map.choose", "state.observe" }, decoded.Entries.Select(e => e.Kind));
        JsonObject expected = JsonNode.Parse(decoded.Entries[1].Payload.GetProperty("state").GetRawText())!.AsObject();
        expected["scope"] = "run.core";
        expected["run"]!["floor"] = 1;
        Assert.Equal(CanonicalJson.Serialize(Element(expected)), CanonicalJson.Serialize(decoded.Entries[3].Payload.GetProperty("state")));
        Assert.Equal(3, decoded.Entries[3].Payload.GetProperty("after").GetInt32());
    }

    [Fact]
    public void RawWireDeltasCannotBypassModelValidation()
    {
        RunRecord raw = Builder().AppendJson("state.delta", "{\"base\":1,\"observation\":{},\"patch\":[]}").Build();
        Assert.Contains(Sts2PgnValidator.Validate(raw).Errors, issue => issue.Code == "undecoded_state_delta");
    }

    [Fact]
    public void FailedAppendCannotLeaveAReusableWriterWithAnUnwrittenBaseline()
    {
        RunRecord record = Trace(State(80), State(79));
        using FailingWriter output = new();
        using Sts2PgnStreamWriter writer = new(output, record.Metadata, leaveOpen: true);
        foreach (RunRecordEntry entry in record.Entries.Take(4))
        {
            writer.Append(entry.Kind, entry.Payload);
        }
        output.Fail = true;
        Assert.Throws<IOException>(() => writer.Append("state.observe", record.Entries[4].Payload));
        output.Fail = false;
        Assert.Throws<InvalidOperationException>(() => writer.Append("state.observe", record.Entries[4].Payload));
        Assert.Throws<InvalidOperationException>(() => writer.Complete(RunResult.Defeat));
    }

    [Fact]
    public void DeltasRetainAllFieldsExactNumbersArrayOrderAndObservationMetadata()
    {
        JsonObject first = State(80);
        first["unknown"] = JsonNode.Parse("""
            {"":"empty key","a/~b":{"remove":4,"nil":3},"number":18446744073709551615,
             "decimal":0.1234567890123456789012345678,"cards":[{"id":2},{"id":1}]}
            """);
        JsonObject second = (JsonObject)first.DeepClone();
        second["unknown"]!["a/~b"]!.AsObject().Remove("remove");
        second["unknown"]!["a/~b"]!["nil"] = null;
        second["unknown"]!["a/~b"]!["added"] = true;
        second["unknown"]!["cards"] = JsonNode.Parse("[{\"id\":1,\"upgrade\":2},{\"id\":2}]");
        second["unknown"]!["number"] = JsonNode.Parse("18446744073709551614");
        second["unknown"]!["decimal"] = JsonNode.Parse("0.1234567890123456789012345679");
        RunRecord original = Trace(first, second, second);

        string wire = Sts2PgnWriter.Write(original);
        Assert.Equal(2, Lines(wire, "state.delta").Length);
        Assert.True(wire.Length < Sts2PgnWriter.Write(original, new() { EncodeStateDeltas = false }).Length);
        RunRecord decoded = Sts2PgnParser.Parse(wire);
        AssertSameRecords(original, decoded);
        Assert.True(Sts2PgnValidator.Validate(decoded).IsValid);
        AssertSameRecords(original, Sts2PgnParser.Parse(Sts2PgnWriter.Write(decoded)));
        JsonObject repeat = Payload(Lines(wire, "state.delta")[1]);
        Assert.Empty(repeat["patch"]!.AsArray());
    }

    [Fact]
    public void VersionOneRemainsFullAndBothVersionsValidate()
    {
        RunRecord v2 = Trace(State(80), State(79));
        RunRecord v1 = new(new RunMetadata(v2.Metadata.Tags.Select(tag =>
            tag.Name == "FormatVersion" ? new TagPair(tag.Name, "1") : tag)), v2.Entries, v2.Result);
        string wire = Sts2PgnWriter.Write(v1);
        Assert.Empty(Lines(wire, "state.delta"));
        AssertSameRecords(v1, Sts2PgnParser.Parse(wire));
        Assert.True(Sts2PgnValidator.Validate(v1).IsValid);
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(
            Sts2PgnWriter.Write(v2).Replace("[FormatVersion \"2\"]", "[FormatVersion \"1\"]")));
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(
            wire.Replace("[FormatVersion \"1\"]", "[FormatVersion \"99\"]")));
    }

    [Fact]
    public void StreamWriterFlushesDeltasButReturnsFullObservations()
    {
        RunRecord record = Trace(State(80), State(79));
        using StringWriter output = new();
        using (Sts2PgnStreamWriter writer = new(output, record.Metadata, leaveOpen: true))
        {
            foreach (RunRecordEntry entry in record.Entries)
            {
                RunRecordEntry appended = writer.Append(entry.Kind, entry.Payload);
                Assert.Equal(entry.Kind, appended.Kind);
                // Every append is already a parseable prefix, including each delta.
                Assert.Equal(entry.Sequence, Sts2PgnParser.Parse(output.ToString()).Entries.Count);
            }
            writer.Complete(record.Result);
        }
        Assert.Single(Lines(output.ToString(), "state.delta"));
        AssertSameRecords(record, Sts2PgnParser.Parse(output.ToString()));
    }

    [Fact]
    public void PeriodicFullCheckpointsBoundDeltaChains()
    {
        string wire = Sts2PgnWriter.Write(Trace(Enumerable.Range(0, 132).Select(i => State(i)).ToArray()));
        string[] observations = wire.Split('\n').Where(line => line.Contains(" state.", StringComparison.Ordinal)).ToArray();
        Assert.Contains(" state.observe ", observations[0]);
        Assert.All(observations[1..65], line => Assert.Contains(" state.delta ", line));
        Assert.Contains(" state.observe ", observations[65]);
        Assert.Contains(" state.observe ", observations[130]);
        Assert.Equal(132, Sts2PgnParser.Parse(wire).Entries.Count(entry => entry.Kind == "state.observe"));
    }

    [Theory]
    [InlineData("run.resume")]
    [InlineData("room.enter")]
    [InlineData("act.enter")]
    public void BoundariesForceFullObservationsEvenWhenStateIsIdentical(string kind)
    {
        RunRecordBuilder builder = Builder();
        JsonObject state = State(80);
        ObserveAction(builder, state);
        builder.Append(kind, Element(new JsonObject { ["data"] = new JsonObject { ["state"] = state.DeepClone() } }));
        Observe(builder, state, kind);
        ObserveAction(builder, state);
        string wire = Sts2PgnWriter.Write(builder.Build(), new() { ValidateBeforeWriting = false });
        Assert.Equal(2, Lines(wire, "state.observe").Length);
        Assert.Single(Lines(wire, "state.delta"));
        AssertSameRecords(builder.Build(), Sts2PgnParser.Parse(wire));
    }

    [Fact]
    public void LargerPatchFallsBackToFullObservation()
    {
        JsonObject first = State(80);
        JsonObject second = State(79);
        first["extra"] = new JsonObject();
        second["extra"] = new JsonObject();
        for (int i = 0; i < 200; i++)
        {
            first["extra"]![i.ToString()] = i;
            second["extra"]![i.ToString()] = i + 1;
        }
        Assert.Empty(Lines(Sts2PgnWriter.Write(Trace(first, second)), "state.delta"));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("digest")]
    [InlineData("path")]
    [InlineData("remove")]
    [InlineData("operation")]
    [InlineData("state")]
    [InlineData("extension")]
    public void InvalidDeltasFailEvenWhenTheyAreTheUnterminatedLastRecord(string damage)
    {
        string wire = Sts2PgnWriter.Write(Trace(State(80), State(79)));
        string line = Assert.Single(Lines(wire, "state.delta"));
        JsonObject payload = Payload(line);
        switch (damage)
        {
            case "base": payload["base"] = 1; break;
            case "digest": payload["observation"]!["digest"] = "sha256:" + new string('0', 64); break;
            case "path": payload["patch"] = JsonNode.Parse("[[\"set\",[\"absent\",\"hp\"],79]]"); break;
            case "remove": payload["patch"] = JsonNode.Parse("[[\"remove\",[\"absent\"]]]"); break;
            case "operation": payload["patch"] = JsonNode.Parse("[[\"increment\",[\"hp\"],1]]"); break;
            case "state": payload["observation"]!["state"] = new JsonObject(); break;
            case "extension": payload["unknown_transport_field"] = true; break;
        }
        string bad = wire[..wire.IndexOf(line, StringComparison.Ordinal)] + "5 state.delta " + payload.ToJsonString();
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(bad));
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(bad + "\n"));
    }

    [Fact]
    public void MissingBaseAndCorruptFullCheckpointAreRejected()
    {
        string wire = Sts2PgnWriter.Write(Trace(State(80), State(79)));
        string full = Assert.Single(Lines(wire, "state.observe"));
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(wire.Replace(full + "\n", "")));
        JsonObject damaged = Payload(full);
        damaged["state"]!["players"]![0]!["current_hp"] = 1;
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(wire.Replace(full, "3 state.observe " + damaged.ToJsonString())));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CrashRecoveryThenMultipleResumesResetTheBaseline(int version)
    {
        JsonObject abandoned = State(70);
        abandoned["combat"] = new JsonObject { ["round"] = 5 };
        abandoned["choices"]!["rewards"] = new JsonArray(new JsonObject { ["id"] = 10 });
        abandoned["run"]!["abandoned_only"] = true;
        RunRecord original = Trace(State(80), abandoned);
        string wire = Sts2PgnWriter.Write(original, new() { IncludeTerminalResult = false });
        if (version == 1)
        {
            wire = Sts2PgnWriter.Write(original, new() { IncludeTerminalResult = false, EncodeStateDeltas = false })
                .Replace("[FormatVersion \"2\"]", "[FormatVersion \"1\"]");
        }
        string path = Path.Combine(_directory, "resume.sts2pgn");
        File.WriteAllText(path, wire + "6 state.delta {\"base\":5,\"patch\":[");
        for (int reload = 1; reload <= 2; reload++)
        {
            using Sts2PgnTraceContinuation continuation = Sts2PgnTraceContinuation.Open(path);
            Assert.Equal(reload == 1, continuation.RemovedTruncatedFinalLine);
            Sts2PgnStreamWriter writer = continuation.Writer;
            JsonObject restored = State(80);
            restored["players"]![0]!["deck"] = new JsonArray();
            restored["combat"] = null;
            long sequence = writer.Append("run.resume", Element(new JsonObject
            {
                ["data"] = new JsonObject { ["reloads"] = reload, ["state"] = restored.DeepClone() },
            })).Sequence;
            writer.Append("state.observe", Observation(sequence, restored, "run.resume"));
            sequence = writer.AppendJson("map.choose", "{\"actor\":\"p0\",\"data\":{\"col\":1,\"row\":1}}").Sequence;
            writer.Append("state.observe", Observation(sequence, restored));
        }
        string resumed = File.ReadAllText(path);
        RunRecord decoded = Sts2PgnParser.Parse(resumed);
        Assert.Equal(2, decoded.Entries.Count(entry => entry.Kind == "run.resume"));
        Assert.True(Sts2PgnValidator.Validate(decoded).IsValid);
        JsonElement restoredState = decoded.Entries[^1].Payload.GetProperty("state");
        Assert.Equal(JsonValueKind.Null, restoredState.GetProperty("combat").ValueKind);
        Assert.Empty(restoredState.GetProperty("players")[0].GetProperty("deck").EnumerateArray());
        Assert.Empty(restoredState.GetProperty("choices").GetProperty("rewards").EnumerateArray());
        Assert.False(restoredState.GetProperty("run").TryGetProperty("abandoned_only", out _));
        string[] lines = resumed.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(" run.resume ", StringComparison.Ordinal))
            {
                Assert.Contains(" state.observe ", lines[i + 1]);
                Assert.Contains(version == 2 ? " state.delta " : " state.observe ", lines[i + 3]);
            }
        }
    }

    [Fact]
    public void DeltaCannotReachAcrossResumeEvenWithAValidOldBase()
    {
        string wire = Sts2PgnWriter.Write(Trace(State(80), State(79)));
        string full = Assert.Single(Lines(wire, "state.observe"));
        string resume = "4 run.resume {\"data\":{}}\n";
        string delta = Assert.Single(Lines(wire, "state.delta"));
        string input = wire[..(wire.IndexOf(full, StringComparison.Ordinal) + full.Length)] + "\n" + resume + delta + "\n";
        Assert.Throws<Sts2PgnParseException>(() => Sts2PgnParser.Parse(input));
    }

    [Fact]
    public void GzipFinalizationPreservesDeltaWireAndTransparentReading()
    {
        RunRecord record = Trace(State(80), State(79));
        string path = Path.Combine(_directory, "complete.sts2pgn");
        File.WriteAllText(path, Sts2PgnWriter.Write(record));
        string compressed = Sts2PgnFileFinalizer.FinalizeRecording(path, RunResult.Defeat);
        using StreamReader reader = Sts2PgnFile.OpenText(compressed);
        string text = reader.ReadToEnd();
        Assert.Single(Lines(text, "state.delta"));
        AssertSameRecords(record, Sts2PgnParser.Parse(text));
        Assert.Equal(0, Tool.Program.Main(["validate", compressed]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullAndDeltaReplayHaveIdenticalResultsIncludingUnchangedFieldDivergence(bool mismatch)
    {
        RunRecord original = Trace(State(80), State(79));
        RunRecord decoded = Sts2PgnParser.Parse(Sts2PgnWriter.Write(original));
        Dictionary<long, JsonElement> states = original.Entries.Where(e => e.Kind == "state.observe")
            .ToDictionary(e => e.Sequence, e => e.Payload.GetProperty("state"));
        if (mismatch)
        {
            JsonObject wrong = JsonNode.Parse(states[5].GetRawText())!.AsObject();
            wrong["run"]!["unchanged"] = "simulator accidentally changed this";
            states[5] = Element(wrong);
        }
        ReplayExecutionResult full = await new ReplayEngine().ExecuteAsync(original, new Simulator(states));
        ReplayExecutionResult delta = await new ReplayEngine().ExecuteAsync(decoded, new Simulator(states));
        Assert.Equal(!mismatch, full.Succeeded);
        Assert.Equal(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(full)),
            CanonicalJson.Serialize(JsonSerializer.SerializeToElement(delta)));
        if (mismatch)
        {
            Assert.Equal(5, delta.Failure!.Sequence);
            Assert.Equal("/run/unchanged", Assert.Single(delta.Failure.Divergence!.Differences).Path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleResumesRetainReplayInitializationAndFirstDivergence(bool mismatch)
    {
        RunRecordBuilder builder = Builder();
        ObserveAction(builder, State(40));
        for (int reload = 1; reload <= 2; reload++)
        {
            JsonObject restored = State(80);
            builder.Append("run.resume", Element(new JsonObject
            {
                ["data"] = new JsonObject { ["reloads"] = reload, ["state"] = restored.DeepClone() },
            }));
            Observe(builder, restored, "run.resume");
            ObserveAction(builder, State(70));
        }
        RunRecord original = builder.Build(RunResult.Defeat);
        string wire = Sts2PgnWriter.Write(original);
        Assert.Equal(2, Lines(wire, "state.delta").Length);
        RunRecord decoded = Sts2PgnParser.Parse(wire);
        Dictionary<long, JsonElement> states = original.Entries.Where(e => e.Kind == "state.observe")
            .ToDictionary(e => e.Sequence, e => e.Payload.GetProperty("state"));
        long lastSequence = original.Entries[^1].Sequence;
        if (mismatch)
        {
            states[lastSequence] = Element(State(69));
        }
        Simulator fullSimulator = new(states);
        Simulator deltaSimulator = new(states);
        ReplayExecutionResult full = await new ReplayEngine().ExecuteAsync(original, fullSimulator);
        ReplayExecutionResult delta = await new ReplayEngine().ExecuteAsync(decoded, deltaSimulator);
        Assert.Equal(!mismatch, full.Succeeded);
        Assert.Equal(new[] { "run.start", "run.resume", "run.resume" }, deltaSimulator.Initializations);
        Assert.Equal(fullSimulator.Initializations, deltaSimulator.Initializations);
        Assert.Equal(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(full)),
            CanonicalJson.Serialize(JsonSerializer.SerializeToElement(delta)));
        if (mismatch)
        {
            Assert.Equal(lastSequence, delta.Failure!.Sequence);
            Assert.Equal("/players/p0/current_hp", Assert.Single(delta.Failure.Divergence!.Differences).Path);
        }
    }

    private static RunRecordBuilder Builder() => TestRecordFactory.CreateBuilder().SetTag("FormatVersion", "2")
        .AppendJson("run.start", "{\"data\":{}}");

    private static RunRecord Trace(params JsonObject[] states)
    {
        RunRecordBuilder builder = Builder();
        foreach (JsonObject state in states)
        {
            ObserveAction(builder, state);
        }
        return builder.Build(RunResult.Defeat);
    }

    private static void ObserveAction(RunRecordBuilder builder, JsonObject state)
    {
        builder.AppendJson("map.choose", "{\"actor\":\"p0\",\"data\":{\"col\":1,\"row\":1}}");
        Observe(builder, state);
    }

    private static void Observe(RunRecordBuilder builder, JsonObject state, string context = "action.settled") =>
        builder.Append("state.observe", Observation(builder.Entries[^1].Sequence, state, context));

    private static JsonElement Observation(long after, JsonObject state, string context = "action.settled")
    {
        JsonObject observation = JsonSerializer.SerializeToNode(StateObservationFactory.Create(after, context, Element(state)))!.AsObject();
        // These names must remain available to unknown observation metadata, not collide with the wire envelope.
        observation["base"] = "producer extension";
        observation["patch"] = new JsonArray(1, 2, 3);
        return Element(observation);
    }

    private static JsonObject State(int hp) => JsonSerializer.SerializeToNode(new
    {
        scope = "run.core",
        run = new { seed = "TEST", act = 0, floor = 1, unchanged = "keep" },
        acts = new[] { new { index = 0, model_id = "ACT.OVERGROWTH" } },
        players = new[] { new { slot = "p0", character_model = "CHARACTER.IRONCLAD", current_hp = hp, deck = new[] { "STRIKE", "DEFEND" } } },
        rooms = Array.Empty<object>(),
        choices = new { player_choices = Array.Empty<object>(), rewards = Array.Empty<object>() },
        special_event = (object?)null,
        combat = (object?)null,
        extra = new { repeated = new string('x', 2000) },
    })!.AsObject();

    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
    private static string[] Lines(string wire, string kind) => wire.Split('\n').Where(line => line.Contains($" {kind} ", StringComparison.Ordinal)).ToArray();
    private static JsonObject Payload(string line) => JsonNode.Parse(line.Split(' ', 3)[2])!.AsObject();

    private static void AssertSameRecords(RunRecord expected, RunRecord actual)
    {
        Assert.Equal(expected.Result, actual.Result);
        Assert.Equal(expected.Entries.Count, actual.Entries.Count);
        foreach ((RunRecordEntry left, RunRecordEntry right) in expected.Entries.Zip(actual.Entries))
        {
            Assert.Equal(left.Sequence, right.Sequence);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(CanonicalJson.Serialize(left.Payload), CanonicalJson.Serialize(right.Payload));
        }
    }

    private sealed class Simulator(Dictionary<long, JsonElement> states) : IReplaySimulatorAdapter
    {
        public List<string> Initializations { get; } = [];
        public ValueTask<ReplayCompatibilityResult> CheckCompatibilityAsync(RunMetadata metadata, CancellationToken cancellationToken = default) => ValueTask.FromResult(ReplayCompatibilityResult.Supported);
        public ValueTask<ReplayConsumeResult> InitializeAsync(RunMetadata metadata, ReplayStep runStart, CancellationToken cancellationToken = default)
        {
            Initializations.Add(runStart.Kind);
            return ValueTask.FromResult(ReplayConsumeResult.Success);
        }
        public ValueTask<ReplayConsumeResult> ConsumeAsync(ReplayStep step, CancellationToken cancellationToken = default) => ValueTask.FromResult(ReplayConsumeResult.Success);
        public ValueTask<JsonElement> CaptureStateAsync(ReplayObservationRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(states[request.Sequence]);
        public ValueTask<RunResult> GetResultAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(RunResult.Defeat);
    }

    private sealed class FailingWriter : StringWriter
    {
        public bool Fail { get; set; }
        public override void Write(string? value)
        {
            if (Fail)
            {
                throw new IOException("Injected disk write failure.");
            }
            base.Write(value);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
