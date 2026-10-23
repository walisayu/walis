using System.Text;

namespace Walis.Core.Jenkins;

/// <summary>
///     A parser for the Groovy subset used in Jenkinsfiles. The goal is not to understand Groovy but to pull out
///     DSL structures like <c>name(args) { body }</c>; anything else is kept verbatim as <see cref="GroovyRaw" />,
///     so the converter can warn instead of failing the whole file. Never throws.
/// </summary>
public static class GroovyParser
{
    public static IReadOnlyList<GroovyNode> Parse(string source)
    {
        var tokens = GroovyTokenizer.Tokenize(source);
        var parser = new Parser(source, tokens);
        return parser.ParseBlock(false);
    }

    private sealed class Parser(string source, IReadOnlyList<GroovyToken> tokens)
    {
        private static readonly HashSet<string> Keywords =
        [
            "if", "else", "for", "while", "try", "catch", "finally", "return", "def", "import", "switch",
            "throw", "new", "package", "class", "@Library", "break", "continue", "do", "assert"
        ];

        private int _position;

        private GroovyToken Current => tokens[Math.Min(_position, tokens.Count - 1)];

        private bool AtEnd => Current.Kind == GroovyTokenKind.End;

        private GroovyToken Peek(int offset = 1)
        {
            return tokens[Math.Min(_position + offset, tokens.Count - 1)];
        }

        public List<GroovyNode> ParseBlock(bool untilClose)
        {
            var nodes = new List<GroovyNode>();

            while (!AtEnd)
            {
                SkipSeparators();

                if (AtEnd) break;

                if (Current.Kind == GroovyTokenKind.Punct && Current.Text == "}")
                {
                    if (untilClose)
                    {
                        _position++;
                        return nodes;
                    }

                    // Extra closing brace: skip it rather than let it stall the whole file.
                    _position++;
                    continue;
                }

                var start = _position;
                try
                {
                    var node = ParseStatement();
                    if (node is not null) nodes.Add(node);
                }
                catch (ParseException)
                {
                    _position = start;
                    nodes.Add(CaptureRaw());
                }

                // Safety net: always advance, otherwise this loops forever.
                if (_position == start)
                {
                    nodes.Add(CaptureRaw());
                    if (_position == start) _position++;
                }
            }

            return nodes;
        }

        private GroovyNode? ParseStatement()
        {
            var token = Current;

            if (token.Kind == GroovyTokenKind.Punct && token.Text == "@")
                // Annotation-style markers (@Library('x') _, @NonCPS): skip the whole line.
                return CaptureRaw();

            if (token.Kind != GroovyTokenKind.Identifier) return CaptureRaw();

            if (token.Text is "import" or "package")
            {
                SkipToLineEnd();
                return null;
            }

            if (token.Text == "def")
            {
                // def x = expr -> assignment; def f() { } -> kept verbatim.
                if (Peek().Kind == GroovyTokenKind.Identifier && Peek(2).IsPunct("="))
                {
                    _position += 2;
                    var declared = Peek(-1).Text;
                    _position++;
                    var value = ParseExpression();
                    return new GroovyAssignment(token.Line, declared, value, true);
                }

                return CaptureRaw();
            }

            if (Keywords.Contains(token.Text)) return CaptureRaw();

            // Names may be a.b.c (env.FOO, steps.sh, currentBuild.result).
            var line = token.Line;
            var name = ParseDottedName();

            if (Current.IsPunct("="))
            {
                _position++;
                var value = ParseExpression();
                return new GroovyAssignment(line, name, value, false);
            }

            if (Current.IsPunct("+=") || Current.IsPunct("-=") || Current.IsPunct("++") || Current.IsPunct("--"))
            {
                _position = FindTokenIndexOfName(line);
                return CaptureRaw();
            }

            return ParseCallRest(name, line);
        }

        private int FindTokenIndexOfName(int line)
        {
            var index = _position;
            while (index > 0 && tokens[index - 1].Line == line &&
                   tokens[index - 1].Kind != GroovyTokenKind.Newline) index--;

            return index;
        }

        /// <summary>The name has been read; next come the arguments and the closure.</summary>
        private GroovyCall ParseCallRest(string name, int line)
        {
            var arguments = new List<GroovyArgument>();
            List<GroovyNode>? body = null;

            if (Current.IsPunct("("))
            {
                _position++;
                arguments = ParseArguments(")");
                Expect(")");
            }
            else if (StartsValueOnSameLine(line))
            {
                arguments = ParseCommandArguments(line);
            }

            if (Current.IsPunct("{")) body = ParseClosureBody();

            // Chained calls such as `.each { }` or `.trim()`: keep the whole thing verbatim to avoid misparsing.
            if (Current.IsPunct(".") && Current.Line == line)
            {
                var start = FindTokenIndexOfName(line);
                _position = start;
                throw new ParseException();
            }

            return new GroovyCall(line, name, arguments, body);
        }

