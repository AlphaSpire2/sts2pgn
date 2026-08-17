using System.Text;
using STS2PGN.Model;
using STS2PGN.Validation;

namespace STS2PGN.Serialization;

public static class Sts2PgnWriter
{
    public static string Write(RunRecord record, Sts2PgnWriterOptions? options = null)
    {
        using StringWriter writer = new(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        Write(writer, record, options);
        return writer.ToString();
    }

    /// <summary>
    /// Writes to the supplied text writer without closing it.
    /// </summary>
    public static void Write(TextWriter writer, RunRecord record, Sts2PgnWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(record);
        options ??= new Sts2PgnWriterOptions();

        if (options.ValidateBeforeWriting)
        {
            Sts2PgnValidationResult validation = Sts2PgnValidator.Validate(record);
            if (!validation.IsValid)
            {
                throw new Sts2PgnValidationException(validation);
            }
        }

        foreach (TagPair tag in record.Metadata.Tags)
        {
            writer.Write('[');
            writer.Write(tag.Name);
            writer.Write(" \"");
            WriteEscapedTagValue(writer, tag.Value);
            writer.Write("\"]\n");
        }

        writer.Write('\n');
        foreach (RunRecordEntry entry in record.Entries)
        {
            writer.Write(entry.Sequence);
            writer.Write(' ');
            writer.Write(entry.Kind);
            writer.Write(' ');
            writer.Write(CanonicalJson.Serialize(entry.Payload));
            writer.Write('\n');
        }

        if (options.IncludeTerminalResult)
        {
            writer.Write(record.Result.ToToken());
            writer.Write('\n');
        }
    }

    private static void WriteEscapedTagValue(TextWriter writer, string value)
    {
        foreach (char character in value)
        {
            writer.Write(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => throw new ArgumentException(
                    $"Tag values cannot contain control character U+{(int)character:X4}.",
                    nameof(value)),
                _ => character.ToString(),
            });
        }
    }
}
