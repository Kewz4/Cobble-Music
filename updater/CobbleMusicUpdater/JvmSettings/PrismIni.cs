using System.Text;

namespace CobbleMusicUpdater;

/// <summary>The file is valid for Prism but uses a construct this reader does not model, so no decision may be based on it.</summary>
internal sealed class PrismIniUnsupportedException : Exception
{
    public PrismIniUnsupportedException(string message) : base(message) { }
}

/// <summary>One key line of the [General] section.</summary>
internal sealed record PrismIniEntry(int LineIndex, string Key, string RawValue);

/// <summary>A decoded value as QSettings would return it: a string, a string list (unquoted comma), or an @-typed value.</summary>
internal sealed record PrismIniValue(string Text, bool IsList, bool IsTyped);

/// <summary>
/// A Prism instance.cfg / prismlauncher.cfg (QSettings IniFormat, written by Prism's INIFile::saveFile) held as its original
/// lines, so a parse followed by a serialize gives back the exact bytes, and an edit changes only whole lines of our keys.
/// Values are decoded with a port of qsettings.cpp iniUnescapedStringList and encoded with a port of iniEscapedString.
/// Anything Qt would read differently from a simple one-line key=value (multi-line quoted values, backslash line
/// continuations, inline ';' comments, lines Qt rejects, mixed line endings, duplicate keys) makes the file unsupported:
/// the caller then takes no action instead of guessing.
/// </summary>
internal sealed class PrismIniDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly char[] IniSpaces = [' ', '\t'];

    private readonly List<string> _lines;

    private PrismIniDocument(bool hasBom, string lineEnding, List<string> lines, bool endsWithLineEnding)
    {
        HasBom = hasBom;
        LineEnding = lineEnding;
        _lines = lines;
        EndsWithLineEnding = endsWithLineEnding;
        IndexGeneralSection();
    }

    public bool HasBom { get; }
    public string LineEnding { get; }
    public bool EndsWithLineEnding { get; }
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Index of the "[General]" header line, or -1 when the keys start at the top of the file with no header.</summary>
    public int GeneralHeaderIndex { get; private set; }

    /// <summary>Exclusive end of the [General] section (the next section header, or the line count).</summary>
    public int GeneralEndIndex { get; private set; }

    public IReadOnlyList<PrismIniEntry> GeneralEntries { get; private set; } = [];

    public static PrismIniDocument Parse(byte[] bytes)
    {
        bool hasBom = bytes.AsSpan().StartsWith(Bom);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            throw new PrismIniUnsupportedException("The file is not valid UTF-8.");
        }

        // Qt writes CRLF on Windows (qsettings.cpp writeIniFile). A file that mixes endings was not written by one save, so
        // re-emitting it with a single ending would move bytes we did not intend to touch.
        string lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string withoutEndings = lineEnding == "\r\n" ? text.Replace("\r\n", string.Empty, StringComparison.Ordinal) : text;
        if (lineEnding == "\r\n" ? withoutEndings.IndexOfAny(['\r', '\n']) >= 0 : text.Contains('\r'))
        {
            throw new PrismIniUnsupportedException("The file mixes line endings.");
        }

        bool endsWithLineEnding = text.EndsWith(lineEnding, StringComparison.Ordinal);
        string body = endsWithLineEnding ? text[..^lineEnding.Length] : text;
        List<string> lines = text.Length == 0 ? [] : [.. body.Split(lineEnding)];
        return new PrismIniDocument(hasBom, lineEnding, lines, endsWithLineEnding);
    }

    public byte[] Serialize()
    {
        var builder = new StringBuilder();
        for (int index = 0; index < _lines.Count; index++)
        {
            builder.Append(_lines[index]);
            if (index < _lines.Count - 1 || EndsWithLineEnding)
            {
                builder.Append(LineEnding);
            }
        }
        byte[] content = StrictUtf8.GetBytes(builder.ToString());
        return HasBom ? [.. Bom, .. content] : content;
    }

    /// <summary>The entry for a [General] key (case-insensitive, like QSettings IniFormat on Windows), or null.</summary>
    public PrismIniEntry? FindGeneral(string key) =>
        GeneralEntries.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The decoded string value of a [General] key; null when absent. Lists and @-typed values are unsupported.</summary>
    public string? GetGeneralString(string key)
    {
        PrismIniEntry? entry = FindGeneral(key);
        if (entry is null)
        {
            return null;
        }
        PrismIniValue value = PrismIni.DecodeValue(entry.RawValue);
        if (value.IsList || value.IsTyped)
        {
            throw new PrismIniUnsupportedException($"{key} holds a list or typed value.");
        }
        return value.Text;
    }

    /// <summary>
    /// A new document where each key's line is replaced in place (same key spelling) or, when absent, inserted after the last
    /// non-blank line of [General] (before the blank line that ends the section). Every other line is carried over unchanged.
    /// </summary>
    public PrismIniDocument WithGeneralValues(IReadOnlyList<KeyValuePair<string, string>> encodedValues, out int insertedCount)
    {
        var lines = new List<string>(_lines);
        var inserts = new List<string>();
        foreach ((string key, string encoded) in encodedValues)
        {
            PrismIniEntry? entry = FindGeneral(key);
            if (entry is null)
            {
                inserts.Add(key + "=" + encoded);
            }
            else
            {
                lines[entry.LineIndex] = entry.Key + "=" + encoded;
            }
        }
        int insertAt = GeneralEndIndex;
        while (insertAt > GeneralHeaderIndex + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
        {
            insertAt--;
        }
        lines.InsertRange(insertAt, inserts);
        insertedCount = inserts.Count;
        return new PrismIniDocument(HasBom, LineEnding, lines, EndsWithLineEnding || _lines.Count == 0);
    }

    private void IndexGeneralSection()
    {
        // Keys before any header belong to [General], exactly as in QSettings.
        GeneralHeaderIndex = -1;
        int generalEnd = -1;
        bool currentIsGeneral = true;
        bool generalHeaderSeen = false;
        var entries = new List<PrismIniEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < _lines.Count; index++)
        {
            string line = _lines[index];
            string trimmed = line.TrimStart(IniSpaces);
            if (trimmed.Length == 0 || trimmed[0] == ';')
            {
                continue;
            }
            if (trimmed[0] == '[')
            {
                int close = trimmed.IndexOf(']');
                string name = (close < 0 ? trimmed[1..] : trimmed[1..close]).Trim();
                if (string.Equals(name, "general", StringComparison.OrdinalIgnoreCase))
                {
                    if (generalHeaderSeen || entries.Count > 0)
                    {
                        // Qt merges repeated sections; modelling that merge is not worth it, and Prism never writes it.
                        throw new PrismIniUnsupportedException("[General] appears more than once.");
                    }
                    generalHeaderSeen = true;
                    GeneralHeaderIndex = index;
                    generalEnd = -1;
                    currentIsGeneral = true;
                }
                else
                {
                    if (currentIsGeneral && generalEnd < 0)
                    {
                        generalEnd = index;
                    }
                    currentIsGeneral = false;
                }
                continue;
            }
            ScanLine(line, out int equalsPos, out bool unterminatedQuote, out bool continuation, out bool inlineComment);
            if (unterminatedQuote || continuation)
            {
                throw new PrismIniUnsupportedException($"Line {index + 1} continues onto the next line.");
            }
            if (equalsPos < 0)
            {
                throw new PrismIniUnsupportedException($"Line {index + 1} is not a key=value line.");
            }
            if (!currentIsGeneral)
            {
                continue;
            }
            if (inlineComment)
            {
                throw new PrismIniUnsupportedException($"Line {index + 1} has an inline ';' comment.");
            }
            string rawKey = line[..equalsPos].Trim(IniSpaces);
            if (!keys.Add(PrismIni.UnescapeKey(rawKey)))
            {
                throw new PrismIniUnsupportedException($"[General] has the key {rawKey} more than once.");
            }
            entries.Add(new PrismIniEntry(index, rawKey, line[(equalsPos + 1)..]));
        }
        if (!generalHeaderSeen && entries.Count == 0)
        {
            throw new PrismIniUnsupportedException("The file has no [General] section.");
        }
        GeneralEndIndex = generalEnd < 0 ? _lines.Count : generalEnd;
        GeneralEntries = entries;
    }

    /// <summary>The part of qsettings.cpp readIniLine that matters for one physical line: '=' / '"' / '\' / ';'.</summary>
    private static void ScanLine(string line, out int equalsPos, out bool unterminatedQuote, out bool continuation, out bool inlineComment)
    {
        equalsPos = -1;
        inlineComment = false;
        continuation = false;
        bool inQuotes = false;
        for (int index = 0; index < line.Length; index++)
        {
            char ch = line[index];
            if (ch == '=')
            {
                if (!inQuotes && equalsPos == -1)
                {
                    equalsPos = index;
                }
            }
            else if (ch == '\\')
            {
                if (index + 1 >= line.Length)
                {
                    continuation = true;
                }
                index++;
            }
            else if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ';' && !inQuotes)
            {
                inlineComment = true;
                break;
            }
        }
        unterminatedQuote = inQuotes;
    }
}