        private bool StartsValueOnSameLine(int line)
        {
            var token = Current;
            if (token.Line != line || token.Kind is GroovyTokenKind.Newline or GroovyTokenKind.End) return false;

            return token.Kind switch
            {
                GroovyTokenKind.String or GroovyTokenKind.Number => true,
                GroovyTokenKind.Identifier => !Keywords.Contains(token.Text) ||
                                              token.Text is "true" or "false" or "null",
                GroovyTokenKind.Punct => token.Text is "[" or "-" or "!",
                _ => false
            };
        }

        /// <summary>Calls without parentheses: <c>sh 'x'</c>, <c>echo "y"</c>, <c>agent any</c>, <c>git url: 'x', branch: 'y'</c>.</summary>
        private List<GroovyArgument> ParseCommandArguments(int line)
        {
            var arguments = new List<GroovyArgument>();

            while (!AtEnd && Current.Kind != GroovyTokenKind.Newline && !Current.IsPunct(";") && !Current.IsPunct("}"))
            {
                if (Current.IsPunct("{")) break;

                arguments.Add(ParseArgument());

                if (Current.IsPunct(","))
                {
                    _position++;
                    SkipNewlines();
                    continue;
                }

                break;
            }

            return arguments;
        }

        private List<GroovyArgument> ParseArguments(string closer)
        {
            var arguments = new List<GroovyArgument>();
            SkipNewlines();

            while (!AtEnd && !Current.IsPunct(closer))
            {
                arguments.Add(ParseArgument());
                SkipNewlines();

                if (Current.IsPunct(","))
                {
                    _position++;
                    SkipNewlines();
                    continue;
                }

                if (!Current.IsPunct(closer)) throw new ParseException();
            }

            return arguments;
        }

        private GroovyArgument ParseArgument()
        {
            // key: value (the key may be an identifier or a string)
            if (Current.Kind is GroovyTokenKind.Identifier or GroovyTokenKind.String && Peek().IsPunct(":"))
            {
                var key = Current.Text;
                _position += 2;
                SkipNewlines();
                return new GroovyArgument(key, ParseExpression());
            }

            return new GroovyArgument(null, ParseExpression());
        }

        private GroovyValue ParseExpression()
        {
            var start = _position;
            var value = ParsePrimary();

            // String plus string: merge; any other operation: keep the whole thing verbatim.
            while (Current.Kind == GroovyTokenKind.Punct && IsBinaryOperator(Current.Text))
            {
                if (Current.Text == "+" && value is GroovyString left)
                {
                    _position++;
                    SkipNewlines();
                    var right = ParsePrimary();
                    if (right is GroovyString rightText)
                    {
                        value = new GroovyString(left.Text + rightText.Text,
                            left.Interpolated || rightText.Interpolated);
                        continue;
                    }
                }

                _position = start;
                return new GroovyRawValue(CaptureExpressionText());
            }

            if (Current.IsPunct("?") || Current.IsPunct("?:") || Current.IsPunct("?."))
            {
                _position = start;
                return new GroovyRawValue(CaptureExpressionText());
            }

            return value;
        }

        private static bool IsBinaryOperator(string text)
        {
            return text is "+" or "-" or "*" or "/" or "%" or "==" or "!=" or "&&" or "||" or "<" or ">" or "<=" or ">="
                or "=~" or "==~" or "<<" or ">>" or "in" or "instanceof";
        }

