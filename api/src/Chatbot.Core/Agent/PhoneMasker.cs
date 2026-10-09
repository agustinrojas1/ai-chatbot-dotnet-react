using System.Text.RegularExpressions;

namespace Chatbot.Core.Agent;

/// <summary>Masks phone numbers for the public trace (SPEC 7.5): <c>+54 11 5555-4444</c> becomes <c>+54 11 ****-4444</c>.</summary>
public static partial class PhoneMasker
{
    /// <summary>
    /// Keeps the last 4 digits and masks the digit group right before them. Everything else (country code, area code,
    /// separators) is kept. A number written as one unbroken run of 9+ digits keeps its first 2 digits instead.
    /// </summary>
    public static string Mask(string phone)
    {
        var chars = phone.ToCharArray();
        int totalDigits = 0;
        foreach (var c in chars)
        {
            if (char.IsAsciiDigit(c))
            {
                totalDigits++;
            }
        }

        if (totalDigits <= 4)
        {
            // Too short to be a real phone: mask it all rather than leak it.
            for (int k = 0; k < chars.Length; k++)
            {
                if (char.IsAsciiDigit(chars[k]))
                {
                    chars[k] = '*';
                }
            }

            return new string(chars);
        }

        // Walk backwards: skip the last 4 digits (kept).
        int i = chars.Length - 1;
        for (int kept = 0; kept < 4; i--)
        {
            if (char.IsAsciiDigit(chars[i]))
            {
                kept++;
            }
        }

        // Next digit group to mask. If a digit follows directly, the last 4 digits sat inside one unbroken run.
        bool sameRun = i >= 0 && char.IsAsciiDigit(chars[i]);
        while (i >= 0 && !char.IsAsciiDigit(chars[i]))
        {
            i--;
        }

        int runEnd = i;
        while (i >= 0 && char.IsAsciiDigit(chars[i]))
        {
            i--;
        }

        int runStart = i + 1;
        int keepHead = sameRun && (runEnd - runStart + 1) + 4 >= 9 ? 2 : 0;
        for (int k = runStart + keepHead; k <= runEnd; k++)
        {
            chars[k] = '*';
        }

        return new string(chars);
    }

    /// <summary>Masks every phone-like number inside free text or serialized JSON.</summary>
    public static string MaskInText(string text) =>
        PhoneLike().Replace(text, m => IsPhone(m.ValueSpan) ? Mask(m.Value) : m.Value);

    // Candidate: optional +, digits separated by spaces, dashes or parentheses. Dots and slashes are not separators,
    // so prices ("1.500.000") and decimals are not candidates. IsPhone applies the remaining rules.
    [GeneratedRegex(@"(?<![\w-])\+?\(?\d[\d ()\-]{5,}\d(?![\w-])")]
    private static partial Regex PhoneLike();

    private static bool IsPhone(ReadOnlySpan<char> candidate)
    {
        int digits = 0, lastGroup = 0;
        bool plus = candidate[0] == '+', separated = false;
        foreach (var c in candidate)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
                lastGroup++;
            }
            else if (c != '+')
            {
                separated = true;
                lastGroup = 0;
            }
        }

        if (digits is < 7 or > 15)
        {
            return false;
        }

        if (plus)
        {
            return true;
        }

        if (!separated)
        {
            return digits >= 9; // a bare 7-8 digit number is more likely a price or an id
        }

        // Separated without "+": needs a phone-like tail ("5555-4444", "555-1234") and must not be a date (2026-10-09, 09-10-2026).
        return lastGroup >= 4 && !IsDate(candidate);
    }

    private static bool IsDate(ReadOnlySpan<char> s)
    {
        int dashes = 0;
        foreach (var c in s)
        {
            if (c == '-')
            {
                dashes++;
            }
            else if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return dashes == 2 && s.Length <= 10;
    }
}
