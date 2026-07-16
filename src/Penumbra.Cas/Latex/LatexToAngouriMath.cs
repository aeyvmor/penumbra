using System.Text;

namespace Penumbra.Cas.Latex;

/// <summary>
/// Translates a LaTeX math string into AngouriMath's parser syntax.
/// </summary>
/// <remarks>
/// AngouriMath parses its own infix syntax (and emits LaTeX natively), but it does not
/// consume LaTeX as input — so this small, heavily-tested layer bridges the gap. It is the
/// finicky seam called out in <c>docs/ARCHITECTURE.md</c>: <c>\frac</c>, <c>\sqrt</c>,
/// <c>\cdot</c>, implicit multiplication (<c>2x</c> → <c>2*x</c>), <c>\pi</c>, function names.
///
/// Scope (Phase 2, "flat"): the common single-expression / single-equation constructs. It is a
/// translator, not a validator — malformed LaTeX yields best-effort output that AngouriMath then
/// rejects, surfaced as an evaluation failure upstream.
/// </remarks>
public static class LatexToAngouriMath
{
    /// <summary>Translates <paramref name="latex"/> into an AngouriMath-parseable string.</summary>
    public static string Translate(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
        {
            return string.Empty;
        }

        var scanner = new Scanner(latex);
        return TranslateSequence(scanner, closer: null);
    }

