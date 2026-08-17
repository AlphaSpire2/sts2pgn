using System.Text;
using STS2PGN.Model;

namespace STS2PGN.Serialization;

/// <summary>
/// Atomically updates the PGN-style Result header after the append-only stream has
/// already written and durably flushed its authoritative terminal result.
/// </summary>
public static class Sts2PgnFileFinalizer
{
    public static void FinalizeResultHeader(string path, RunResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (result == RunResult.Incomplete)
        {
            throw new ArgumentException("A finalized file requires a terminal result.", nameof(result));
        }

        const string incompleteHeader = "[Result \"*\"]";
        string finalHeader = $"[Result \"{result.ToToken()}\"]";
        string contents = File.ReadAllText(path, Encoding.UTF8);
        int headerIndex = contents.IndexOf(incompleteHeader, StringComparison.Ordinal);
        if (headerIndex < 0)
        {
            throw new InvalidDataException("The trace Result header could not be finalized.");
        }

        contents = contents[..headerIndex] + finalHeader + contents[(headerIndex + incompleteHeader.Length)..];
        string temporaryPath = $"{path}.finalizing-{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream temporary = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(
                temporary,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024,
                leaveOpen: true))
            {
                writer.NewLine = "\n";
                writer.Write(contents);
                writer.Flush();
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
