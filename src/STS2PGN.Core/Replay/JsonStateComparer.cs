using System.Text.Json;
using STS2PGN.Serialization;

namespace STS2PGN.Replay;

/// <summary>
/// Compares portable state using JSON Pointer paths and the format's ordered/keyed collection rules.
/// </summary>
public static class JsonStateComparer
{
    public static StateComparisonResult Compare(
        JsonElement expected,
        JsonElement actual,
        StateComparisonRules? rules = null,
        int maxDifferences = 100)
    {
        if (maxDifferences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDifferences));
        }

        List<StateDifference> differences = [];
        int searchLimit = maxDifferences == int.MaxValue ? int.MaxValue : maxDifferences + 1;
        CompareValue(expected, actual, string.Empty, rules ?? StateComparisonRules.Default, differences, searchLimit);
        bool truncated = differences.Count > maxDifferences;
        if (truncated)
        {
            differences.RemoveRange(maxDifferences, differences.Count - maxDifferences);
        }

        return new StateComparisonResult(differences.AsReadOnly(), truncated);
    }

    private static void CompareValue(
        JsonElement expected,
        JsonElement actual,
        string path,
        StateComparisonRules rules,
        List<StateDifference> differences,
        int maximum)
    {
        if (differences.Count >= maximum)
        {
            return;
        }

        if (expected.ValueKind != actual.ValueKind)
        {
            AddDifference(path, expected, actual, differences);
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(expected, actual, path, rules, differences, maximum);
                break;
            case JsonValueKind.Array:
                CompareArrays(expected, actual, path, rules, differences, maximum);
                break;
            default:
                if (!ScalarEquals(expected, actual))
                {
                    AddDifference(path, expected, actual, differences);
                }

                break;
        }
    }

    private static void CompareObjects(
        JsonElement expected,
        JsonElement actual,
        string path,
        StateComparisonRules rules,
        List<StateDifference> differences,
        int maximum)
    {
        if (!TryGetProperties(expected, out SortedDictionary<string, JsonElement>? expectedProperties)
            || !TryGetProperties(actual, out SortedDictionary<string, JsonElement>? actualProperties))
        {
            AddDifference(path, expected, actual, differences);
            return;
        }

        foreach (string name in expectedProperties.Keys.Union(actualProperties.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (differences.Count >= maximum)
            {
                return;
            }

            string childPath = AppendPath(path, name);
            bool hasExpected = expectedProperties.TryGetValue(name, out JsonElement expectedValue);
            bool hasActual = actualProperties.TryGetValue(name, out JsonElement actualValue);
            if (!hasExpected || !hasActual)
            {
                AddDifference(
                    childPath,
                    hasExpected ? expectedValue : null,
                    hasActual ? actualValue : null,
                    differences);
                continue;
            }

            CompareValue(expectedValue, actualValue, childPath, rules, differences, maximum);
        }
    }

    private static void CompareArrays(
        JsonElement expected,
        JsonElement actual,
        string path,
        StateComparisonRules rules,
        List<StateDifference> differences,
        int maximum)
    {
        if (rules.TryGetArrayKey(path, out string keyProperty)
            && TryKeyArray(expected, keyProperty, out SortedDictionary<string, JsonElement>? expectedItems)
            && TryKeyArray(actual, keyProperty, out SortedDictionary<string, JsonElement>? actualItems))
        {
            foreach (string key in expectedItems.Keys.Union(actualItems.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (differences.Count >= maximum)
                {
                    return;
                }

                string childPath = AppendPath(path, key);
                bool hasExpected = expectedItems.TryGetValue(key, out JsonElement expectedItem);
                bool hasActual = actualItems.TryGetValue(key, out JsonElement actualItem);
                if (!hasExpected || !hasActual)
                {
                    AddDifference(
                        childPath,
                        hasExpected ? expectedItem : null,
                        hasActual ? actualItem : null,
                        differences);
                    continue;
                }

                CompareValue(expectedItem, actualItem, childPath, rules, differences, maximum);
            }

            return;
        }

        JsonElement[] expectedItemsInOrder = expected.EnumerateArray().ToArray();
        JsonElement[] actualItemsInOrder = actual.EnumerateArray().ToArray();
        int count = Math.Max(expectedItemsInOrder.Length, actualItemsInOrder.Length);
        for (int index = 0; index < count && differences.Count < maximum; index++)
        {
            string childPath = AppendPath(path, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            bool hasExpected = index < expectedItemsInOrder.Length;
            bool hasActual = index < actualItemsInOrder.Length;
            if (!hasExpected || !hasActual)
            {
                AddDifference(
                    childPath,
                    hasExpected ? expectedItemsInOrder[index] : null,
                    hasActual ? actualItemsInOrder[index] : null,
                    differences);
                continue;
            }

            CompareValue(
                expectedItemsInOrder[index],
                actualItemsInOrder[index],
                childPath,
                rules,
                differences,
                maximum);
        }
    }

    private static bool TryGetProperties(
        JsonElement value,
        out SortedDictionary<string, JsonElement> properties)
    {
        properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryKeyArray(
        JsonElement value,
        string keyProperty,
        out SortedDictionary<string, JsonElement> items)
    {
        items = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty(keyProperty, out JsonElement keyElement)
                || !TryGetKey(keyElement, out string key)
                || !items.TryAdd(key, item))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetKey(JsonElement element, out string key)
    {
        string? value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => CanonicalJson.Serialize(element),
            _ => null,
        };
        key = value ?? string.Empty;
        return value is not null;
    }

    private static bool ScalarEquals(JsonElement expected, JsonElement actual) => expected.ValueKind switch
    {
        JsonValueKind.String => string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal),
        JsonValueKind.Number => string.Equals(
            CanonicalJson.Serialize(expected),
            CanonicalJson.Serialize(actual),
            StringComparison.Ordinal),
        JsonValueKind.True or JsonValueKind.False => expected.GetBoolean() == actual.GetBoolean(),
        JsonValueKind.Null => true,
        _ => string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal),
    };

    private static void AddDifference(
        string path,
        JsonElement? expected,
        JsonElement? actual,
        List<StateDifference> differences) =>
        differences.Add(new StateDifference(path, expected, actual));

    private static string AppendPath(string path, string segment) =>
        path + "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