        private GroovyValue ParsePrimary()
        {
            var token = Current;

            switch (token.Kind)
            {
                case GroovyTokenKind.String:
                    _position++;
                    return new GroovyString(token.Text, token.Interpolated);

                case GroovyTokenKind.Number:
                    _position++;
                    return new GroovyNumber(token.Text);

                case GroovyTokenKind.Identifier:
                {
                    if (Keywords.Contains(token.Text) && token.Text is not ("true" or "false" or "null"))
                        return new GroovyRawValue(CaptureExpressionText());

                    var line = token.Line;
                    var name = ParseDottedName();

                    if (Current.IsPunct("(") || (Current.IsPunct("{") && Current.Line == line))
                    {
                        var callStart = _position;
                        try
                        {
                            var call = ParseCallRest(name, line);
                            return new GroovyCallValue(call);
                        }
                        catch (ParseException)
                        {
                            // Method chains like sh(...).trim(): the value is kept verbatim as a whole, the assignment itself still holds.
                            _position = FindTokenIndexOfName(line) < callStart ? FindTokenIndexOfName(line) : callStart;
                            return new GroovyRawValue(CaptureExpressionText());
                        }
                    }

                    if (Current.IsPunct("["))
                    {
                        // params['X'], env['X']
                        _position++;
                        var index = ParseExpression();
                        Expect("]");
                        return new GroovyIdentifier(index is GroovyString text ? $"{name}.{text.Text}" : name);
                    }

                    return new GroovyIdentifier(name);
                }

                case GroovyTokenKind.Punct when token.Text == "[":
                    return ParseListOrMap();

                case GroovyTokenKind.Punct when token.Text == "{":
                    return new GroovyClosure(ParseClosureBody());

                case GroovyTokenKind.Punct when token.Text == "(":
                {
                    _position++;
                    SkipNewlines();
                    var inner = ParseExpression();
                    SkipNewlines();
                    Expect(")");
                    return inner;
                }

                case GroovyTokenKind.Punct when token.Text == "-" && Peek().Kind == GroovyTokenKind.Number:
                    _position += 2;
                    return new GroovyNumber("-" + Peek(-1).Text);

                default:
                    return new GroovyRawValue(CaptureExpressionText());
            }
        }

        private GroovyValue ParseListOrMap()
        {
            Expect("[");
            SkipNewlines();

            if (Current.IsPunct("]"))
            {
                _position++;
                return new GroovyList([]);
            }

            if (Current.IsPunct(":") && Peek().IsPunct("]"))
            {
                _position += 2;
                return new GroovyMap([]);
            }

            var isMap = Current.Kind is GroovyTokenKind.Identifier or GroovyTokenKind.String && Peek().IsPunct(":");
            if (isMap)
            {
                var entries = ParseArguments("]");
                Expect("]");
                return new GroovyMap(entries);
            }

            var items = new List<GroovyValue>();
            while (!AtEnd && !Current.IsPunct("]"))
            {
                items.Add(ParseExpression());
                SkipNewlines();

                if (Current.IsPunct(","))
                {
                    _position++;
                    SkipNewlines();
                    continue;
                }

                if (!Current.IsPunct("]")) throw new ParseException();
            }

            Expect("]");
            return new GroovyList(items);
        }

        private List<GroovyNode> ParseClosureBody()
        {
            Expect("{");

            // Closure parameters: { e -> ... }, { a, b -> ... }
            var look = _position;
            while (look < tokens.Count && tokens[look].Line == tokens[_position].Line &&
                   (tokens[look].Kind == GroovyTokenKind.Identifier || tokens[look].IsPunct(",")))
                look++;

            if (look < tokens.Count && tokens[look].IsPunct("->")) _position = look + 1;

            return ParseBlock(true);
        }

        private string ParseDottedName()
        {
            var builder = new StringBuilder(Current.Text);
            _position++;

            while (Current.IsPunct(".") && Peek().Kind == GroovyTokenKind.Identifier)
            {
                builder.Append('.').Append(Peek().Text);
                _position += 2;
            }

            return builder.ToString();
        }

        /// <summary>A statement we cannot parse: grab from the current position to the end of the statement (including a trailing {} block and else/catch).</summary>
        private GroovyRaw CaptureRaw()
        {
            var startToken = Current;
            var startOffset = startToken.Start;
            var endOffset = startOffset;
            var depth = 0;

            while (!AtEnd)
            {
                var token = Current;

                if (token.Kind == GroovyTokenKind.Newline)
                {
                    if (depth == 0)
                    {
                        // Continue when the next meaningful token is else/catch/finally/{.
                        var next = NextSignificant(_position + 1);
                        if (next is not null && (next.Text is "else" or "catch" or "finally" || next.IsPunct("{")))
                        {
                            _position++;
                            continue;
                        }

                        break;
                    }

                    _position++;
                    continue;
                }

                if (token.Kind == GroovyTokenKind.Punct)
                {
                    if (token.Text is "(" or "[" or "{")
                    {
                        depth++;
                    }
                    else if (token.Text is ")" or "]" or "}")
                    {
                        if (depth == 0) break;

                        depth--;
                        if (depth == 0 && token.Text == "}")
                        {
                            endOffset = token.End;
                            _position++;
                            var next = NextSignificant(_position);
                            if (next is not null && next.Text is "else" or "catch" or "finally") continue;

                            break;
                        }
                    }
                    else if (token.Text == ";" && depth == 0)
                    {
                        _position++;
                        break;
                    }
                }

                endOffset = token.End;
                _position++;
            }

            var text = source[startOffset..Math.Max(startOffset, endOffset)].Trim();
            return new GroovyRaw(startToken.Line, text);
        }

