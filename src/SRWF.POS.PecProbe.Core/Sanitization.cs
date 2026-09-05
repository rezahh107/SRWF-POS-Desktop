using System.Text.RegularExpressions;

namespace SRWF.POS.PecProbe.Core;

public static partial class SensitiveDataSanitizer
{
    [GeneratedRegex(@"(?<!\d)(\d{13,19})(?!\d)")]
    private static partial Regex LongDigitsRegex();

    [GeneratedRegex(@"(?<!\d)(\d{13,19})(?:=|D)(\d{4,})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Track2Regex();

    public static SanitizationResult Sanitize(string input)
    {
        var findings = new List<SanitizationFinding>();

        var result = Track2Regex().Replace(input, match =>
        {
            var tokenIndex = GetWhitespaceTokenIndex(input, match.Index);
            var placeholder = $"<TRACK2_REDACTED len={match.Length} sha256={Hashing.Sha256(System.Text.Encoding.UTF8.GetBytes(match.Value))}>";
            findings.Add(new(tokenIndex, match.Length, "DIGIT_TRACK2", Hashing.Sha256(System.Text.Encoding.UTF8.GetBytes(match.Value)), "TRACK2_LIKE", placeholder));
            return placeholder;
        });

        result = LongDigitsRegex().Replace(result, match =>
        {
            if (!IsLuhnValid(match.Value)) return match.Value;
            var tokenIndex = GetWhitespaceTokenIndex(result, match.Index);
            var hash = Hashing.Sha256(System.Text.Encoding.UTF8.GetBytes(match.Value));
            var placeholder = $"<PAN_REDACTED len={match.Length} sha256={hash}>";
            findings.Add(new(tokenIndex, match.Length, "DIGITS", hash, "LUHN_VALID_PAN_CANDIDATE", placeholder));
            return placeholder;
        });

        return new(result, findings);
    }

    private static int GetWhitespaceTokenIndex(string text, int charIndex)
    {
        var index = 0;
        var inToken = false;
        for (var i = 0; i < Math.Min(charIndex, text.Length); i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inToken = false;
            }
            else if (!inToken)
            {
                if (i > 0) index++;
                inToken = true;
            }
        }
        return index;
    }

    public static bool IsLuhnValid(string digits)
    {
        if (digits.Length is < 13 or > 19 || digits.Any(c => !char.IsDigit(c))) return false;
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}
