using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CobbleMusicUpdater;

// Byte-preserving single-value edits. Each edit replaces exactly the value text of one key and leaves every other
// byte of the file (other keys, order, spacing, comments, CRLF/LF, a UTF-8 BOM) untouched. A key that is missing,
// repeated, or not a plain scalar is never guessed at: the edit is refused and the file is left alone.
internal static class SettingText
{
    public const long MaximumFileBytes = 4L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal readonly record struct ValueSpan(int Start, int End, string Raw);

    // Finds the one value span of key in content, or null (missing, repeated, not a scalar, not valid UTF-8).
    public static ValueSpan? Find(byte[] content, string format, string key) => format switch
    {
        "json" => FindJson(content, key),
        "options" => FindLine(content, key, options: true),
        "properties" => FindLine(content, key, options: false),
        _ => null
    };

    public static byte[] Replace(byte[] content, ValueSpan span, string newValue)
    {
        byte[] value = StrictUtf8.GetBytes(newValue);
        var result = new byte[content.Length - (span.End - span.Start) + value.Length];
        Buffer.BlockCopy(content, 0, result, 0, span.Start);
        Buffer.BlockCopy(value, 0, result, span.Start, value.Length);
        Buffer.BlockCopy(content, span.End, result, span.Start + value.Length, content.Length - span.End);
        return result;
    }

    private static ValueSpan? FindJson(byte[] content, string keyPath)
    {
        int offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        string[] wanted = keyPath.Split('.');
        var reader = new Utf8JsonReader(content.AsSpan(offset), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var path = new List<string?>();
        string? pendingName = null;
        ValueSpan? found = null;
        int matches = 0;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pendingName = reader.GetString();
                        break;
                    case JsonTokenType.StartObject:
                        path.Add(pendingName);
                        pendingName = null;
                        break;
                    case JsonTokenType.EndObject:
                        if (path.Count > 0) path.RemoveAt(path.Count - 1);
                        break;
                    case JsonTokenType.StartArray:
                        // Values inside arrays are not addressable by a key path.
                        reader.Skip();
                        pendingName = null;
                        break;
                    case JsonTokenType.String:
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        if (pendingName is not null && IsPath(path, pendingName, wanted))
                        {
                            int start = offset + (int)reader.TokenStartIndex;
                            int end = offset + (int)reader.BytesConsumed;
                            matches++;
                            found = new ValueSpan(start, end, StrictUtf8.GetString(content, start, end - start));
                        }
                        pendingName = null;
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return null;
        }
        return matches == 1 ? found : null;
    }

    private static bool IsPath(List<string?> parents, string name, string[] wanted)
    {
        // parents[0] is the root object (null name).
        if (parents.Count != wanted.Length || parents[0] is not null) return false;
        for (int index = 1; index < parents.Count; index++)
        {
            if (!string.Equals(parents[index], wanted[index - 1], StringComparison.Ordinal)) return false;
        }
        return string.Equals(name, wanted[^1], StringComparison.Ordinal);
    }