/// <summary>Ports of the QSettings INI value and key codecs (qtbase 6.8 qsettings.cpp).</summary>
internal static class PrismIni
{
    /// <summary>
    /// The text QSettings writes for a QString value: variantToString (a leading '@' is doubled) then iniEscapedString
    /// (escapes, UTF-8, and double quotes when the value holds ';' ',' '=' or starts/ends with a space).
    /// </summary>
    public static string EncodeString(string value)
    {
        if (value.Contains('\0'))
        {
            // Qt would write @String(...); Prism never stores NUL in these keys.
            throw new ArgumentException("NUL cannot be written to a Prism setting.", nameof(value));
        }
        if (value.StartsWith('@'))
        {
            value = "@" + value;
        }
        return EscapeString(value);
    }

    public static string EncodeBool(bool value) => value ? "true" : "false";

    public static string EncodeInt(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Port of QSettingsPrivate::iniEscapedString (the result is text; the file encoding makes it UTF-8).</summary>
    public static string EscapeString(string str)
    {
        bool needsQuotes = false;
        bool escapeNextIfDigit = false;
        var result = new StringBuilder(str.Length * 3 / 2);
        foreach (char qch in str)
        {
            uint ch = qch;
            if (ch == ';' || ch == ',' || ch == '=')
            {
                needsQuotes = true;
            }
            if (escapeNextIfDigit && Uri.IsHexDigit(qch))
            {
                result.Append("\\x").Append(ch.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }
            escapeNextIfDigit = false;
            switch (qch)
            {
                case '\0':
                    result.Append("\\0");
                    escapeNextIfDigit = true;
                    break;
                case '\a':
                    result.Append("\\a");
                    break;
                case '\b':
                    result.Append("\\b");
                    break;
                case '\f':
                    result.Append("\\f");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\r':
                    result.Append("\\r");
                    break;
                case '\t':
                    result.Append("\\t");
                    break;
                case '\v':
                    result.Append("\\v");
                    break;
                case '"':
                case '\\':
                    result.Append('\\').Append(qch);
                    break;
                default:
                    if (ch <= 0x1F)
                    {
                        result.Append("\\x").Append(ch.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
                        escapeNextIfDigit = true;
                    }
                    else
                    {
                        result.Append(qch);
                    }
                    break;
            }
        }
        if (needsQuotes || (result.Length > 0 && (result[0] == ' ' || result[^1] == ' ')))
        {
            result.Insert(0, '"').Append('"');
        }
        return result.ToString();
    }

    /// <summary>
    /// Port of QSettingsPrivate::iniUnescapedStringList followed by stringToVariant's '@' handling: returns the string, or
    /// flags a list (unquoted comma) or an @-typed value (@ByteArray, @Variant, ...).
    /// </summary>
    public static PrismIniValue DecodeValue(string str)
    {
        var stringResult = new StringBuilder();
        var stringListResult = new List<string>();
        bool isStringList = UnescapeStringList(str, stringResult, stringListResult);
        if (isStringList)
        {
            return new PrismIniValue(string.Join(",", stringListResult), IsList: true, IsTyped: false);
        }
        string text = stringResult.ToString();
        if (text.StartsWith('@'))
        {
            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                return new PrismIniValue(text[1..], IsList: false, IsTyped: false);
            }
            if (text.EndsWith(')') && (text.StartsWith("@ByteArray(", StringComparison.Ordinal)
                || text.StartsWith("@String(", StringComparison.Ordinal)
                || text.StartsWith("@Variant(", StringComparison.Ordinal)
                || text.StartsWith("@DateTime(", StringComparison.Ordinal)
                || text.StartsWith("@Rect(", StringComparison.Ordinal)
                || text.StartsWith("@Size(", StringComparison.Ordinal)
                || text.StartsWith("@Point(", StringComparison.Ordinal))
                || text == "@Invalid()")
            {
                return new PrismIniValue(text, IsList: false, IsTyped: true);
            }
        }
        return new PrismIniValue(text, IsList: false, IsTyped: false);
    }

    private static bool UnescapeStringList(string str, StringBuilder stringResult, List<string> stringListResult)
    {
        bool isStringList = false;
        bool inQuotedString = false;
        bool currentValueIsQuoted = false;
        ushort escapeVal = 0;
        int i = 0;
        char ch;
        int chopLimit;

    StSkipSpaces:
        while (i < str.Length && ((ch = str[i]) == ' ' || ch == '\t'))
        {
            ++i;
        }

    StNormal:
        chopLimit = stringResult.Length;
        while (i < str.Length)
        {
            switch (str[i])
            {
                case '\\':
                    ++i;
                    if (i >= str.Length)
                    {
                        goto End;
                    }
                    ch = str[i++];
                    char? mapped = ch switch
                    {
                        'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v',
                        '"' => '"', '?' => '?', '\'' => '\'', '\\' => '\\',
                        _ => null
                    };
                    if (mapped is char mappedChar)
                    {
                        stringResult.Append(mappedChar);
                        goto StNormal;
                    }
                    if (ch == 'x')
                    {
                        escapeVal = 0;
                        if (i >= str.Length)
                        {
                            goto End;
                        }
                        ch = str[i];
                        if (Uri.IsHexDigit(ch))
                        {
                            goto StHexEscape;
                        }
                    }
                    else if (ch is >= '0' and <= '7')
                    {
                        escapeVal = (ushort)(ch - '0');
                        goto StOctEscape;
                    }
                    else if (ch == '\n' || ch == '\r')
                    {
                        if (i < str.Length)
                        {
                            char ch2 = str[i];
                            if ((ch2 == '\n' || ch2 == '\r') && ch2 != ch)
                            {
                                ++i;
                            }
                        }
                    }
                    chopLimit = stringResult.Length;
                    break;
                case '"':
                    ++i;
                    currentValueIsQuoted = true;
                    inQuotedString = !inQuotedString;
                    if (!inQuotedString)
                    {
                        goto StSkipSpaces;
                    }
                    break;
                case ',' when !inQuotedString:
                    if (!currentValueIsQuoted)
                    {
                        ChopTrailingSpaces(stringResult, chopLimit);
                    }
                    if (!isStringList)
                    {
                        isStringList = true;
                        stringListResult.Clear();
                    }
                    stringListResult.Add(stringResult.ToString());
                    stringResult.Clear();
                    currentValueIsQuoted = false;
                    ++i;
                    goto StSkipSpaces;
                default:
                {
                    int j = i + 1;
                    while (j < str.Length)
                    {
                        ch = str[j];
                        if (ch == '\\' || ch == '"' || ch == ',')
                        {
                            break;
                        }
                        ++j;
                    }
                    stringResult.Append(str, i, j - i);
                    i = j;
                    break;
                }
            }
        }
        if (!currentValueIsQuoted)
        {
            ChopTrailingSpaces(stringResult, chopLimit);
        }
        goto End;

    StHexEscape:
        if (i >= str.Length)
        {
            stringResult.Append((char)escapeVal);
            goto End;
        }
        ch = str[i];
        if (Uri.IsHexDigit(ch))
        {
            escapeVal = unchecked((ushort)((escapeVal << 4) + Uri.FromHex(ch)));
            ++i;
            goto StHexEscape;
        }
        stringResult.Append((char)escapeVal);
        goto StNormal;

    StOctEscape:
        if (i >= str.Length)
        {
            stringResult.Append((char)escapeVal);
            goto End;
        }
        ch = str[i];
        if (ch is >= '0' and <= '7')
        {
            escapeVal = unchecked((ushort)((escapeVal << 3) + (ch - '0')));
            ++i;
            goto StOctEscape;
        }
        stringResult.Append((char)escapeVal);
        goto StNormal;

    End:
        if (isStringList)
        {
            stringListResult.Add(stringResult.ToString());
        }
        return isStringList;
    }

    private static void ChopTrailingSpaces(StringBuilder str, int limit)
    {
        int n = str.Length - 1;
        while (n >= limit && (str[n] == ' ' || str[n] == '\t'))
        {
            str.Length = n--;
        }
    }

    /// <summary>Port of QSettingsPrivate::iniUnescapedKey: '\' means '/', %XX and %UXXXX are character codes.</summary>
    public static string UnescapeKey(string key)
    {
        var result = new StringBuilder(key.Length);
        int i = 0;
        while (i < key.Length)
        {
            char ch = key[i];
            if (ch == '\\')
            {
                result.Append('/');
                ++i;
                continue;
            }
            if (ch != '%' || i == key.Length - 1)
            {
                result.Append(ch);
                ++i;
                continue;
            }
            int numDigits = 2;
            int firstDigitPos = i + 1;
            if (key[i + 1] == 'U')
            {
                ++firstDigitPos;
                numDigits = 4;
            }
            if (firstDigitPos + numDigits > key.Length
                || !ushort.TryParse(key.AsSpan(firstDigitPos, numDigits), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out ushort code))
            {
                result.Append('%');
                ++i;
                continue;
            }
            result.Append((char)code);
            i = firstDigitPos + numDigits;
        }
        return result.ToString();
    }

    /// <summary>QVariant(QString).toBool(): false only for "", "0" and "false" (any case).</summary>
    public static bool ToBool(string? value) =>
        !(string.IsNullOrEmpty(value) || value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));

    /// <summary>A whole decimal int as Prism's toInt() would read it, or null for anything we do not model exactly.</summary>
    public static int? ToStrictInt(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 9 || !value.All(char.IsAsciiDigit))
        {
            return null;
        }
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
