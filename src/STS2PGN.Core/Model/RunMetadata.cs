using System.Collections.ObjectModel;

namespace STS2PGN.Model;

/// <summary>
/// Ordered run tag pairs. Lookup is ordinal and case-sensitive, as required by the format.
/// </summary>
public sealed class RunMetadata
{
    private readonly ReadOnlyCollection<TagPair> _tags;

    public IReadOnlyList<TagPair> Tags => _tags;

    public RunMetadata(IEnumerable<TagPair> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags = Array.AsReadOnly(tags.Select(CloneTag).ToArray());
    }

    public string? GetValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _tags.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.Ordinal))?.Value;
    }

    public IReadOnlyList<string> GetValues(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Array.AsReadOnly(
            _tags
                .Where(tag => string.Equals(tag.Name, name, StringComparison.Ordinal))
                .Select(tag => tag.Value)
                .ToArray());
    }

    private static TagPair CloneTag(TagPair tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return new TagPair(tag.Name, tag.Value);
    }
}
