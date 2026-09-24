using System.Security.Cryptography;
using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Serialization;

namespace STS2PGN.Tests;

public sealed class Sts2PgnSidecarTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sts2pgn-sidecar-").FullName;

    [Fact]
    public void FinalizerIndexesPublishedArchiveWithExactFileIdentity()
    {
        string path = WriteRecording();
        string archive = Sts2PgnFileFinalizer.FinalizeRecording(path, RunResult.Victory);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(archive + Sts2PgnSidecar.Extension));
        JsonElement index = json.RootElement;
        Assert.Equal(1, index.GetProperty("schema_version").GetInt32());
        Assert.Equal(Path.GetFileName(archive), index.GetProperty("file").GetString());
        Assert.Equal(new FileInfo(archive).Length, index.GetProperty("file_size_bytes").GetInt64());
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))), index.GetProperty("file_sha256").GetString());
        Assert.Equal("000123", index.GetProperty("seed").GetString());
        Assert.Equal("recording", index.GetProperty("profile").GetString());
        Assert.Equal("VICTORY", index.GetProperty("result").GetString());
        Assert.Equal("IRONCLAD", index.GetProperty("players")[0].GetProperty("character").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void BackfillUsesLegacyIdentityAndTerminalResultWithoutChangingSource()
    {
        string path = WriteRecording();
        string text = File.ReadAllText(path).Replace("[Profile \"recording\"]\n", "");
        File.WriteAllText(path, text);
        Assert.Contains("[Result \"*\"]", text);
        Assert.Equal(0, Tool.Program.Main(["index", path]));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path + Sts2PgnSidecar.Extension));
        Assert.Equal("VICTORY", json.RootElement.GetProperty("result").GetString());
        Assert.Equal("recording", json.RootElement.GetProperty("profile").GetString());
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void SidecarFailurePreservesArchiveAndReportsWarning()
    {
        string path = WriteRecording();
        string sidecar = path + ".gz" + Sts2PgnSidecar.Extension;
        File.WriteAllText(sidecar, "existing metadata");
        Exception? warning = null;
        string archive = Sts2PgnFileFinalizer.FinalizeRecording(path, RunResult.Victory, error => warning = error);
        Assert.IsType<IOException>(warning);
        Assert.True(File.Exists(archive));
        Assert.False(File.Exists(path));
        Assert.Equal("existing metadata", File.ReadAllText(sidecar));
        using StreamReader reader = Sts2PgnFile.OpenText(archive);
        Assert.EndsWith("VICTORY\n", reader.ReadToEnd());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ReplacementRequiresExplicitOptionAndCliContinuesAfterFailure()
    {
        string path = WriteRecording();
        string sidecar = Sts2PgnSidecar.Write(path);
        File.WriteAllText(sidecar, "stale");
        Assert.Equal(1, Tool.Program.Main(["index", path]));
        Assert.Equal("stale", File.ReadAllText(sidecar));
        Assert.Equal(0, Tool.Program.Main(["index", "--overwrite", path]));
        using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(sidecar));
        Assert.Equal(1, Tool.Program.Main(["index", "--overwrite", Path.Combine(_directory, "missing"), path]));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("conflict")]
    [InlineData("trailing")]
    public void InvalidIndexInputDoesNotPublishMetadata(string failure)
    {
        string path = WriteRecording();
        string text = File.ReadAllText(path);
        text = failure switch
        {
            "open" => text[..text.LastIndexOf("VICTORY", StringComparison.Ordinal)],
            "conflict" => text.Replace("[Profile \"recording\"]", "[Profile \"script\"]"),
            _ => text + "extra\n",
        };
        File.WriteAllText(path, text);
        Assert.Equal(1, Tool.Program.Main(["index", path]));
        Assert.False(File.Exists(path + Sts2PgnSidecar.Extension));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliDetectsAndIndexesLegacyAndExplicitScripts(bool legacy)
    {
        string path = Path.Combine(_directory, "script.sts2pgn");
        string text = Sts2PgnWriter.Write(TestRecordFactory.CreateValidScript());
        File.WriteAllText(path, legacy ? text.Replace("[Profile \"script\"]\n", "") : text);
        Assert.Equal(0, Tool.Program.Main(["validate", path]));
        Assert.Equal(0, Tool.Program.Main(["index", path]));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path + Sts2PgnSidecar.Extension));
        Assert.Equal("script", json.RootElement.GetProperty("profile").GetString());
        Assert.Equal("*", json.RootElement.GetProperty("result").GetString());
    }

    private string WriteRecording()
    {
        string path = Path.Combine(_directory, "run.sts2pgn");
        RunRecord record = TestRecordFactory.CreateBuilder().SetTag("Seed", "000123")
            .AppendJson("run.start", "{\"data\":{}}").Build(RunResult.Victory);
        File.WriteAllText(path, Sts2PgnWriter.Write(record));
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