    /// <summary>
    /// Translates a run of tokens until end-of-input (or, when <paramref name="closer"/> is set,
    /// the matching delimiter — which is left for the caller to consume), inserting implicit
    /// multiplication between adjacent value tokens.
    /// </summary>
    private static string TranslateSequence(Scanner scanner, char? closer)
    {
        var sb = new StringBuilder();

        // True when the last thing emitted can be the *left* operand of an implicit product
        // (a number, identifier, group, or factorial). Two values in a row → insert '*'.
        var prevIsValue = false;

        void EmitValue(string text)
        {
            if (prevIsValue)
            {
                sb.Append('*');
            }

            sb.Append(text);
            prevIsValue = true;
        }

        void EmitOperator(string text)
        {
            sb.Append(text);
            prevIsValue = false;
        }

        while (!scanner.Eof)
        {
            var c = scanner.Peek();
            if (closer.HasValue && c == closer.Value)
            {
                break;
            }

            if (char.IsWhiteSpace(c))
            {
                scanner.Next();
                continue;
            }

            switch (c)
            {
                case '{':
                    EmitValue("(" + ReadDelimited(scanner, '{', '}') + ")");
                    break;

                case '(':
                    EmitValue("(" + ReadDelimited(scanner, '(', ')') + ")");
                    break;

                case '[':
                    EmitValue("(" + ReadDelimited(scanner, '[', ']') + ")");
                    break;

                case '}':
                case ')':
                case ']':
                    // Stray closer with no matching opener: consume and ignore.
                    scanner.Next();
                    break;

                case '\\':
                {
                    var token = TranslateCommand(scanner);
                    if (token.IsOperator)
                    {
                        EmitOperator(token.Text);
                    }
                    else if (!token.IsEmpty)
                    {
                        EmitValue(token.Text);
                    }

                    break;
                }

                case '^':
                    scanner.Next();
                    sb.Append("^(").Append(ReadAtom(scanner)).Append(')');
                    prevIsValue = true;
                    break;

                case '_':
                    // Subscript: fold into the preceding identifier's name (x_1 → x1, v_{0} → v0)
                    // so flat variables stay distinct. Appended directly, never multiplied.
                    scanner.Next();
                    sb.Append(ReadSubscript(scanner));
                    prevIsValue = true;
                    break;

                case '+':
                case '-':
                    scanner.Next();
                    EmitOperator(c.ToString());
                    break;

                case '*':
                    scanner.Next();
                    EmitOperator("*");
                    break;

                case '/':
                    scanner.Next();
                    EmitOperator("/");
                    break;

                case '=':
                    scanner.Next();
                    EmitOperator("=");
                    break;

                case '<':
                case '>':
                    scanner.Next();
                    if (!scanner.Eof && scanner.Peek() == '=')
                    {
                        scanner.Next();
                        EmitOperator(c == '<' ? "<=" : ">=");
                    }
                    else
                    {
                        EmitOperator(c.ToString());
                    }

                    break;

                case ',':
                    scanner.Next();
                    EmitOperator(",");
                    break;

                case '!':
                    scanner.Next();
                    if (!scanner.Eof && scanner.Peek() == '=')
                    {
                        // AngouriMath spells not-equal "<>". Passing "!=" through is parsed as
                        // factorial followed by equality (2! = 3), which is a convincing false result.
                        scanner.Next();
                        EmitOperator("<>");
                    }
                    else
                    {
                        sb.Append('!');
                        // Preserve the lexical boundary after postfix factorial. Adjacent "!=" is
                        // deliberately not-equal, while "3! = 5" is factorial equality; without this
                        // one space the translated strings collide before SplitEquation can distinguish them.
                        if (!scanner.Eof && char.IsWhiteSpace(scanner.Peek()))
                        {
                            sb.Append(' ');
                        }

                        prevIsValue = true;
                    }

                    break;

                case '|':
                    // |x| → abs(x): an opening bar when no value precedes, else a closing bar.
                    scanner.Next();
                    if (prevIsValue)
                    {
                        sb.Append(')');
                        prevIsValue = true;
                    }
                    else
                    {
                        sb.Append("abs(");
                        prevIsValue = false;
                    }

                    break;

                default:
                    if (char.IsDigit(c) || c == '.')
                    {
                        EmitValue(scanner.ReadNumber());
                    }
                    else if (char.IsLetter(c))
                    {
                        // Each Latin letter is its own variable, so "xy" → x*y (implicit product).
                        scanner.Next();
                        EmitValue(c.ToString());
                    }
                    else
                    {
                        // Unknown punctuation: skip it rather than poison the output.
                        scanner.Next();
                    }

                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Consumes <paramref name="open"/>, translates until <paramref name="close"/>, consumes it.</summary>
    private static string ReadDelimited(Scanner scanner, char open, char close)
    {
        scanner.Expect(open);
        var inner = TranslateSequence(scanner, close);
        scanner.Expect(close);
        return inner;
    }

    /// <summary>Reads and translates a single argument atom (for <c>^</c>, function args, etc.).</summary>
    private static string ReadAtom(Scanner scanner)
    {
        scanner.SkipSpaces();
        if (scanner.Eof)
        {
            return string.Empty;
        }

        var c = scanner.Peek();
        switch (c)
        {
            case '{':
                return ReadDelimited(scanner, '{', '}');

            case '(':
                return ReadDelimited(scanner, '(', ')');

            case '\\':
            {
                CommandToken command = TranslateCommand(scanner);

                // \left / \right (and spacing commands like "\,") tokenize as Empty — they only
                // decorate the delimiter that follows, carrying no atom of their own. Skip them and
                // read the atom they wrap, so \sin\left(x\right) reads its argument as "x" rather
                // than an empty group (which produced the "sin()*(x)" bug).
                return command.IsEmpty ? ReadAtom(scanner) : command.Text;
            }

            default:
                if (char.IsDigit(c) || c == '.')
                {
                    return scanner.ReadNumber();
                }

                if (char.IsLetter(c))
                {
                    scanner.Next();
                    return c.ToString();
                }

                scanner.Next();
                return string.Empty;
        }
    }

    /// <summary>Reads a subscript's text, stripped of braces/spaces, for folding into a name.</summary>
    private static string ReadSubscript(Scanner scanner)
    {
        scanner.SkipSpaces();
        if (scanner.Eof)
        {
            return string.Empty;
        }

        if (scanner.Peek() == '{')
        {
            scanner.Next();
            var sb = new StringBuilder();
            while (!scanner.Eof && scanner.Peek() != '}')
            {
                var ch = scanner.Next();
                if (!char.IsWhiteSpace(ch))
                {
                    sb.Append(ch);
                }
            }

            scanner.Expect('}');
            return sb.ToString();
        }

        return scanner.Next().ToString();
    }

    /// <summary>Reads a <c>\command</c> and returns its AngouriMath translation.</summary>
    private static CommandToken TranslateCommand(Scanner scanner)
    {
        scanner.Expect('\\');
        if (scanner.Eof)
        {
            return CommandToken.Empty;
        }

        // A control symbol like "\," or "\{" is a single non-letter character.
        if (!char.IsLetter(scanner.Peek()))
        {
            var symbol = scanner.Next();
            return symbol is '{' or '}'
                ? CommandToken.Value(symbol.ToString())
                : CommandToken.Empty; // spacing: \, \; \! "\ " etc.
        }

        var name = scanner.ReadWord();
        switch (name)
        {
            case "frac":
            case "dfrac":
            case "tfrac":
            {
                var numerator = ReadAtom(scanner);
                var denominator = ReadAtom(scanner);
                return CommandToken.Value($"(({numerator})/({denominator}))");
            }

            case "sqrt":
            {
                scanner.SkipSpaces();
                if (!scanner.Eof && scanner.Peek() == '[')
                {
                    var index = ReadDelimited(scanner, '[', ']');
                    var radicand = ReadAtom(scanner);
                    return CommandToken.Value($"(({radicand})^(1/({index})))");
                }

                return CommandToken.Value($"sqrt({ReadAtom(scanner)})");
            }

            case "log":
            {
                // \log_{b} x → log(b, x); bare \log defaults to base 10 (AngouriMath needs a base).
                scanner.SkipSpaces();
                if (!scanner.Eof && scanner.Peek() == '_')
                {
                    scanner.Next();
                    var baseArg = ReadAtom(scanner);
                    return CommandToken.Value($"log({baseArg},{ReadAtom(scanner)})");
                }

                return CommandToken.Value($"log(10,{ReadAtom(scanner)})");
            }

            case "exp":
                return CommandToken.Value($"e^({ReadAtom(scanner)})");

            case "sin" or "cos" or "tan" or "cot" or "sec" or "csc"
                or "sinh" or "cosh" or "tanh"
                or "arcsin" or "arccos" or "arctan"
                or "ln":
                return CommandToken.Value($"{name}({ReadAtom(scanner)})");

            case "cdot" or "times" or "ast":
                return CommandToken.Operator("*");

            case "div":
                return CommandToken.Operator("/");

            case "pm":
                // Rejected on purpose: "a \pm b" denotes two answers (a+b and a-b). Emitting only
                // "+" would silently drop the minus branch (a wrong single answer); translating both
                // branches is out of scope until the solver returns solution sets. \pm is one of the
                // 39 recognizer classes, so it can arrive from real ink — fail loudly, not silently.
                throw new NotSupportedException(
                    @"\pm is not supported yet (both-branch solutions arrive with a later phase).");

            case "leq" or "le":
                return CommandToken.Operator("<=");

            case "geq" or "ge":
                return CommandToken.Operator(">=");

            case "neq" or "ne":
                // AngouriMath's comparison grammar uses "<>". Its parser reads "!=" as factorial
                // plus equality, so preserving the familiar spelling would silently change the math.
                return CommandToken.Operator("<>");

            case "lt":
                // R1's shipped relation class is the LaTeX control word "\lt". Leaving it to the
                // generic command fallback turns "2\lt3" into the plausible symbolic product
                // "2*lt*3" — a silent wrong rather than a visible parse failure.
                return CommandToken.Operator("<");

            // Delimiter sizing only — drop it; the bracket itself is read next.
            case "left" or "right":
                return CommandToken.Empty;

            case "pi":
                return CommandToken.Value("pi");

            case "tau":
                return CommandToken.Value("(2*pi)");

            // Decorative / spacing words that carry no math.
            case "cdotp" or "ldots" or "dots" or "quad" or "qquad" or "displaystyle":
                return CommandToken.Empty;

            default:
                // Greek letters and other word commands become plain variable names (\theta → theta).
                return CommandToken.Value(name);
        }
    }

    /// <summary>A translated command: a value, an operator, or nothing.</summary>
    private readonly struct CommandToken
    {
        private CommandToken(string text, bool isOperator)
        {
            Text = text;
            IsOperator = isOperator;
        }

        public string Text { get; }

        public bool IsOperator { get; }

        public bool IsEmpty => Text.Length == 0 && !IsOperator;

        public static CommandToken Empty { get; } = new(string.Empty, isOperator: false);

        public static CommandToken Value(string text) => new(text, isOperator: false);

        public static CommandToken Operator(string text) => new(text, isOperator: true);
    }

    /// <summary>A tiny forward-only cursor over the LaTeX source.</summary>
    private sealed class Scanner
    {
        private readonly string _source;
        private int _position;

        public Scanner(string source) => _source = source;

        public bool Eof => _position >= _source.Length;

        public char Peek() => _source[_position];

        public char Next() => _source[_position++];

        public void SkipSpaces()
        {
            while (!Eof && char.IsWhiteSpace(Peek()))
            {
                _position++;
            }
        }

        /// <summary>Consumes the expected character if present (tolerant of malformed input).</summary>
        public void Expect(char c)
        {
            if (!Eof && Peek() == c)
            {
                _position++;
            }
        }

        public string ReadWord()
        {
            var start = _position;
            while (!Eof && char.IsLetter(Peek()))
            {
                _position++;
            }

            return _source[start.._position];
        }

        public string ReadNumber()
        {
            var start = _position;
            var seenDot = false;
            while (!Eof)
            {
                var c = Peek();
                if (char.IsDigit(c))
                {
                    _position++;
                }
                else if (c == '.' && !seenDot)
                {
                    seenDot = true;
                    _position++;
                }
                else
                {
                    break;
                }
            }

            return _source[start.._position];
        }
    }
}