        private string CaptureExpressionText()
        {
            var startOffset = Current.Start;
            var endOffset = startOffset;
            var depth = 0;

            while (!AtEnd)
            {
                var token = Current;

                if (token.Kind == GroovyTokenKind.Newline && depth == 0) break;

                if (token.Kind == GroovyTokenKind.Punct)
                {
                    if (token.Text is "(" or "[" or "{")
                    {
                        depth++;
                    }
                    else if (token.Text is ")" or "]" or "}")
                    {
                        if (depth == 0) break;

                        depth--;
                    }
                    else if (depth == 0 && token.Text is "," or ";")
                    {
                        break;
                    }
                }

                endOffset = token.End;
                _position++;
            }

            return source[startOffset..Math.Max(startOffset, endOffset)].Trim();
        }

        private GroovyToken? NextSignificant(int index)
        {
            while (index < tokens.Count)
            {
                if (tokens[index].Kind != GroovyTokenKind.Newline)
                    return tokens[index].Kind == GroovyTokenKind.End ? null : tokens[index];

                index++;
            }

            return null;
        }

        private void SkipSeparators()
        {
            while (!AtEnd && (Current.Kind == GroovyTokenKind.Newline || Current.IsPunct(";"))) _position++;
        }

        private void SkipNewlines()
        {
            while (!AtEnd && Current.Kind == GroovyTokenKind.Newline) _position++;
        }

        private void SkipToLineEnd()
        {
            while (!AtEnd && Current.Kind != GroovyTokenKind.Newline) _position++;
        }

        private void Expect(string punct)
        {
            if (!Current.IsPunct(punct)) throw new ParseException();

            _position++;
        }
    }

    private sealed class ParseException : Exception;
}

public enum GroovyTokenKind
{
    Identifier,
    String,
    Number,
    Punct,
    Newline,
    End
}

public sealed record GroovyToken(
    GroovyTokenKind Kind,
    string Text,
    int Line,
    int Start,
    int End,
    bool Interpolated = false)
{
    public bool IsPunct(string text)
    {
        return Kind == GroovyTokenKind.Punct && Text == text;
    }
}

/// <summary>Groovy tokenizer: handles comments, four kinds of strings (including triple quotes and GString), numbers, identifiers and symbols.</summary>
public static class GroovyTokenizer
{
    private static readonly string[] MultiCharPuncts =
    [
        "==~", "?:", "?.", "->", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "++", "--", "=~", "<<",
        ">>", "::"
    ];

