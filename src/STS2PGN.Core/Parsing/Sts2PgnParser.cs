using System.Globalization;
using System.Text;
using System.Text.Json;
using STS2PGN.Model;
using STS2PGN.Validation;

namespace STS2PGN.Parsing;

public static class Sts2PgnParser
{
    public static RunRecord Parse(string content, Sts2PgnParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        using StringReader reader = new(content);
        return Parse(reader, options);
    }

    /// <summary>
    /// Parses incrementally from a text reader. The reader remains open.
    /// </summary>
    public static RunRecord Parse(TextReader reader, Sts2PgnParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new Sts2PgnParseOptions();
        if (options.MaxJsonDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxJsonDepth must be positive.");
        }

        List<TagPair> tags = [];
        List<RunRecordEntry> entries = [];
        RunResult result = RunResult.Incomplete;
        bool inRecordSection = false;
        bool sawTerminalResult = false;
        bool recovered = false;
        int lineNumber = 0;

        while (TryReadPhysicalLine(reader, out string line, out bool wasTerminated))
        {
            lineNumber++;

            if (sawTerminalResult)
            {
                throw Error("No content is allowed after the terminal result.", lineNumber);
            }

            if (!inRecordSection)
            {
                if (line.Length == 0)
                {
                    if (tags.Count == 0)
                    {
                        throw Error("The tag section cannot be empty.", lineNumber);
                    }

                    inRecordSection = true;
                    continue;
                }

                if (tags.Count > 0 && char.IsAsciiDigit(line[0]))
                {
                    throw Error("The tag section must be followed by a blank line.", lineNumber);
                }

                tags.Add(ParseTag(line, lineNumber));
                continue;
            }

            if (line.Length == 0)
            {
                throw Error("Blank lines are not allowed in the record section.", lineNumber);
            }

            if (RunResultExtensions.TryParseToken(line, out RunResult parsedResult))
            {
                result = parsedResult;
                sawTerminalResult = true;
                continue;
            }

            if (!wasTerminated
                && options.RecoverTruncatedFinalLine
                && IsPrefixOfTerminalResult(line))
            {
                recovered = true;
                break;
            }

            RunRecordEntry? entry = ParseEntry(line, lineNumber, wasTerminated, options, out bool entryRecovered);
            if (entryRecovered)
            {
                recovered = true;
                break;
            }

            entries.Add(entry!);
        }

        if (lineNumber == 0)
        {
            throw Error("The document is empty.", 1);
        }

        if (!inRecordSection)
        {
            throw Error("The tag section must be followed by a blank line.", lineNumber + 1);
        }

        return new RunRecord(new RunMetadata(tags), entries, result, recovered);
    }

    private static TagPair ParseTag(string line, int lineNumber)
    {
        int index = 0;
        if (line.Length < 5 || line[index++] != '[')
        {
            throw Error("Expected a tag pair such as [Key \"value\"].", lineNumber);
        }

        int nameStart = index;
        while (index < line.Length && line[index] != ' ' && line[index] != '\t')
        {
            index++;
        }

        string name = line[nameStart..index];
        if (!FormatSyntax.IsTagName(name))
        {
            throw Error($"Invalid tag name '{name}'.", lineNumber);
        }

        SkipHorizontalWhitespace(line, ref index);
        if (index >= line.Length || line[index++] != '"')
        {
            throw Error("Expected a quoted tag value.", lineNumber);
        }

        StringBuilder value = new();
        bool closedQuote = false;
        while (index < line.Length)
        {
            char character = line[index++];
            if (character == '"')
            {
                closedQuote = true;
                break;
            }

            if (character < 0x20)
            {
                throw Error("Tag values cannot contain unescaped control characters.", lineNumber);
            }

            if (character != '\\')
            {
                value.Append(character);
                continue;
            }

            if (index >= line.Length)
            {
                throw Error("Tag value ends with an incomplete escape sequence.", lineNumber);
            }

            value.Append(line[index++] switch
            {
                '"' => '"',
                '\\' => '\\',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                char invalid => throw Error($"Unsupported tag escape '\\{invalid}'.", lineNumber),
            });
        }

        if (!closedQuote)
        {
            throw Error("Tag value is missing its closing quote.", lineNumber);
        }

        SkipHorizontalWhitespace(line, ref index);
        if (index >= line.Length || line[index++] != ']')
        {
            throw Error("Tag pair is missing its closing bracket.", lineNumber);
        }

        SkipHorizontalWhitespace(line, ref index);
        if (index != line.Length)
        {
            throw Error("Unexpected content after the tag pair.", lineNumber);
        }

        return new TagPair(name, value.ToString());
    }

