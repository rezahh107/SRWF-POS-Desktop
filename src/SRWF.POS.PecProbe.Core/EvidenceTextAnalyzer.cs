using System.Text;

namespace SRWF.POS.PecProbe.Core;

public static class EvidenceTextAnalyzer
{
    public static TextEvidence Analyze(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return new("EMPTY", false, "NONE", false, true, string.Empty, [], []);

        var (encoding, encodingName, bomLength, safe) = DetectEncoding(bytes);
        if (!safe || encoding is null)
            return new(encodingName, bomLength > 0, DetectNewlineBytes(bytes), EndsWithNewline(bytes), false, null, [], []);

        string text;
        try
        {
            text = encoding.GetString(bytes[bomLength..]);
        }
        catch
        {
            return new(encodingName, bomLength > 0, DetectNewlineBytes(bytes), EndsWithNewline(bytes), false, null, [], []);
        }

        var fields = new List<KeyValuePair<string, string>>();
        var fieldNames = new List<string>();
        foreach (var line in SplitLogicalLines(text))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx];
            var value = line[(idx + 1)..];
            fields.Add(new(key, value));
            fieldNames.Add(key);
        }

        return new(
            encodingName,
            bomLength > 0,
            DetectNewlineText(text),
            text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n') || text.EndsWith('\r'),
            true,
            text,
            fieldNames,
            fields);
    }

    public static byte[] EncodeLike(TextEvidence evidence, string text)
    {
        Encoding encoding = evidence.ProbableEncoding switch
        {
            "UTF-8" or "ASCII" => new UTF8Encoding(false, true),
            "UTF-16LE" => Encoding.Unicode,
            "UTF-16BE" => Encoding.BigEndianUnicode,
            _ => throw new InvalidOperationException($"Encoding {evidence.ProbableEncoding} is not safely reproducible.")
        };

        var payload = encoding.GetBytes(text);
        if (!evidence.BomPresent) return payload;
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0 && evidence.ProbableEncoding == "UTF-8") preamble = [0xEF, 0xBB, 0xBF];
        return [.. preamble, .. payload];
    }

    private static (Encoding? Encoding, string Name, int BomLength, bool Safe) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith([0xEF, 0xBB, 0xBF])) return (new UTF8Encoding(false, true), "UTF-8", 3, true);
        if (bytes.StartsWith([0xFF, 0xFE])) return (Encoding.Unicode, "UTF-16LE", 2, true);
        if (bytes.StartsWith([0xFE, 0xFF])) return (Encoding.BigEndianUnicode, "UTF-16BE", 2, true);

        if (bytes.ToArray().All(b => b < 0x80)) return (Encoding.ASCII, "ASCII", 0, true);

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false, true), "UTF-8", 0, true);
        }
        catch
        {
            return (null, "UNKNOWN_OR_LEGACY", 0, false);
        }
    }

    private static IReadOnlyList<string> SplitLogicalLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string DetectNewlineText(string text)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var withoutCrlf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        var lf = withoutCrlf.Contains('\n');
        var cr = withoutCrlf.Contains('\r');
        var count = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        if (count > 1) return "MIXED";
        if (crlf) return "CRLF";
        if (lf) return "LF";
        if (cr) return "CR";
        return "NONE";
    }

    private static string DetectNewlineBytes(ReadOnlySpan<byte> bytes)
    {
        var arr = bytes.ToArray();
        var crlf = false;
        var lf = false;
        var cr = false;
        for (var i = 0; i < arr.Length; i++)
        {
            if (arr[i] == 13 && i + 1 < arr.Length && arr[i + 1] == 10) { crlf = true; i++; continue; }
            if (arr[i] == 10) lf = true;
            else if (arr[i] == 13) cr = true;
        }
        var count = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        if (count > 1) return "MIXED_OR_BINARY";
        if (crlf) return "CRLF_BYTES";
        if (lf) return "LF_BYTES";
        if (cr) return "CR_BYTES";
        return "NONE_OR_BINARY";
    }

    private static bool EndsWithNewline(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 0 && (bytes[^1] == 10 || bytes[^1] == 13);
}
