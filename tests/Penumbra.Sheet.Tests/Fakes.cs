using Penumbra.Cas;

namespace Penumbra.Sheet.Tests;

/// <summary>
/// Counts every <see cref="IEvaluator.Evaluate"/> call passing through to an inner evaluator — the
/// instrument that proves recompute touches EXACTLY the dependent set and nothing else.
/// </summary>
internal sealed class CountingEvaluator : IEvaluator
{
    private readonly IEvaluator _inner;

    public CountingEvaluator(IEvaluator inner) => _inner = inner;

    public int Calls { get; private set; }

    /// <summary>Every LaTeX handed to the inner evaluator, in order.</summary>
    public List<string> SeenLatex { get; } = new();

    public EvaluationResult Evaluate(EvaluationRequest request)
    {
        Calls++;
        SeenLatex.Add(request.Latex);
        return _inner.Evaluate(request);
    }

    public void Reset()
    {
        Calls = 0;
        SeenLatex.Clear();
    }
}

/// <summary>
/// A CAS-free analyzer speaking a toy grammar — <c>name=rhs</c> defines, trailing <c>=</c> queries,
/// single lowercase letters are variables. Proves the graph runs with no AngouriMath in sight.
/// </summary>
internal sealed class FakeAnalyzer : IExpressionAnalyzer
{
    public ExpressionAnalysis Analyze(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
        {
            return new ExpressionAnalysis(null, new HashSet<string>(), false);
        }

        var eq = latex.IndexOf('=');
        if (eq < 0)
        {
            return new ExpressionAnalysis(null, VarsOf(latex), false);
        }

        var left = latex[..eq];
        var right = latex[(eq + 1)..];
        if (right.Length == 0)
        {
            return new ExpressionAnalysis(null, VarsOf(left), IsQuery: true);
        }

        if (left.Length == 1 && char.IsAsciiLetterLower(left[0]))
        {
            return new ExpressionAnalysis(left, VarsOf(right), false);
        }

        var both = VarsOf(left);
        both.UnionWith(VarsOf(right));
        return new ExpressionAnalysis(null, both, false);
    }

    private static HashSet<string> VarsOf(string text) =>
        text.Where(char.IsAsciiLetterLower).Select(c => c.ToString()).ToHashSet();
}

/// <summary>
/// A CAS-free evaluator that just echoes the expression with its bindings spliced in — enough to
/// observe that definition values flowed downstream, without evaluating anything.
/// </summary>
internal sealed class FakeEvaluator : IEvaluator
{
    public EvaluationResult Evaluate(EvaluationRequest request)
    {
        var text = request.Latex;
        foreach (var (name, value) in request.Variables.OrderBy(p => p.Key))
        {
            text = text.Replace(name, $"[{value}]");
        }

        return new EvaluationResult(text, text, IsComputed: true, EvaluationKind.Symbolic);
    }
}
