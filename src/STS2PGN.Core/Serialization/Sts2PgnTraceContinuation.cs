using System.Text;
using STS2PGN.Model;
using STS2PGN.Parsing;

namespace STS2PGN.Serialization;

/// <summary>
/// Reopens an incomplete trace so a resumed run can append to it. The existing
/// content is strictly parsed first; a crash-truncated unterminated final line is
/// physically removed so every appended record extends a valid prefix that ends in
/// exactly one newline. A finalized trace, or one whose complete records do not
/// parse, cannot be continued.
/// </summary>
public sealed class Sts2PgnTraceContinuation : IDisposable
{
    private readonly FileStream _file;
    private readonly StreamWriter _text;
    private bool _disposed;

    public string Path { get; }

    /// <summary>The complete records already on disk, ending at the append position.</summary>
    public RunRecord Prefix { get; }

    /// <summary>Whether a crash-truncated final line was removed before appending.</summary>
    public bool RemovedTruncatedFinalLine { get; }

    public Sts2PgnStreamWriter Writer { get; }

    private Sts2PgnTraceContinuation(
        string path,
        RunRecord prefix,
        bool removedTruncatedFinalLine,
        FileStream file,
        StreamWriter text,
        Sts2PgnStreamWriter writer)
    {
        Path = path;
        Prefix = prefix;
        RemovedTruncatedFinalLine = removedTruncatedFinalLine;
        _file = file;
        _text = text;
        Writer = writer;
    }

    public static Sts2PgnTraceContinuation Open(string path, Sts2PgnParseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new Sts2PgnParseOptions();

        FileStream file = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            byte[] content = new byte[file.Length];
            file.ReadExactly(content);
            RunRecord prefix = Sts2PgnParser.Parse(Encoding.UTF8.GetString(content), options);
            if (prefix.Result != RunResult.Incomplete)
            {
                throw new InvalidDataException("A finalized trace cannot be continued.");
            }

            if (prefix.Entries.Count == 0 || prefix.Entries[0].Kind != "run.start")
            {
                throw new InvalidDataException("A trace without a complete run.start record cannot be continued.");
            }

            bool removedTail = false;
            if (prefix.RecoveredTruncatedFinalLine)
            {
                int lastNewline = Array.LastIndexOf(content, (byte)'\n');
                if (lastNewline < 0)
                {
                    throw new InvalidDataException("The trace has no complete line to continue from.");
                }

                file.SetLength(lastNewline + 1);
                file.Flush(flushToDisk: true);
                removedTail = true;
            }

            file.Seek(0, SeekOrigin.End);
            StreamWriter text = new(
                file,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024,
                leaveOpen: true)
            {
                NewLine = "\n",
            };
            if (!removedTail && content.Length > 0 && content[^1] != (byte)'\n')
            {
                // The final line held a complete record but the newline was lost in a
                // crash. Terminate it so the appended records extend a valid prefix.
                text.Write('\n');
                text.Flush();
                file.Flush(flushToDisk: true);
            }

            long nextSequence = prefix.Entries[^1].Sequence + 1;
            Sts2PgnStreamWriter writer = Sts2PgnStreamWriter.Continue(text, nextSequence, leaveOpen: true);
            return new Sts2PgnTraceContinuation(path, prefix, removedTail, file, text, writer);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Writer.Dispose();
        _text.Dispose();
        _file.Dispose();
    }
}
