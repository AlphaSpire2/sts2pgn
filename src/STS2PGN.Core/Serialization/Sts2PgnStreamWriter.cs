using System.Text.Json;
using STS2PGN.Model;

namespace STS2PGN.Serialization;

/// <summary>
/// Writes an append-only trace and flushes each complete record to durable storage.
/// An interrupted writer therefore leaves a parseable prefix ending at the last newline.
/// </summary>
public sealed class Sts2PgnStreamWriter : IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;
    private long _nextSequence = 1;
    private bool _completed;
    private bool _disposed;

    public long NextSequence => _nextSequence;
    public bool IsCompleted => _completed;

    public Sts2PgnStreamWriter(TextWriter writer, RunMetadata metadata, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(metadata);

        _writer = writer;
        _leaveOpen = leaveOpen;
        Sts2PgnWriter.WriteMetadata(_writer, metadata);
        FlushDurably();
    }

    public RunRecordEntry Append(string kind, JsonElement payload)
    {
        ThrowIfUnavailable();
        RunRecordEntry entry = new(_nextSequence, kind, payload);
        Sts2PgnWriter.WriteEntry(_writer, entry);
        FlushDurably();
        _nextSequence++;
        return entry;
    }

    public RunRecordEntry AppendJson(string kind, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        return Append(kind, document.RootElement);
    }

    public void Complete(RunResult result)
    {
        ThrowIfUnavailable();
        if (result == RunResult.Incomplete)
        {
            throw new ArgumentException("A completed stream requires a terminal result.", nameof(result));
        }

        _writer.Write(result.ToToken());
        _writer.Write('\n');
        FlushDurably();
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FlushDurably();
        if (!_leaveOpen)
        {
            _writer.Dispose();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The trace already has a terminal result.");
        }
    }

    private void FlushDurably()
    {
        _writer.Flush();
        if (_writer is StreamWriter { BaseStream: FileStream file })
        {
            file.Flush(flushToDisk: true);
        }
    }
}
