using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Wands;

// Only a decoded reply string behind an explicit readyToFinish:false may reach
// provisional speech. The terminal flag still requires the full valid response.
internal sealed class MobileWandReplyStream
{
    private readonly StringBuilder json = new();
    private int emitted;
    private int emittedClauses;
    private bool held;
    internal string Json => json.ToString();
    internal string Text { get; private set; } = "";

    internal string[] Append(string delta)
    {
        if (delta.Length > 8192 || json.Length + delta.Length > 8192) { throw Invalid(); }
        json.Append(delta);
        var bytes = Encoding.UTF8.GetBytes(json.ToString());
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, new JsonReaderState(new() { MaxDepth = 4 }));
        bool? ready = null;
        var replyStart = -1;
        string? property = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                property = reader.GetString();
                if (property is not ("readyToFinish" or "reply") || !names.Add(property)) { throw Invalid(); }
                if (property == "reply")
                {
                    // PropertyName consumption includes its colon. The token
                    // span lets the prefix decoder start just after the name.
                    replyStart = (int)reader.TokenStartIndex + reader.ValueSpan.Length + 2;
                }
            }
            else if (property == "readyToFinish" && reader.CurrentDepth == 1)
            {
                if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False)) { throw Invalid(); }
                ready = reader.GetBoolean();
                property = null;
            }
            else if (property == "reply" && reader.CurrentDepth == 1)
            {
                if (reader.TokenType != JsonTokenType.String) { throw Invalid(); }
                property = null;
            }
        }
        if (ready != false || replyStart < 0) { return []; }
        var text = DecodeReplyPrefix(bytes, replyStart);
        if (text.Length > 600 || text.Any(character => char.IsControl(character))) { throw Invalid(); }
        if (held) { return []; }
        if (!OrdinaryClause(text)) { held = true; return []; }
        Text = text;
        var clauses = new List<string>();
        for (var index = emitted; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '?' or '!' or ';') || index + 1 - emitted < 8) { continue; }
            var clause = text[emitted..(index + 1)];
            if (emittedClauses == 8) { break; }
            clauses.Add(clause);
            emittedClauses++;
            emitted = index + 1;
        }
        return clauses.ToArray();
    }

    private static bool OrdinaryClause(string clause)
    {
        var normalized = new string(clause.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        // A false flag is the structural gate. Hold ambiguous manufacture/result
        // wording too, rather than trusting an unfinished model's prose as state.
        return !new[] { "vyrob", "vyrab", "hotov", "uloz", "dokonc", "prijat", "prijal",
            "manufactur", "saved", "complete", "finished" }.Any(normalized.Contains);
    }

    private static string DecodeReplyPrefix(byte[] bytes, int propertyEnd)
    {
        var start = propertyEnd;
        while (start < bytes.Length && char.IsWhiteSpace((char)bytes[start])) { start++; }
        if (start == bytes.Length) { return ""; }
        if (bytes[start++] != ':') { throw Invalid(); }
        while (start < bytes.Length && char.IsWhiteSpace((char)bytes[start])) { start++; }
        if (start == bytes.Length) { return ""; }
        if (bytes[start] != '"') { throw Invalid(); }
        var index = start + 1;
        while (index < bytes.Length)
        {
            var value = bytes[index];
            if (value == '"') { break; }
            if (value < 0x20) { throw Invalid(); }
            if (value == '\\')
            {
                if (index + 1 == bytes.Length) { break; }
                var escape = bytes[index + 1];
                if (escape == 'u')
                {
                    if (index + 6 > bytes.Length) { break; }
                    var code = HexCode(bytes.AsSpan(index + 2, 4));
                    if (code is >= 0xD800 and <= 0xDBFF)
                    {
                        if (index + 12 > bytes.Length) { break; }
                        if (bytes[index + 6] != '\\' || bytes[index + 7] != 'u'
                            || HexCode(bytes.AsSpan(index + 8, 4)) is not (>= 0xDC00 and <= 0xDFFF)) { throw Invalid(); }
                        index += 12;
                    }
                    else
                    {
                        if (code is >= 0xDC00 and <= 0xDFFF) { throw Invalid(); }
                        index += 6;
                    }
                    continue;
                }
                if (escape is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f'
                    or (byte)'n' or (byte)'r' or (byte)'t')) { throw Invalid(); }
                index += 2;
                continue;
            }
            var status = Rune.DecodeFromUtf8(bytes.AsSpan(index), out _, out var consumed);
            if (status == OperationStatus.NeedMoreData) { break; }
            if (status != OperationStatus.Done) { throw Invalid(); }
            index += consumed;
        }
        var quoted = new byte[index - start + 1];
        bytes.AsSpan(start, index - start).CopyTo(quoted);
        quoted[^1] = (byte)'"';
        return JsonSerializer.Deserialize<string>(quoted) ?? throw Invalid();
    }

    private static int HexCode(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        foreach (var item in bytes)
        {
            var digit = item switch
            {
                >= (byte)'0' and <= (byte)'9' => item - '0',
                >= (byte)'a' and <= (byte)'f' => item - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => item - 'A' + 10,
                _ => -1
            };
            if (digit < 0) { throw Invalid(); }
            value = value * 16 + digit;
        }
        return value;
    }

    private static MobileWandQuestionAuthoringException Invalid() => new("dialogue_response_invalid", false);
}
