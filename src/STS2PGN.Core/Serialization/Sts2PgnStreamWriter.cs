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
    private readonly StateDeltaCodec? _deltaCodec;
    private long _nextSequence = 1;
    private bool _completed;
    private bool _disposed;
    private bool _faulted;

    public long NextSequence => _nextSequence;
    public bool IsCompleted => _completed;

    public Sts2PgnStreamWriter(TextWriter writer, RunMetadata metadata, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(metadata);

        _writer = writer;
        _leaveOpen = leaveOpen;
        _deltaCodec = metadata.GetValue("FormatVersion") == "2" ? new StateDeltaCodec() : null;
        Sts2PgnWriter.WriteMetadata(_writer, metadata);
        FlushDurably();
    }

    private Sts2PgnStreamWriter(TextWriter writer, long nextSequence, bool leaveOpen, int formatVersion)
    {
        _writer = writer;
        _leaveOpen = leaveOpen;
        _nextSequence = nextSequence;
        _deltaCodec = formatVersion == FormatConstants.DeltaEncodingVersion ? new StateDeltaCodec() : null;
    }

    /// <summary>
    /// Continues an existing incomplete trace. The underlying writer must already be
    /// positioned immediately after the final complete, newline-terminated record;
    /// no tag section is written.
    /// </summary>
    public static Sts2PgnStreamWriter Continue(TextWriter writer, long nextSequence, bool leaveOpen = false) =>
        Continue(writer, nextSequence, leaveOpen, formatVersion: 1);

    /// <summary>
    /// Continues using the existing file's version. No baseline is carried across reopen;
    /// the first new observation is full, including for version-two files.
    /// </summary>
    public static Sts2PgnStreamWriter Continue(TextWriter writer, long nextSequence, bool leaveOpen, int formatVersion)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfLessThan(nextSequence, 1);
        if (!FormatConstants.IsSupportedVersion(formatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }
        return new Sts2PgnStreamWriter(writer, nextSequence, leaveOpen, formatVersion);
    }

    public RunRecordEntry Append(string kind, JsonElement payload)
    {
        ThrowIfUnavailable();
        RunRecordEntry entry = new(_nextSequence, kind, payload);
        RunRecordEntry encoded = _deltaCodec?.Encode(entry) ?? entry;
        try
        {
            Sts2PgnWriter.WriteEntry(_writer, encoded);
            FlushDurably();
        }
        catch
        {
            // A failed write may have advanced the codec or left a partial line. Reopen
            // through trace continuation to recover the durable prefix before appending.
            _faulted = true;
            throw;
        }
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
        if (_faulted)
        {
            throw new InvalidOperationException("The trace writer failed; reopen the durable prefix before appending.");
        }
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
