using AngouriMath;
using Penumbra.Cas.Latex;
using Penumbra.Core;
using Penumbra.Core.Layout;

namespace Penumbra.Graphing;

/// <summary>
/// The real <see cref="IGraphDetector"/>: recognizes an explicit-function candidate <c>y = f(x)</c> from
/// either an accepted layout tree or a LaTeX string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Variable-name policy:</b> the dependent/independent names are never hardcoded to <c>y</c>/<c>x</c>. The
/// left-hand side may be any single bare variable (<c>f=x^2</c> is as valid as <c>y=x^2</c>); the independent
/// variable is whichever single symbol the right-hand side actually depends on. <c>\pi</c>/<c>e</c> are
/// constants (AngouriMath's own <see cref="Entity.Vars"/> already excludes them) and never count as the
/// independent variable, so <c>y=\pi</c> is a constant, not a curve in <c>\pi</c>.
/// </para>
/// <para>
/// <b>Constant RHS policy:</b> <c>a=2</c> (and <c>a=\pi</c>) is deliberately <em>not</em> a graph candidate —
/// its right-hand side has zero free variables, so there is no curve to sample, only a single point repeated
/// across the domain. This is a Sheet-style definition, not something Phase 6 draws.
/// </para>
/// <para>
/// <b>Rejection taxonomy:</b> <see cref="GraphRejectionReason.NotAnEquation"/> (no top-level <c>=</c>, or a
/// trailing-relation query with no right-hand side), <see cref="GraphRejectionReason.LhsNotBareVariable"/>
/// (e.g. <c>2x=6</c>), <see cref="GraphRejectionReason.ConstantRhs"/>, <see cref="GraphRejectionReason.MultipleFreeVariables"/>
/// (e.g. <c>z=x+y</c>), and <see cref="GraphRejectionReason.UnsupportedConstruct"/> (a translator-rejected
/// construct such as <c>\pm</c>, a chained relation <c>y=x=2</c>, or a self-referential
/// <c>x=x^2</c> where the dependent variable names its own independent variable).
/// </para>
/// <para>
/// <b>Metrics discipline:</b> a clean rejection (no exception) records <see cref="MetricOutcome.Refused"/>; an
/// acceptance records <see cref="MetricOutcome.Completed"/>; a translate/parse exception caught at the
/// boundary records <see cref="MetricOutcome.Failed"/> — an internal exception path is never reported as a
/// deliberate refusal, matching the Phase 5.5 metrics ledger discipline.
/// </para>
/// </remarks>
public sealed class GraphDetector : IGraphDetector
{
    private readonly ILocalMetricsSink _metricsSink;
    private readonly TimeProvider _timeProvider;

    public GraphDetector()
        : this(NoOpLocalMetricsSink.Instance, TimeProvider.System)
    {
    }

    public GraphDetector(ILocalMetricsSink metricsSink, TimeProvider? timeProvider = null)
    {
        _metricsSink = metricsSink ?? throw new ArgumentNullException(nameof(metricsSink));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public GraphDetectionOutcome Detect(LayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using MetricTimingScope timing = MetricTimingScope.Start(_metricsSink, MetricOperation.GraphDetection, _timeProvider);
        try
        {
            GraphDetectionOutcome outcome = DetectFromLayoutCore(root);
            Terminate(timing, outcome);
            return outcome;
        }
        catch (Exception)
        {
            timing.Fail();
            return GraphDetectionOutcome.Rejected(
                GraphRejectionReason.UnsupportedConstruct, "the expression could not be parsed");
        }
    }

    /// <inheritdoc />
    public GraphDetectionOutcome Detect(string latex)
    {
        ArgumentNullException.ThrowIfNull(latex);

        using MetricTimingScope timing = MetricTimingScope.Start(_metricsSink, MetricOperation.GraphDetection, _timeProvider);
        try
        {
            GraphDetectionOutcome outcome = DetectFromLatexCore(latex);
            Terminate(timing, outcome);
            return outcome;
        }
        catch (Exception)
        {
            timing.Fail();
            return GraphDetectionOutcome.Rejected(
                GraphRejectionReason.UnsupportedConstruct, "the expression could not be parsed");
        }
    }

    private static void Terminate(MetricTimingScope timing, GraphDetectionOutcome outcome)
    {
        if (outcome.IsAccepted)
        {
            timing.Complete(1);
        }
        else
        {
            timing.Refuse();
        }
    }

    private static GraphDetectionOutcome DetectFromLayoutCore(LayoutNode root)
    {
        if (root is not RelationNode relation || relation.RelationToken.Latex != "=")
        {
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.NotAnEquation);
        }

        if (relation.Right is null)
        {
            // A trailing-relation query ("y=" with nothing after) is a compute request, not a curve.
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.NotAnEquation);
        }

