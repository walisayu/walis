using System.Text;

namespace Walis.Core.Actions;

/// <summary>
///     A minimal renderer for action templates, supporting only three constructs:
///     <list type="bullet">
///         <item><c>{{name}}</c>: the parameter value safely wrapped in single quotes.</item>
///         <item><c>{{{name}}}</c>: inserted verbatim, for the command itself or flags.</item>
///         <item><c>{{#if name}}…{{/if}}</c> and <c>{{#unless name}}…{{/unless}}</c>: emit the block depending on whether the parameter has a value.</item>
///     </list>
///     Loops and expressions are deliberately absent so action definitions stay pure data that is easy to review.
/// </summary>
public static class ActionTemplate
{
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var expanded = RenderConditionals(template, values);
        return RenderPlaceholders(expanded, values);
    }

    /// <summary>
    ///     Wraps a value as a single-quoted string that is safe to place in a shell. When the value contains <c>${{ steps.x.y }}</c> references,
    ///     the referenced parts become the run-time expansion <c>"${WALIS_STEP_x_y}"</c> while the remaining text stays a single-quoted literal.
    /// </summary>
    public static string Quote(string? value)
    {
        if (value is null) return "''";

        return StepExpressions.Contains(value) ? StepExpressions.Quote(value) : QuoteLiteral(value);
    }

    /// <summary>Plain single-quoting that ignores references.</summary>
    public static string QuoteLiteral(string value)
    {
        return $"'{value.Replace("'", "'\\''")}'";
    }

    public static bool IsTruthy(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
               value != "0";
    }

    private static string RenderConditionals(string template, IReadOnlyDictionary<string, string?> values)
    {
        var builder = new StringBuilder();
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf("{{#", index, StringComparison.Ordinal);

            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);

            var headerEnd = template.IndexOf("}}", open, StringComparison.Ordinal);

            if (headerEnd < 0)
            {
                builder.Append(template, open, template.Length - open);
                break;
            }

            var header = template[(open + 3)..headerEnd].Trim();
            var negate = header.StartsWith("unless ", StringComparison.Ordinal);
            var name = header.StartsWith("if ", StringComparison.Ordinal)
                ? header[3..].Trim()
                : negate
                    ? header[7..].Trim()
                    : string.Empty;

            if (name.Length == 0)
            {
                builder.Append(template, open, headerEnd + 2 - open);
                index = headerEnd + 2;
                continue;
            }

            var closeTag = negate ? "{{/unless}}" : "{{/if}}";
            var close = FindClose(template, headerEnd + 2, closeTag);

            if (close < 0)
            {
                index = headerEnd + 2;
                continue;
            }

            var body = template[(headerEnd + 2)..close];
            var truthy = IsTruthy(values.GetValueOrDefault(name));

            if (truthy != negate) builder.Append(RenderConditionals(body, values));

            index = close + closeTag.Length;
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Finds the end tag that belongs to this conditional block. Blocks in between count towards the depth,
    ///     so <c>{{#if a}}…{{#if b}}…{{/if}}…{{/if}}</c> does not stop at the inner end tag.
    /// </summary>
    private static int FindClose(string template, int searchFrom, string closeTag)
    {
        var depth = 1;
        var index = searchFrom;

        while (index < template.Length)
        {
            var open = template.IndexOf("{{#", index, StringComparison.Ordinal);
            var close = template.IndexOf("{{/", index, StringComparison.Ordinal);

            if (close < 0) return -1;

            if (open >= 0 && open < close)
            {
                depth++;
                index = open + 3;
                continue;
            }

            depth--;

            // Back at depth zero the tags must match; otherwise the block is unterminated and stays as written.
            if (depth == 0)
                return string.CompareOrdinal(template, close, closeTag, 0, closeTag.Length) == 0 ? close : -1;

            index = close + 3;
        }

        return -1;
    }

    private static string RenderPlaceholders(string template, IReadOnlyDictionary<string, string?> values)
    {
        var builder = new StringBuilder();
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf("{{", index, StringComparison.Ordinal);

            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);

            var raw = template.Length > open + 2 && template[open + 2] == '{';
            var closeTag = raw ? "}}}" : "}}";
            var close = template.IndexOf(closeTag, open + (raw ? 3 : 2), StringComparison.Ordinal);

            if (close < 0)
            {
                builder.Append(template, open, template.Length - open);
                break;
            }

            var name = template[(open + (raw ? 3 : 2))..close].Trim();

            // Conditional tags were handled in the previous pass; anything left here is unterminated and is kept
            // verbatim, because quoting it would turn a stray {{/if}} into an empty argument.
            if (name.StartsWith('#') || name.StartsWith('/'))
            {
                builder.Append(template, open, close + closeTag.Length - open);
                index = close + closeTag.Length;
                continue;
            }

            var value = values.GetValueOrDefault(name);

            builder.Append(raw ? StepExpressions.ExpandRaw(value ?? string.Empty) : Quote(value));
            index = close + closeTag.Length;
        }

        return builder.ToString();
    }
}