    private static ValueSpan? FindLine(byte[] content, string key, bool options)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        ValueSpan? found = null;
        int matches = 0;
        int lineStart = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] is not ('\r' or '\n')) continue;
            string line = text[lineStart..index];
            if (TryValueRange(line, key, options, out int valueStart, out int valueEnd))
            {
                matches++;
                int charStart = lineStart + valueStart;
                int charEnd = lineStart + valueEnd;
                int byteStart = Encoding.UTF8.GetByteCount(text.AsSpan(0, charStart));
                int byteEnd = byteStart + Encoding.UTF8.GetByteCount(text.AsSpan(charStart, charEnd - charStart));
                found = new ValueSpan(byteStart, byteEnd, text[charStart..charEnd]);
            }
            if (index < text.Length && text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            lineStart = index + 1;
        }
        return matches == 1 ? found : null;
    }

    private static bool TryValueRange(string line, string key, bool options, out int valueStart, out int valueEnd)
    {
        valueStart = valueEnd = 0;
        if (options)
        {
            // options.txt: key:value, exactly as Minecraft writes it (no spaces, no comments).
            if (!line.StartsWith(key, StringComparison.Ordinal) || line.Length <= key.Length || line[key.Length] != ':') return false;
            valueStart = key.Length + 1;
            valueEnd = line.Length;
            return true;
        }
        // key = value (spaces around '=' optional); '#' and '!' lines are comments.
        int first = 0;
        while (first < line.Length && line[first] is ' ' or '\t') first++;
        if (first >= line.Length || line[first] is '#' or '!' or '[') return false;
        int equals = line.IndexOf('=', first);
        if (equals < 0 || !line[first..equals].TrimEnd(' ', '\t').Equals(key, StringComparison.Ordinal)) return false;
        int start = equals + 1;
        while (start < line.Length && line[start] is ' ' or '\t') start++;
        int end = line.Length;
        while (end > start && line[end - 1] is ' ' or '\t') end--;
        if (end <= start) return false;
        valueStart = start;
        valueEnd = end;
        return true;
    }

    public static bool IsValidJsonDocument(byte[] content)
    {
        int offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        try
        {
            var reader = new Utf8JsonReader(content.AsSpan(offset), new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

// Removes or re-inserts pack ids in an official Packed Packs profile while keeping the file's own text: kept
// entries keep their exact bytes, the separator between entries is reused, and nothing outside "packIds" moves.
internal static class PackProfileText
{
    private static readonly JsonSerializerOptions IdEncoding = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal sealed record PackList(int ArrayStart, int ArrayEnd, List<(string Id, string Raw)> Entries, string Separator, string Leading, string Trailing);

    public static PackList? Parse(byte[] content)
    {
        int offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        try
        {
            var reader = new Utf8JsonReader(content.AsSpan(offset), new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            PackList? found = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return null;
                bool isPackIds = reader.ValueTextEquals("packIds");
                if (!reader.Read()) return null;
                if (!isPackIds)
                {
                    reader.Skip();
                    continue;
                }
                if (found is not null || reader.TokenType != JsonTokenType.StartArray) return null;
                int arrayStart = offset + (int)reader.TokenStartIndex;
                var spans = new List<(int Start, int End, string Id)>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String) return null;
                    spans.Add((offset + (int)reader.TokenStartIndex, offset + (int)reader.BytesConsumed, reader.GetString()!));
                }
                int arrayEnd = offset + (int)reader.TokenStartIndex; // the ']'
                string Text(int start, int end) => Encoding.UTF8.GetString(content, start, end - start);
                string leading = spans.Count == 0 ? "" : Text(arrayStart + 1, spans[0].Start);
                string trailing = spans.Count == 0 ? Text(arrayStart + 1, arrayEnd) : Text(spans[^1].End, arrayEnd);
                string separator = spans.Count > 1 ? Text(spans[0].End, spans[1].Start) : ",";
                // A pretty-printed list uses one separator everywhere; anything else is not rewritten by text.
                for (int index = 1; index < spans.Count; index++)
                {
                    if (Text(spans[index - 1].End, spans[index].Start) != separator) return null;
                }
                found = new PackList(arrayStart, arrayEnd,
                    spans.Select(span => (span.Id, Text(span.Start, span.End))).ToList(), separator, leading, trailing);
            }
            return found;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static byte[] Write(byte[] content, PackList list, IReadOnlyList<(string Id, string Raw)> entries)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        if (entries.Count == 0)
        {
            builder.Append(list.Entries.Count == 0 ? list.Trailing : "");
        }
        else
        {
            builder.Append(list.Entries.Count == 0 ? "" : list.Leading);
            builder.Append(string.Join(list.Separator, entries.Select(entry => entry.Raw)));
            builder.Append(list.Entries.Count == 0 ? "" : list.Trailing);
        }
        builder.Append(']');
        byte[] middle = Encoding.UTF8.GetBytes(builder.ToString());
        var result = new byte[list.ArrayStart + middle.Length + (content.Length - list.ArrayEnd - 1)];
        Buffer.BlockCopy(content, 0, result, 0, list.ArrayStart);
        Buffer.BlockCopy(middle, 0, result, list.ArrayStart, middle.Length);
        Buffer.BlockCopy(content, list.ArrayEnd + 1, result, list.ArrayStart + middle.Length, content.Length - list.ArrayEnd - 1);
        return result;
    }

    public static string Encode(string id) => JsonSerializer.Serialize(id, IdEncoding);
}
