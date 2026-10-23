using System.Text.RegularExpressions;

namespace Walis.Core.Security;

/// <summary>
///     Sanitizes user input: strips HTML tags and control characters so stored content is not interpreted as markup elsewhere.
///     Ported from an internal shared security library and adapted to Walis naming conventions.
/// </summary>
public static partial class InputSanitizer
{
    /// <summary>
    ///     Strips HTML tags, control characters, Unicode format characters (bidirectional overrides, zero-width marks) and
    ///     line/paragraph separators, trims whitespace and truncates to the limit; returns null when the result is empty.
    /// </summary>
    public static string? Sanitize(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var result = HtmlTag().Replace(text, string.Empty);
        result = ControlCharacter().Replace(result, string.Empty).Trim();

        if (result.Length > maxLength) result = SafeTruncate(result, maxLength);

        return result.Length == 0 ? null : result;
    }

    /// <summary>
    ///     Truncates to the given length. When the cut lands in the middle of a UTF-16 surrogate pair, the leading half is dropped too;
    ///     a lone surrogate would blow up later JSON serialization or database writes.
    /// </summary>
    public static string SafeTruncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;

        var length = maxLength;

        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;

        return text[..length];
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.None, 100)]
    private static partial Regex HtmlTag();

    [GeneratedRegex("[\\x00-\\x1F\\x7F\\p{Cf}\\p{Zl}\\p{Zp}]", RegexOptions.None, 100)]
    private static partial Regex ControlCharacter();
}