        if (relation.Right is RelationNode)
        {
            // The grammar's own parser would not produce this, but nothing in the type system forbids
            // constructing it — refuse defensively rather than silently reading only the inner relation.
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.UnsupportedConstruct, "chained relation");
        }

        var leftLatex = LayoutLatexSerializer.Serialize(relation.Left);
        var rightLatex = LayoutLatexSerializer.Serialize(relation.Right);
        return ClassifyParts(leftLatex, rightLatex);
    }

    private static GraphDetectionOutcome DetectFromLatexCore(string latex)
    {
        (string leftLatex, string rightLatex, bool hasEquals) = SplitTopLevelEquation(latex);
        if (!hasEquals || string.IsNullOrWhiteSpace(rightLatex))
        {
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.NotAnEquation);
        }

        if (SplitTopLevelEquation(rightLatex).HasEquals)
        {
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.UnsupportedConstruct, "chained relation");
        }

        return ClassifyParts(leftLatex, rightLatex);
    }

    /// <summary>
    /// The shared core: classifies an already-split left/right LaTeX pair. Throws freely on a translate/parse
    /// failure — both public entry points catch at their own boundary and record <see cref="MetricOutcome.Failed"/>.
    /// </summary>
    private static GraphDetectionOutcome ClassifyParts(string leftLatex, string rightLatex)
    {
        var translatedLeft = LatexToAngouriMath.Translate(leftLatex);
        var leftEntity = MathS.FromString(translatedLeft);
        if (leftEntity is not Entity.Variable dependentVariable)
        {
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.LhsNotBareVariable);
        }

        var translatedRight = LatexToAngouriMath.Translate(rightLatex);
        var rightEntity = MathS.FromString(translatedRight);

        // Entity.Vars already excludes pi/e; the extra name filter mirrors AngouriMathExpressionAnalyzer's
        // defensive belt-and-braces so a future AngouriMath behaviour change can't silently reintroduce them.
        var freeVariables = rightEntity.Vars
            .Select(v => v.Name)
            .Where(name => name is not ("pi" or "e"))
            .ToHashSet();

        if (freeVariables.Count == 0)
        {
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.ConstantRhs);
        }

        if (freeVariables.Count > 1)
        {
            return GraphDetectionOutcome.Rejected(GraphRejectionReason.MultipleFreeVariables);
        }

        var independentVariable = freeVariables.Single();
        if (independentVariable == dependentVariable.Name)
        {
            // "x=x^2": the dependent variable is also the only free variable on the right — there is no
            // separate axis to sample against, so this is not an explicit y=f(x) curve.
            return GraphDetectionOutcome.Rejected(
                GraphRejectionReason.UnsupportedConstruct,
                "the dependent variable cannot be its own independent variable");
        }

        var candidate = new GraphCandidate(
            dependentVariable.Name, independentVariable, rightLatex.Trim(), rightEntity);
        return GraphDetectionOutcome.Accepted(candidate);
    }

    /// <summary>
    /// Splits raw LaTeX on the first top-level, standalone <c>=</c> (ignoring <c>&lt;=</c>, <c>&gt;=</c>,
    /// <c>!=</c>, <c>==</c>, and anything inside <c>{}</c>/<c>()</c>/<c>[]</c>). Mirrors
    /// <c>AngouriMathEvaluator.SplitEquation</c>'s algorithm but runs on the untranslated LaTeX dialect (so the
    /// returned halves stay LaTeX, for <see cref="GraphCandidate.ExpressionLatex"/>) — a single depth counter
    /// is sufficient here as elsewhere in this codebase because it only needs balanced nesting, not
    /// bracket-kind pairing (that is <c>OwnershipValidator</c>'s job for a full layout tree).
    /// </summary>
    private static (string Left, string Right, bool HasEquals) SplitTopLevelEquation(string latex)
    {
        var depth = 0;
        for (var i = 0; i < latex.Length; i++)
        {
            var c = latex[i];
            switch (c)
            {
                case '{' or '(' or '[':
                    depth++;
                    break;
                case '}' or ')' or ']':
                    depth--;
                    break;
                case '=' when depth <= 0:
                {
                    var prev = i > 0 ? latex[i - 1] : '\0';
                    var next = i + 1 < latex.Length ? latex[i + 1] : '\0';
                    if (prev is '<' or '>' or '!' or '=' || next == '=')
                    {
                        break; // part of a relational/equality operator, not a split point
                    }

                    return (latex[..i], latex[(i + 1)..], true);
                }
            }
        }

        return (latex, string.Empty, false);
    }
}