    public static List<GroovyToken> Tokenize(string source)
    {
        var tokens = new List<GroovyToken>();
        var index = 0;
        var line = 1;

        while (index < source.Length)
        {
            var character = source[index];

            if (character == '\n')
            {
                tokens.Add(new GroovyToken(GroovyTokenKind.Newline, "\n", line, index, index + 1));
                line++;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            // Line continuation: backslash followed by newline
            if (character == '\\' && index + 1 < source.Length && source[index + 1] == '\n')
            {
                index += 2;
                line++;
                continue;
            }

            if (character == '#' && index == 0 && source.StartsWith("#!"))
            {
                while (index < source.Length && source[index] != '\n') index++;
                continue;
            }

            if (character == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n') index++;
                continue;
            }

            if (character == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var close = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var end = close < 0 ? source.Length : close + 2;
                line += source[index..end].Count(c => c == '\n');
                index = end;
                continue;
            }

            if (character is '\'' or '"')
            {
                var start = index;
                var startLine = line;
                var (text, next, interpolated, newlines) = ReadString(source, index);
                tokens.Add(new GroovyToken(GroovyTokenKind.String, text, startLine, start, next, interpolated));
                line += newlines;
                index = next;
                continue;
            }

            if (char.IsDigit(character))
            {
                var start = index;
                while (index < source.Length &&
                       (char.IsLetterOrDigit(source[index]) || source[index] == '.' || source[index] == '_')) index++;

                tokens.Add(new GroovyToken(GroovyTokenKind.Number, source[start..index], line, start, index));
                continue;
            }

            if (char.IsLetter(character) || character is '_' or '$')
            {
                var start = index;
                while (index < source.Length &&
                       (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '$')) index++;

                tokens.Add(new GroovyToken(GroovyTokenKind.Identifier, source[start..index], line, start, index));
                continue;
            }

            var matched = MultiCharPuncts.FirstOrDefault(punct =>
                string.CompareOrdinal(source, index, punct, 0, punct.Length) == 0);
            if (matched is not null)
            {
                tokens.Add(new GroovyToken(GroovyTokenKind.Punct, matched, line, index, index + matched.Length));
                index += matched.Length;
                continue;
            }

            tokens.Add(new GroovyToken(GroovyTokenKind.Punct, character.ToString(), line, index, index + 1));
            index++;
        }

        tokens.Add(new GroovyToken(GroovyTokenKind.End, string.Empty, line, source.Length, source.Length));
        return tokens;
    }

    private static (string Text, int Next, bool Interpolated, int Newlines) ReadString(string source, int index)
    {
        var quote = source[index];
        var triple = index + 2 < source.Length && source[index + 1] == quote && source[index + 2] == quote;
        var closer = triple ? new string(quote, 3) : quote.ToString();
        var position = index + closer.Length;
        var builder = new StringBuilder();
        var newlines = 0;

        // A newline right after the opening triple quote is not content (Groovy prints it that way too).
        if (triple && position < source.Length && source[position] == '\n')
        {
            position++;
            newlines++;
        }

        while (position < source.Length)
        {
            if (string.CompareOrdinal(source, position, closer, 0, closer.Length) == 0)
            {
                position += closer.Length;
                return (builder.ToString(), position, quote == '"', newlines);
            }

            var character = source[position];

            if (character == '\\' && position + 1 < source.Length)
            {
                var escaped = source[position + 1];
                builder.Append(escaped switch
                {
                    'n' => "\n",
                    't' => "\t",
                    'r' => "\r",
                    '\\' => "\\",
                    '\'' => "'",
                    '"' => "\"",
                    '$' => "\\$",
                    '\n' => string.Empty,
                    _ => "\\" + escaped
                });

                if (escaped == '\n') newlines++;

                position += 2;
                continue;
            }

            if (character == '\n') newlines++;

            builder.Append(character);
            position++;
        }

        // Unterminated: consume to end of file.
        return (builder.ToString(), source.Length, quote == '"', newlines);
    }
}

public abstract record GroovyNode(int Line);

/// <summary><c>name(args) { body }</c>; the matching field is empty when there are no arguments or body.</summary>
public sealed record GroovyCall(
    int Line,
    string Name,
    IReadOnlyList<GroovyArgument> Arguments,
    IReadOnlyList<GroovyNode>? Body) : GroovyNode(Line)
{
    /// <summary>The first unnamed argument.</summary>
    public GroovyValue? First => Arguments.FirstOrDefault(argument => argument.Name is null)?.Value;

    /// <summary>The last segment of the name: <c>steps.sh</c> -> <c>sh</c>.</summary>
    public string ShortName => Name.Contains('.') ? Name[(Name.LastIndexOf('.') + 1)..] : Name;

    public GroovyValue? Named(string name)
    {
        return Arguments.FirstOrDefault(argument => string.Equals(argument.Name, name, StringComparison.Ordinal))
            ?.Value;
    }
}

/// <summary><c>X = value</c>, <c>env.X = value</c>, <c>def x = value</c>.</summary>
public sealed record GroovyAssignment(int Line, string Target, GroovyValue Value, bool IsDeclaration)
    : GroovyNode(Line);

/// <summary>A statement that could not be parsed, kept verbatim so the converter can warn.</summary>
public sealed record GroovyRaw(int Line, string Text) : GroovyNode(Line);

public sealed record GroovyArgument(string? Name, GroovyValue Value);

public abstract record GroovyValue;

public sealed record GroovyString(string Text, bool Interpolated) : GroovyValue;

public sealed record GroovyNumber(string Text) : GroovyValue;

/// <summary>An identifier or property path: <c>any</c>, <c>scm</c>, <c>env.FOO</c>, <c>params.X</c>, <c>true</c>.</summary>
public sealed record GroovyIdentifier(string Name) : GroovyValue;

public sealed record GroovyList(IReadOnlyList<GroovyValue> Items) : GroovyValue;

public sealed record GroovyMap(IReadOnlyList<GroovyArgument> Entries) : GroovyValue;

public sealed record GroovyClosure(IReadOnlyList<GroovyNode> Body) : GroovyValue;

/// <summary>A call used as a value: <c>credentials('id')</c>, <c>string(name: 'X')</c>, <c>usernamePassword(...)</c>.</summary>
public sealed record GroovyCallValue(GroovyCall Call) : GroovyValue;

public sealed record GroovyRawValue(string Text) : GroovyValue;