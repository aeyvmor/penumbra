using System.Text.Json;
using System.Text.RegularExpressions;
using Penumbra.Cas;
using Penumbra.Cas.Latex;
using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 3.9a (token-assembly separator) and 3.9b (digit-context glyph rewrite). A scripted
/// classifier isolates the assembly + rewrite logic; assembled LaTeX is then run through the real
/// <see cref="LatexToAngouriMath"/> / <see cref="AngouriMathEvaluator"/> to prove no phantom
/// concatenated identifiers ("\pi" + "x" → "pix") survive, and that arithmetic still evaluates.
/// </summary>
public sealed class DigitContextAndAssemblyTests
{
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "Models");

    // The 39 class labels the classifier can emit (source of truth for the property test).
    private static readonly IReadOnlyList<string> ClassLabels = LoadClassLabels();

    // Identifiers that may legitimately appear in a translation: everything each class label
    // translates to on its own (x, pi, sqrt, sum, int, theta, alpha, …) plus the CAS function
    // names. Anything outside this set in a *pair's* translation is a concatenation phantom.
    private static readonly HashSet<string> AllowedIdentifiers = BuildAllowedIdentifiers();

    // ---- 3.9a: separator only after \-commands; digits stay concatenated -------------------------

    [Fact]
    public void CommandFollowedByLetterOrDigit_NeverProducesAPhantomIdentifier()
    {
        string[] commands = ClassLabels.Where(l => l.StartsWith('\\')).ToArray();
        string[] followers = ClassLabels.Where(IsSingleLetterOrDigit).ToArray();

        Assert.NotEmpty(commands);
        Assert.NotEmpty(followers);

        foreach (string command in commands)
        {
            foreach (string follower in followers)
            {
                string latex = Assemble(command, follower).Latex;
                string translated = LatexToAngouriMath.Translate(latex);

                foreach (string identifier in Identifiers(translated))
                {
                    Assert.True(
                        AllowedIdentifiers.Contains(identifier),
                        $"pair [{command}, {follower}] assembled \"{latex}\" → \"{translated}\" " +
                        $"produced phantom identifier \"{identifier}\"");
                }
            }
        }
    }

    [Theory]
    // \pi \times x = → pi*x= : the fix that motivated 3.9a (no "\pix" phantom).
    [InlineData(new[] { @"\pi", @"\times", "x", "=" }, "pi*x=")]
    [InlineData(new[] { @"\sqrt", "x", "=" }, "sqrt(x)=")]
    [InlineData(new[] { "3", @"\div", "b", "=" }, "3/b=")]
    // Multi-digit numbers depend on direct concatenation — no spaced "2 1" → 2*1.
    [InlineData(new[] { "2", "1", "+", "7", "=" }, "21+7=")]
    public void AssembledSequenceTranslatesCorrectly(string[] labels, string expected)
    {
        Assert.Equal(expected, LatexToAngouriMath.Translate(Assemble(labels).Latex));
    }

    [Fact]
    public void ConcatenatedDigitsEvaluateAsOneNumber_Not_ImplicitProduct()
    {
        // The verified trap: "2""1""+""7""=" must be 21+7 = 28, never 2*1+7 = 9.
        string latex = Assemble("2", "1", "+", "7", "=").Latex;

        var result = new AngouriMathEvaluator()
            .Evaluate(new EvaluationRequest(latex, new Dictionary<string, string>()));

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.Equal("28", result.DisplayText);
    }

    // ---- 3.9b: digit-context x↔\times and |→1 rewrite --------------------------------------------

    [Fact]
    public void XBetweenTwoDigits_BecomesTimes_AndTokenLabelUpdates()
    {
        RecognitionResult result = Assemble("3", "x", "7", "=");

        Assert.Equal(@"\times", result.Tokens[1].Latex);           // Seam-1 consumers see the fix
        Assert.Equal("3*7=", LatexToAngouriMath.Translate(result.Latex));
    }

    [Fact]
    public void RewritePreservesConfidenceOfTheRelabelledToken()
    {
        var classifier = new ScriptedClassifier(
            ("3", 0.91), ("x", 0.42), ("7", 0.88), ("=", 0.99));
        RecognitionResult result = Recognize(classifier, 4);

        Assert.Equal(@"\times", result.Tokens[1].Latex);
        Assert.Equal(0.42, result.Tokens[1].Confidence, 6);        // confidence carried over unchanged
    }

    [Fact]
    public void XWithDigitLeftButOperatorRight_StaysVariable()
    {
        // 2x+3= : left neighbour is a digit but the right neighbour is '+', so this is algebra.
        RecognitionResult result = Assemble("2", "x", "+", "3", "=");

        Assert.Equal("x", result.Tokens[1].Latex);
        Assert.Equal("2*x+3=", LatexToAngouriMath.Translate(result.Latex));
    }

    [Fact]
    public void XAtSequenceStart_HasNoLeftNeighbour_StaysVariable()
    {
        RecognitionResult result = Assemble("x", "+", "1", "=");

        Assert.Equal("x", result.Tokens[0].Latex);
        Assert.Equal("x+1=", LatexToAngouriMath.Translate(result.Latex));
    }

    [Fact]
    public void BarBetweenTwoDigits_BecomesOne()
    {
        // '|' is a real class label the classifier confuses with '1'; being a digit now it
        // concatenates into the surrounding number: "2|3" → "213".
        RecognitionResult result = Assemble("2", "|", "3");

        Assert.Equal("1", result.Tokens[1].Latex);
        Assert.Equal("213", result.Latex);
        Assert.Equal("213", LatexToAngouriMath.Translate(result.Latex));
    }

    [Fact]
    public void BarAtSequenceEnd_BecomesOne()
    {
        // 3.9g: '|' has no valid M1 reading, so it is relabelled to '1' UNCONDITIONALLY — even with
        // no right neighbour. (Previously the narrow rule left an edge '|' as-is.)
        RecognitionResult result = Assemble("2", "3", "|");

        Assert.Equal("1", result.Tokens[2].Latex);
        Assert.Equal("231", result.Latex);
    }

    [Fact]
    public void BarAtSequenceStart_BecomesOne()
    {
        // The mirror of the above: a leading '|' with no left neighbour is still a drawn '1'.
        RecognitionResult result = Assemble("|", "+", "3", "=");

        Assert.Equal("1", result.Tokens[0].Latex);
        Assert.Equal("1+3=", LatexToAngouriMath.Translate(result.Latex));
    }

    [Fact]
    public void BarWithDigitLeftButOperatorRight_BecomesOne()
    {
        // The real-ink case that motivated 3.9g: '2|+7=' (digit left, operator right) fell through the
        // old both-neighbours-are-values rule and the translator died on the raw '|'. Now → '21+7='.
        RecognitionResult result = Assemble("2", "|", "+", "7", "=");

        Assert.Equal("1", result.Tokens[1].Latex);
        Assert.Equal("21+7=", LatexToAngouriMath.Translate(result.Latex));

        var eval = new AngouriMathEvaluator()
            .Evaluate(new EvaluationRequest(result.Latex, new Dictionary<string, string>()));
        Assert.Equal(EvaluationKind.Number, eval.Kind);
        Assert.Equal("28", eval.DisplayText);
    }

    [Fact]
    public void BarAtStart_WithBarRewrite_EvaluatesAsArithmetic()
    {
        // '|+1=' → '1+1=' → 2, proving an edge '|' feeds cleanly through translation and evaluation.
        RecognitionResult result = Assemble("|", "+", "1", "=");

        Assert.Equal("1+1=", LatexToAngouriMath.Translate(result.Latex));

        var eval = new AngouriMathEvaluator()
            .Evaluate(new EvaluationRequest(result.Latex, new Dictionary<string, string>()));
        Assert.Equal(EvaluationKind.Number, eval.Kind);
        Assert.Equal("2", eval.DisplayText);
    }

    // ---- harness ---------------------------------------------------------------------------------

    // Drive the real recognizer over well-separated strokes, one per label, so the segmenter yields
    // one left-to-right group per scripted label — exercising the true assembly + rewrite path.
    private static RecognitionResult Assemble(params string[] labels) =>
        Recognize(new ScriptedClassifier(labels.Select(l => (l, 1.0)).ToArray()), labels.Length);

    private static RecognitionResult Recognize(ISymbolClassifier classifier, int count)
    {
        var strokes = new List<Stroke>(count);
        for (int i = 0; i < count; i++)
        {
            strokes.Add(VLine(i * 100));
        }

        return new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier).Recognize(strokes);
    }

    // Returns scripted (label, confidence) pairs in classification call order = left-to-right groups.
    private sealed class ScriptedClassifier : ISymbolClassifier
    {
        private readonly (string Label, double Confidence)[] _returns;
        private int _index;

        public ScriptedClassifier(params (string Label, double Confidence)[] returns) => _returns = returns;

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            (string label, double confidence) = _returns[_index++];
            return new SymbolPrediction(label, confidence);
        }
    }

    private static bool IsSingleLetterOrDigit(string label) =>
        label.Length == 1 && char.IsAsciiLetterOrDigit(label[0]);

    private static IEnumerable<string> Identifiers(string translated) =>
        Regex.Matches(translated, "[A-Za-z]+").Select(m => m.Value);

    private static HashSet<string> BuildAllowedIdentifiers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string label in ClassLabels)
        {
            foreach (string id in Identifiers(LatexToAngouriMath.Translate(label)))
            {
                allowed.Add(id);
            }
        }

        // CAS function names the translator can emit but which are not standalone class labels.
        foreach (string fn in new[]
                 {
                     "sin", "cos", "tan", "cot", "sec", "csc", "sinh", "cosh", "tanh",
                     "arcsin", "arccos", "arctan", "ln", "log", "exp", "abs", "sqrt", "e",
                 })
        {
            allowed.Add(fn);
        }

        return allowed;
    }

    private static IReadOnlyList<string> LoadClassLabels()
    {
        string json = File.ReadAllText(Path.Combine(ModelDir, "crohme_geo_cnn.meta.json"));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("classes")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static Stroke VLine(double x) =>
        new(Guid.NewGuid(), Enumerable.Range(0, 11)
            .Select(i => new StrokeSample(x, i * 2.0, TimeSpan.Zero, 0.5))
            .ToList());
}
