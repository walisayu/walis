using System.Text.RegularExpressions;

namespace Walis.Core.Security;

/// <summary>
///     Guards user text sent to large language models. Ported from an internal shared security library.
///     Wrapping the user's words in tags and telling the model "that is data, not instructions"
///     only works when the user cannot forge those tags; otherwise a single &lt;/requirement&gt;
///     escapes the boundary and everything after it becomes instructions to the model. This keeps that boundary intact.
/// </summary>
public static class PromptGuard
{
    /// <summary>The wrapper tags that get escaped. When adding a prompt template, add the tags it uses here.</summary>
    public static readonly IReadOnlyList<string> BoundaryTags =
    [
        "requirement",
        "user-request",
        "user-code",
        "user-data",
        "build-log"
    ];

    /// <summary>Common privilege-escalation and prompt-extraction phrases in both Chinese and English. A hit is not proof of malice, just worth a record.</summary>
    public static readonly IReadOnlyList<string> SuspiciousPhrases =
    [
        "ignore previous instructions",
        "ignore the above",
        "disregard previous",
        "忽略以上指令",
        "忽略上述",
        "system prompt",
        "系統提示",
        "developer message",
        "開發者訊息",
        "hidden instruction",
        "隱藏指令",
        "internal instruction",
        "內部指令"
    ];

    /// <summary>
    ///     Sanitizes text to be inserted into a prompt: normalizes newlines, removes control characters (keeping newline and tab),
    ///     and swaps the angle brackets of wrapper tags for full-width ones so they stay visible but lose their boundary effect.
    /// </summary>
    public static string Sanitize(string? text, IReadOnlyList<string>? tags = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var normalized = new string(text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Where(character => !char.IsControl(character) || character is '\n' or '\t')
            .ToArray());

        foreach (var tag in tags ?? BoundaryTags)
            normalized = Regex.Replace(
                normalized,
                $@"<\s*(/?)\s*{Regex.Escape(tag)}\s*>",
                match => match.Groups[1].Value == "/" ? $"＜/{tag}＞" : $"＜{tag}＞",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));

        return normalized;
    }

    /// <summary>Truncates overly long input so nobody can push the system prompt out of the context with a wall of text.</summary>
    public static string Truncate(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return text.Length <= limit
            ? text
            : InputSanitizer.SafeTruncate(text, limit) + "\n…（已截斷）";
    }

    /// <summary>Looks like an attempt to extract the system prompt or ignore the rules. For auditing, not blocking: a false positive costs more than a miss.</summary>
    public static bool LooksLikeInjection(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               SuspiciousPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Truncates and sanitizes user text, then wraps it in tags. Always build prompts through here instead of concatenating strings by hand.</summary>
    public static string Wrap(string tag, string? text, int limit)
    {
        var safe = Sanitize(Truncate(text?.Trim(), limit));
        return $"<{tag}>\n{safe}\n</{tag}>";
    }

    /// <summary>
    ///     Wraps content in a Markdown code block. Triple backticks inside are replaced with characters that carry no syntax;
    ///     otherwise a piece of build output only needs to print its own fence and everything after it becomes instructions to the model.
    /// </summary>
    public static string Fence(string language, string? content, int limit)
    {
        var safe = Sanitize(Truncate(content, limit)).Replace("```", "ˋˋˋ");
        return $"```{language}\n{safe}\n```";
    }

    /// <summary>Single-line fields (job names and the like): truncated with newlines removed, so no new field can be forged.</summary>
    public static string Field(string? text, int limit = 200)
    {
        var safe = Sanitize(Truncate(text, limit)).Replace("\n", " ").Replace("\t", " ");
        return string.IsNullOrWhiteSpace(safe) ? "（未填）" : safe.Trim();
    }
}