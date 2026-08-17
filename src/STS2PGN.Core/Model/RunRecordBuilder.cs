using System.Text.Json;

namespace STS2PGN.Model;

/// <summary>
/// Append-oriented construction API used by recorders and tests.
/// </summary>
public sealed class RunRecordBuilder
{
    private readonly List<TagPair> _tags = [];
    private readonly List<RunRecordEntry> _entries = [];

    public IReadOnlyList<TagPair> Tags => _tags.AsReadOnly();
    public IReadOnlyList<RunRecordEntry> Entries => _entries.AsReadOnly();

    public RunRecordBuilder()
    {
    }

    public RunRecordBuilder(IEnumerable<TagPair> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags.AddRange(tags.Select(tag => new TagPair(tag.Name, tag.Value)));
    }

    public RunRecordBuilder AddTag(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _tags.Add(new TagPair(name, value));
        return this;
    }

    public RunRecordBuilder SetTag(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        int firstIndex = _tags.FindIndex(tag => string.Equals(tag.Name, name, StringComparison.Ordinal));
        _tags.RemoveAll(tag => string.Equals(tag.Name, name, StringComparison.Ordinal));

        TagPair replacement = new(name, value);
        if (firstIndex < 0)
        {
            _tags.Add(replacement);
        }
        else
        {
            _tags.Insert(firstIndex, replacement);
        }

        return this;
    }

    public RunRecordBuilder Append(string kind, JsonElement payload)
    {
        _entries.Add(new RunRecordEntry(_entries.Count + 1L, kind, payload));
        return this;
    }

    public RunRecordBuilder AppendJson(string kind, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        return Append(kind, document.RootElement);
    }

    public RunRecord Build(RunResult result = RunResult.Incomplete) =>
        new(new RunMetadata(_tags), _entries, result);
}