    private static RunRecordEntry? ParseEntry(
        string line,
        int lineNumber,
        bool wasTerminated,
        Sts2PgnParseOptions options,
        out bool recovered)
    {
        recovered = false;
        int firstSpace = line.IndexOf(' ');
        int secondSpace = firstSpace < 0 ? -1 : line.IndexOf(' ', firstSpace + 1);

        if (firstSpace <= 0 || secondSpace <= firstSpace + 1 || secondSpace == line.Length - 1)
        {
            throw Error("Expected 'sequence kind {json payload}'.", lineNumber);
        }

        string sequenceText = line[..firstSpace];
        if (!sequenceText.All(char.IsAsciiDigit)
            || !long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
            || sequence <= 0)
        {
            throw Error($"Invalid sequence number '{sequenceText}'.", lineNumber);
        }

        string kind = line[(firstSpace + 1)..secondSpace];
        if (!FormatSyntax.IsRecordKind(kind))
        {
            throw Error($"Invalid record kind '{kind}'.", lineNumber);
        }

        string payloadText = line[(secondSpace + 1)..];
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                payloadText,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = options.MaxJsonDepth,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Error("A record payload must be a JSON object.", lineNumber);
            }

            return new RunRecordEntry(sequence, kind, document.RootElement);
        }
        catch (JsonException exception) when (
            !wasTerminated
            && options.RecoverTruncatedFinalLine
            && IsLikelyTruncatedJson(exception, payloadText))
        {
            recovered = true;
            return null;
        }
        catch (JsonException exception)
        {
            throw new Sts2PgnParseException($"Invalid JSON payload: {exception.Message}", lineNumber, exception);
        }
    }

    private static bool TryReadPhysicalLine(TextReader reader, out string line, out bool wasTerminated)
    {
        StringBuilder builder = new();
        while (true)
        {
            int next = reader.Read();
            if (next < 0)
            {
                line = builder.ToString();
                wasTerminated = false;
                return builder.Length > 0;
            }

            if (next != '\n')
            {
                builder.Append((char)next);
                continue;
            }

            if (builder.Length > 0 && builder[^1] == '\r')
            {
                builder.Length--;
            }

            line = builder.ToString();
            wasTerminated = true;
            return true;
        }
    }

    private static bool IsPrefixOfTerminalResult(string value) =>
        value.Length > 0
        && new[] { "VICTORY", "DEFEAT", "ABANDONED", "DIVERGED" }
            .Any(token => token.StartsWith(value, StringComparison.Ordinal));

    private static bool IsLikelyTruncatedJson(JsonException exception, string payloadText)
    {
        long bytePosition = exception.BytePositionInLine ?? -1;
        int payloadByteLength = Encoding.UTF8.GetByteCount(payloadText);
        return bytePosition >= payloadByteLength
               || exception.Message.Contains("open JSON object or array", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("end of string", StringComparison.OrdinalIgnoreCase);
    }

    private static void SkipHorizontalWhitespace(string line, ref int index)
    {
        while (index < line.Length && line[index] is ' ' or '\t')
        {
            index++;
        }
    }

    private static Sts2PgnParseException Error(string message, int lineNumber) => new(message, lineNumber);
}
