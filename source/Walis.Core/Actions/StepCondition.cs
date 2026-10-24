using System.Text;
using System.Text.RegularExpressions;

namespace Walis.Core.Actions;

/// <summary>
///     A step's run-condition expression, evaluated at run time against earlier steps' outputs and environment variables to decide whether the step runs:
///     <code>steps.check.changed == 'true' &amp;&amp; env.BRANCH != 'main'</code>.
///     The grammar is only: string literals (single or double quotes), <c>steps.&lt;key&gt;.&lt;name&gt;</c>, <c>env.&lt;name&gt;</c>,
///     <c>==</c>, <c>!=</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, parentheses,
///     and the functions <c>contains(a, b)</c>, <c>startsWith(a, b)</c>, <c>endsWith(a, b)</c>, <c>matches(a, 'regex')</c>, <c>empty(a)</c>.
///     Arithmetic and assignment are deliberately absent so a condition reads at a glance and can have no side effects.
/// </summary>
public static partial class StepCondition
{
    public const int MaximumLength = 2_000;

    public abstract record Node;

    private sealed record Literal(string Value) : Node;

    private sealed record StepReference(string Key, string Name) : Node;

    private sealed record EnvReference(string Name) : Node;

    private sealed record Compare(Node Left, Node Right, bool Equal) : Node;

    private sealed record Logical(Node Left, Node Right, bool And) : Node;

    private sealed record Not(Node Operand) : Node;

    private sealed record Call(string Function, IReadOnlyList<Node> Arguments) : Node;

    public sealed record ParseResult(Node? Root, string? Error)
    {
        public bool IsValid => Error is null && Root is not null;
    }

    public static ParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ParseResult(null, "條件是空的。");
        if (text.Length > MaximumLength) return new ParseResult(null, $"條件不可超過 {MaximumLength:N0} 個字元。");

