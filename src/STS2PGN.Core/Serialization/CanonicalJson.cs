using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STS2PGN.Serialization;

/// <summary>
/// Canonical JSON used for portable state digests and deterministic payload output.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonElement value) => Encoding.UTF8.GetString(SerializeToUtf8Bytes(value));

    public static byte[] SerializeToUtf8Bytes(JsonElement value)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, value);
        }

        return stream.ToArray();
    }

    public static string ComputeSha256(JsonElement value)
    {
        byte[] digest = SHA256.HashData(SerializeToUtf8Bytes(value));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, value);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, value);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException($"Unsupported JSON value kind {value.ValueKind}.", nameof(value));
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement value)
    {
        JsonProperty[] properties = value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        for (int index = 1; index < properties.Length; index++)
        {
            if (string.Equals(properties[index - 1].Name, properties[index].Name, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Duplicate JSON property '{properties[index].Name}' cannot be canonicalized.", nameof(value));
            }
        }

        writer.WriteStartObject();
        foreach (JsonProperty property in properties)
        {
            writer.WritePropertyName(property.Name);
            WriteElement(writer, property.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.TryGetInt64(out long signed))
        {
            writer.WriteNumberValue(signed);
            return;
        }

        if (value.TryGetUInt64(out ulong unsigned))
        {
            writer.WriteNumberValue(unsigned);
            return;
        }

        if (value.TryGetDecimal(out decimal exactDecimal))
        {
            string normalized = exactDecimal.ToString("G29", CultureInfo.InvariantCulture);
            writer.WriteRawValue(normalized, skipInputValidation: true);
            return;
        }

        if (value.TryGetDouble(out double floatingPoint) && double.IsFinite(floatingPoint))
        {
            writer.WriteNumberValue(floatingPoint);
            return;
        }

        throw new ArgumentException($"JSON number '{value.GetRawText()}' cannot be represented canonically.", nameof(value));
    }
}
