using System.IO.Compression;
using System.Text;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class Sts2PgnCompressionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sts2pgn-compression-").FullName;

    [Theory]
    [InlineData(RunResult.Victory)]
    [InlineData(RunResult.Defeat)]
    [InlineData(RunResult.Abandoned)]
    [InlineData(RunResult.Diverged)]
    public void FinalizationReplacesPlainTraceWithExactFinalizedBytes(RunResult result)
    {
        string path = Path.Combine(_directory, "run.sts2pgn");
        // Exercise multiple compression buffers and UTF-8, retaining unknown fields verbatim.
        string text = Sts2PgnWriter.Write(TestRecordFactory.CreateValid(result))
            .Replace("\"extensions\":{}", "\"extensions\":{\"note\":\"" + new string('界', 70000) + "\"}");
        File.WriteAllText(path, text, new UTF8Encoding(false));

        string compressed = Sts2PgnFileFinalizer.FinalizeRecording(path, result);

        Assert.Equal(path + ".gz", compressed);
        Assert.False(File.Exists(path));
        Assert.Equal(new[] { compressed }, Directory.GetFiles(_directory));
        Assert.True(new FileInfo(compressed).Length < Encoding.UTF8.GetByteCount(text));
        using StreamReader reader = Sts2PgnFile.OpenText(compressed);
        string actual = reader.ReadToEnd();
        Assert.Equal(text.Replace("[Result \"*\"]", $"[Result \"{result.ToToken()}\"]"), actual);
        Assert.Equal(result, Sts2PgnParser.Parse(actual).Result);
    }

    [Fact]
    public void IncompleteRecordingIsNotChangedOrCompressed()
    {
        string path = Path.Combine(_directory, "run.sts2pgn");
        string text = Sts2PgnWriter.Write(TestRecordFactory.CreateValid());
        File.WriteAllText(path, text);

        Assert.Throws<ArgumentException>(() => Sts2PgnFileFinalizer.FinalizeRecording(path, RunResult.Incomplete));

        Assert.Equal(text, File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
    }

    [Fact]
    public void ExistingDestinationIsPreservedAndPlainTraceSurvivesPublicationFailure()
    {
        string path = Path.Combine(_directory, "run.sts2pgn");
        string text = Sts2PgnWriter.Write(TestRecordFactory.CreateValid(RunResult.Victory));
        File.WriteAllText(path, text);
        File.WriteAllText(path + ".gz", "existing recording");

        Assert.Throws<IOException>(() => Sts2PgnFileFinalizer.FinalizeRecording(path, RunResult.Victory));

        Assert.Equal("existing recording", File.ReadAllText(path + ".gz"));
        Assert.Equal(text.Replace("[Result \"*\"]", "[Result \"VICTORY\"]"), File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("plain.sts2pgn", false)]
    [InlineData("compressed.sts2pgn.gz", true)]
    [InlineData("renamed.sts2pgn", true)]
    public void OpenTextReadsBothTransportsWithoutChangingTheTrace(string name, bool gzip)
    {
        string path = Path.Combine(_directory, name);
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "minimal.sts2pgn"));
        if (gzip)
        {
            WriteGzip(path, text);
        }
        else
        {
            File.WriteAllText(path, text);
        }

        using (StreamReader reader = Sts2PgnFile.OpenText(path))
        {
            Assert.Equal(text, reader.ReadToEnd());
        }

        // Disposing the text reader must release the underlying compressed/plain file too.
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("a.sts2pgn", true)]
    [InlineData("a.STS2PGN.GZ", true)]
    [InlineData("a.sts2pgn.gz", true)]
    [InlineData("a.sts2pgn.gz.compressing-id.tmp", false)]
    [InlineData("a.playback.json", false)]
    [InlineData("a.gz", false)]
    public void PlaybackDiscoveryRecognizesOnlyTraceFilenames(string path, bool expected) =>
        Assert.Equal(expected, Sts2PgnFile.IsTracePath(path));

    [Fact]
    public void ExistingCliCommandsReadCompressedRecordingsAndScripts()
    {
        string recording = Path.Combine(_directory, "recording.sts2pgn.gz");
        WriteGzip(recording, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "minimal.sts2pgn")));
        Assert.Equal(0, Tool.Program.Main(["validate", recording]));
        Assert.Equal(0, Tool.Program.Main(["summarize", recording]));

        string script = Path.Combine(_directory, "script.sts2pgn.gz");
        WriteGzip(script, Sts2PgnWriter.Write(TestRecordFactory.CreateValidScript()));
        Assert.Equal(0, Tool.Program.Main(["validate", "--script", script]));
        using StreamReader reader = Sts2PgnFile.OpenText(script);
        Assert.Single(DecisionScript.Load(reader).Decisions);
    }

    [Fact]
    public void ToScriptStripsBothExtensionsFromDefaultOutputName()
    {
        RunRecord recording = TestRecordFactory.CreateBuilder()
            .AppendJson("run.start", TestRecordFactory.ScriptRunStart)
            .AppendJson("map.choose", TestRecordFactory.ScriptMapChoose)
            .Build(RunResult.Victory);
        string path = Path.Combine(_directory, "run.sts2pgn.gz");
        WriteGzip(path, Sts2PgnWriter.Write(recording));

        Assert.Equal(0, Tool.Program.Main(["to-script", path, "--producer", "test/1"]));

        string output = Path.Combine(_directory, "run.script.sts2pgn");
        Assert.True(File.Exists(output));
        using StreamReader reader = Sts2PgnFile.OpenText(output);
        Assert.Single(DecisionScript.Load(reader).Decisions);
        Assert.False(File.Exists(Path.Combine(_directory, "run.sts2pgn.script.sts2pgn")));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("summarize")]
    [InlineData("to-script")]
    public void CorruptGzipReportsCommandFailure(string command)
    {
        string path = Path.Combine(_directory, "corrupt.sts2pgn.gz");
        WriteGzip(path, Sts2PgnWriter.Write(TestRecordFactory.CreateValid()));
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^8] ^= 0xff; // Invalid CRC in an otherwise complete gzip member.
        File.WriteAllBytes(path, bytes);

        Assert.Equal(1, Tool.Program.Main(command == "to-script"
            ? [command, path, "--producer", "test/1"]
            : [command, path]));
    }

    private static void WriteGzip(string path, string text)
    {
        using FileStream file = File.Create(path);
        using GZipStream gzip = new(file, CompressionLevel.Optimal);
        gzip.Write(Encoding.UTF8.GetBytes(text));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