        try
        {
            var parser = new Parser(text);
            var root = parser.ParseExpression();
            parser.ExpectEnd();
            return new ParseResult(root, null);
        }
        catch (FormatException exception)
        {
            return new ParseResult(null, exception.Message);
        }
    }

    /// <summary>The step outputs referenced by the condition, for the compile-time "only predecessors may be referenced" check.</summary>
    public static IEnumerable<(string Key, string Name)> StepReferences(Node root)
    {
        switch (root)
        {
            case StepReference reference:
                yield return (reference.Key, reference.Name);
                break;
            case Compare compare:
                foreach (var item in StepReferences(compare.Left)) yield return item;
                foreach (var item in StepReferences(compare.Right)) yield return item;
                break;
            case Logical logical:
                foreach (var item in StepReferences(logical.Left)) yield return item;
                foreach (var item in StepReferences(logical.Right)) yield return item;
                break;
            case Not not:
                foreach (var item in StepReferences(not.Operand)) yield return item;
                break;
            case Call call:
                foreach (var argument in call.Arguments)
                foreach (var item in StepReferences(argument))
                    yield return item;
                break;
        }
    }

    /// <summary>Evaluates against the current variable table; step outputs are read from <c>WALIS_STEP_&lt;key&gt;_&lt;name&gt;</c>, missing ones count as empty strings.</summary>
    public static bool Evaluate(Node root, IReadOnlyDictionary<string, string> variables)
    {
        return ActionTemplate.IsTruthy(Value(root, variables));
    }

    private static string Value(Node node, IReadOnlyDictionary<string, string> variables)
    {
        return node switch
        {
            Literal literal => literal.Value,
            StepReference reference => variables.GetValueOrDefault(StepExpressions.VariableName(reference.Key, reference.Name)) ?? string.Empty,
            EnvReference reference => variables.GetValueOrDefault(reference.Name) ?? string.Empty,
            Compare compare => Bool(string.Equals(Value(compare.Left, variables), Value(compare.Right, variables), StringComparison.Ordinal) == compare.Equal),
            Logical logical => logical.And
                ? Bool(ActionTemplate.IsTruthy(Value(logical.Left, variables)) && ActionTemplate.IsTruthy(Value(logical.Right, variables)))
                : Bool(ActionTemplate.IsTruthy(Value(logical.Left, variables)) || ActionTemplate.IsTruthy(Value(logical.Right, variables))),
            Not not => Bool(!ActionTemplate.IsTruthy(Value(not.Operand, variables))),
            Call call => Invoke(call, variables),
            _ => string.Empty
        };
    }

    private static string Invoke(Call call, IReadOnlyDictionary<string, string> variables)
    {
        var arguments = call.Arguments.Select(argument => Value(argument, variables)).ToList();
        return call.Function switch
        {
            "contains" => Bool(arguments[0].Contains(arguments[1], StringComparison.Ordinal)),
            "startsWith" => Bool(arguments[0].StartsWith(arguments[1], StringComparison.Ordinal)),
            "endsWith" => Bool(arguments[0].EndsWith(arguments[1], StringComparison.Ordinal)),
            "empty" => Bool(string.IsNullOrWhiteSpace(arguments[0])),
            "matches" => Bool(SafeMatch(arguments[0], arguments[1])),
            _ => string.Empty
        };
    }

    private static bool SafeMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    private static readonly Dictionary<string, int> Functions = new(StringComparer.Ordinal)
    {
        ["contains"] = 2, ["startsWith"] = 2, ["endsWith"] = 2, ["matches"] = 2, ["empty"] = 1
    };

    /// <summary>Hand-written recursive-descent parser; the grammar is too small to justify a parser generator.</summary>
    private sealed class Parser(string text)
    {
        private int _position;

        public Node ParseExpression()
        {
            return ParseOr();
        }

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_position < text.Length) throw Error($"多餘的內容「{Rest()}」。");
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (TryConsume("||")) left = new Logical(left, ParseAnd(), false);
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseUnary();
            while (TryConsume("&&")) left = new Logical(left, ParseUnary(), true);
            return left;
        }

        private Node ParseUnary()
        {
            if (TryConsume("!")) return new Not(ParseUnary());
            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParsePrimary();
            if (TryConsume("==")) return new Compare(left, ParsePrimary(), true);
            if (TryConsume("!=")) return new Compare(left, ParsePrimary(), false);
            return left;
        }

        private Node ParsePrimary()
        {
            SkipSpaces();
            if (_position >= text.Length) throw Error("條件不完整。");

            var current = text[_position];

            if (current == '(')
            {
                _position++;
                var inner = ParseOr();
                if (!TryConsume(")")) throw Error("缺少右括號。");
                return inner;
            }

            if (current is '\'' or '"') return new Literal(ReadString(current));

            var word = ReadWord();
            if (word.Length == 0) throw Error($"看不懂「{Rest()}」。");

            if (word is "true" or "false") return new Literal(word);

            if (Functions.TryGetValue(word, out var arity))
            {
                if (!TryConsume("(")) throw Error($"{word} 後面要接括號。");
                var arguments = new List<Node>();
                if (!TryConsume(")"))
                {
                    do arguments.Add(ParseOr());
                    while (TryConsume(","));
                    if (!TryConsume(")")) throw Error($"{word} 缺少右括號。");
                }

                if (arguments.Count != arity) throw Error($"{word} 需要 {arity} 個參數。");
                return new Call(word, arguments);
            }

            var stepMatch = StepPattern().Match(word);
            if (stepMatch.Success) return new StepReference(stepMatch.Groups["key"].Value, stepMatch.Groups["name"].Value);

            var envMatch = EnvPattern().Match(word);
            if (envMatch.Success) return new EnvReference(envMatch.Groups["name"].Value);

            if (word.StartsWith("steps.", StringComparison.Ordinal))
                throw Error($"「{word}」要寫成 steps.<識別字>.<名稱>。");
            if (word.StartsWith("env.", StringComparison.Ordinal))
                throw Error($"「{word}」要寫成 env.<變數名稱>。");

            throw Error($"看不懂「{word}」：值要用引號包住，引用要寫 steps.x.y 或 env.NAME。");
        }

        private string ReadString(char quote)
        {
            _position++;
            var builder = new StringBuilder();
            while (_position < text.Length)
            {
                var current = text[_position++];
                if (current == '\\' && _position < text.Length)
                {
                    builder.Append(text[_position++]);
                    continue;
                }

                if (current == quote) return builder.ToString();
                builder.Append(current);
            }

            throw Error("字串沒有結尾的引號。");
        }

        private string ReadWord()
        {
            var start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '.')) _position++;
            return text[start.._position];
        }

        private bool TryConsume(string token)
        {
            SkipSpaces();
            if (string.CompareOrdinal(text, _position, token, 0, token.Length) != 0) return false;
            _position += token.Length;
            return true;
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }

        private string Rest()
        {
            var rest = text[_position..];
            return rest.Length <= 30 ? rest : rest[..30] + "…";
        }

        private FormatException Error(string message)
        {
            return new FormatException($"第 {_position + 1} 個字元附近：{message}");
        }
    }

    [GeneratedRegex(@"^steps\.(?<key>[a-z][a-z0-9_]*)\.(?<name>[A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex StepPattern();

    [GeneratedRegex(@"^env\.(?<name>[A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex EnvPattern();
}
