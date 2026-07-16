using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Recognition;

namespace Penumbra.Runtime;

/// <summary>Where a taffy gesture's trial LaTeX should be built from.</summary>
public enum TaffyLiteralPath
{
    /// <summary>
    /// The hit region's parse outcome is null (a pre-5.5/legacy result with no structural opinion).
    /// Trial LaTeX is built by <see cref="LiteralRuns.Splice"/> over the flat token list.
    /// </summary>
    Flat,

    /// <summary>
    /// An accepted layout root owns the run's tokens as one literal node. Trial LaTeX is built by
    /// substituting that node and reserializing the whole tree.
    /// </summary>
    Tree,

    /// <summary>
    /// The region's parse outcome exists and is not accepted, or (defensively) an accepted tree does not
    /// own the run's tokens as one exact literal node. Taffy must refuse the hit rather than guess.
    /// </summary>
    Refused,
}

/// <summary>
/// Where in an accepted layout tree a literal run's grabbable digits live, or why it can't be located.
/// <see cref="Root"/>/<see cref="Target"/> are non-null exactly when <see cref="Path"/> is
/// <see cref="TaffyLiteralPath.Tree"/>.
/// </summary>
public readonly record struct TaffyLiteralLocation(TaffyLiteralPath Path, LayoutNode? Root, LayoutNode? Target)
{
    public static readonly TaffyLiteralLocation FlatFallback = new(TaffyLiteralPath.Flat, null, null);
    public static readonly TaffyLiteralLocation StructuralRefusal = new(TaffyLiteralPath.Refused, null, null);
}

/// <summary>One numeric literal discovered together with its authoritative replacement location.</summary>
public sealed record TaffyLiteralCandidate(LiteralRun Run, TaffyLiteralLocation Location);

/// <summary>
/// Phase 5.5 slice 4 step 5: locates a <see cref="LiteralRun"/>'s owning node inside an accepted
/// <see cref="LayoutNode"/> tree and rebuilds a trial LaTeX string by substituting that one node and
/// reserializing the whole tree — never splicing the flat token list once spatial output has landed
/// (kickoff plan, section D final bullet). <see cref="LiteralRuns.Splice"/> remains the path only for
/// results whose parse outcome is null.
/// <para>
/// A region whose <see cref="LayoutParseOutcome"/> is present but not accepted (<see cref="ParseOutcomeKind.Refused"/>
/// or <see cref="ParseOutcomeKind.Ambiguous"/>) refuses outright: a structurally untrustworthy line offers
/// no literal to grab. This is defense-in-depth alongside <see cref="Penumbra.Recognition.RecognitionGate"/>,
/// which already keeps any region carrying a non-accepted <c>ParseOutcome</c> out of
/// <see cref="PageRecognitionSession.AcceptedRegions"/> for every real recognizer (confidence/OOD is checked
/// first, but a structural refusal independently fails the same gate) — so this branch cannot currently be
/// reached through the live product/corpus pipeline. It stays an explicit, directly tested invariant of this
/// new code rather than a silent assumption borrowed from an upstream gate.
/// </para>
/// </summary>
public static class TaffyLiteralTree
{
    /// <summary>
    /// Discovers numeric literals from an accepted layout tree. Each digit/decimal node is one candidate,
    /// so structurally adjacent literals never merge merely because their tokens are adjacent in the result
    /// array. Flat token discovery is retained only for a legacy result carrying no parse outcome.
    /// </summary>
    public static IReadOnlyList<TaffyLiteralCandidate> Discover(RecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ParseOutcome is null)
        {
            return LiteralRuns.Find(result.Tokens)
                .Select(run => new TaffyLiteralCandidate(run, TaffyLiteralLocation.FlatFallback))
                .ToArray();
        }
        if (!result.ParseOutcome.IsAccepted || result.ParseOutcome.Root is null)
        {
            return [];
        }

        LayoutNode root = result.ParseOutcome.Root;
        var candidates = new List<TaffyLiteralCandidate>();
        Discover(root, root, result.Tokens, candidates);
        return candidates.ToArray();
    }

    /// <summary>Resolves where <paramref name="run"/>'s trial LaTeX should come from for <paramref name="result"/>.</summary>
    public static TaffyLiteralLocation Locate(RecognitionResult result, LiteralRun run)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(run);

        if (result.ParseOutcome is { IsAccepted: false })
        {
            return TaffyLiteralLocation.StructuralRefusal;
        }

        if (result.ParseOutcome is null)
        {
            return TaffyLiteralLocation.FlatFallback;
        }

        TaffyLiteralCandidate? candidate = Discover(result)
            .FirstOrDefault(item => SameRun(item.Run, run));
        return candidate?.Location ?? TaffyLiteralLocation.StructuralRefusal;
    }

    /// <summary>
    /// Reserializes <paramref name="location"/>'s whole tree with its target literal node's digits replaced
    /// by <paramref name="valueText"/> (parenthesized when negative, matching <see cref="LiteralRuns.Splice"/>'s
    /// convention for the same reason: a bare leading <c>'-'</c> is unproven mid-expression). Valid only when
    /// <see cref="TaffyLiteralLocation.Path"/> is <see cref="TaffyLiteralPath.Tree"/>.
    /// </summary>
    public static string BuildTrialLatex(TaffyLiteralLocation location, string valueText)
    {
        ArgumentNullException.ThrowIfNull(valueText);
        if (location.Path != TaffyLiteralPath.Tree || location.Root is null || location.Target is null)
        {
            throw new InvalidOperationException(
                "A tree trial substitution requires a located tree literal (TaffyLiteralPath.Tree).");
        }

        LayoutNode replacement = BuildReplacement(valueText);
        LayoutNode replaced = Replace(location.Root, location.Target, replacement);
        return LayoutLatexSerializer.Serialize(replaced);
    }

    // ---- literal-node discovery ----------------------------------------------------------------------

    /// <summary>
    /// Walks structural children until it finds a maximal digit/decimal leaf or sequence, then builds a
    /// run only when those exact token references belong to <paramref name="resultTokens"/>.
    /// </summary>
    private static void Discover(
        LayoutNode root,
        LayoutNode node,
        IReadOnlyList<RecognizedToken> resultTokens,
        ICollection<TaffyLiteralCandidate> candidates)
    {
        RecognizedToken[]? numericTokens = OwnedLeafTokensIfPureNumberShape(node);
        if (numericTokens is not null)
        {
            LiteralRun? run = BuildRun(resultTokens, numericTokens);
            if (run is not null)
            {
                candidates.Add(new TaffyLiteralCandidate(
                    run,
                    new TaffyLiteralLocation(TaffyLiteralPath.Tree, root, node)));
            }

            // This node is the maximal numeric scope. Invalid decimal notation is not decomposed into
            // individually grabbable digits, because that would turn malformed notation into plausible math.
            return;
        }

        foreach (LayoutNode child in Children(node))
        {
            Discover(root, child, resultTokens, candidates);
        }
    }

    /// <summary>
    /// The node's owned leaf tokens, in order, when the node is EXACTLY a digit/decimal-point leaf or a flat
    /// sequence of such leaves — null for anything else (a variable/operator leaf, or a structural node), so
    /// callers never mistake an unrelated leaf/sequence for a grabbable literal.
    /// </summary>
    private static RecognizedToken[]? OwnedLeafTokensIfPureNumberShape(LayoutNode node) => node switch
    {
        LeafNode leaf when IsNumberChar(leaf.Token.Latex) => new[] { leaf.Token },
        SequenceNode sequence when sequence.Children.All(
            child => child is LeafNode digitLeaf && IsNumberChar(digitLeaf.Token.Latex)) =>
            sequence.Children.Cast<LeafNode>().Select(child => child.Token).ToArray(),
        _ => null,
    };

    private static bool IsNumberChar(string latex) =>
        latex.Length == 1 && (char.IsAsciiDigit(latex[0]) || latex[0] == '.');

    private static LiteralRun? BuildRun(
        IReadOnlyList<RecognizedToken> resultTokens,
        IReadOnlyList<RecognizedToken> numericTokens)
    {
        int start = IndexOfReference(resultTokens, numericTokens[0]);
        if (start < 0 || start + numericTokens.Count > resultTokens.Count)
        {
            return null;
        }

        int dotCount = 0;
        InkBounds bounds = numericTokens[0].Bounds;
        var value = new System.Text.StringBuilder(numericTokens.Count);
        var sourceStrokeIds = new List<Guid>();
        for (int index = 0; index < numericTokens.Count; index++)
        {
            RecognizedToken token = numericTokens[index];
            if (!ReferenceEquals(resultTokens[start + index], token))
            {
                return null;
            }

            value.Append(token.Latex);
            sourceStrokeIds.AddRange(token.SourceStrokeIds);
            if (token.Latex == ".")
            {
                dotCount++;
            }
            if (index > 0)
            {
                bounds = Union(bounds, token.Bounds);
            }
        }

        string valueText = value.ToString();
        return dotCount > 1 || valueText == "."
            ? null
            : new LiteralRun(start, numericTokens.Count, valueText, bounds, sourceStrokeIds);
    }

    private static int IndexOfReference(
        IReadOnlyList<RecognizedToken> tokens,
        RecognizedToken target)
    {
        for (int index = 0; index < tokens.Count; index++)
        {
            if (ReferenceEquals(tokens[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private static InkBounds Union(InkBounds left, InkBounds right)
    {
        double x = Math.Min(left.X, right.X);
        double y = Math.Min(left.Y, right.Y);
        double farX = Math.Max(left.X + left.Width, right.X + right.Width);
        double farY = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new InkBounds(x, y, farX - x, farY - y);
    }

    private static bool SameRun(LiteralRun left, LiteralRun right) =>
        left.TokenStart == right.TokenStart
        && left.TokenCount == right.TokenCount
        && string.Equals(left.ValueText, right.ValueText, StringComparison.Ordinal)
        && left.SourceStrokeIds.SequenceEqual(right.SourceStrokeIds);

    private static IEnumerable<LayoutNode> Children(LayoutNode node) => node switch
    {
        LeafNode => Enumerable.Empty<LayoutNode>(),
        SequenceNode sequence => sequence.Children,
        ImplicitProductNode product => product.Factors,
        ScriptNode script => Concat(script.Base, script.Superscript, script.Subscript),
        FractionNode fraction => Concat(fraction.Numerator, fraction.Denominator),
        RadicalNode radical => Concat(radical.Radicand, radical.RootIndex),
        DelimitedGroupNode group => Concat(group.Inner),
        FunctionCallNode call => Concat(call.Argument),
        RelationNode relation => Concat(relation.Left, relation.Right),
        _ => Enumerable.Empty<LayoutNode>(),
    };

    private static IEnumerable<LayoutNode> Concat(params LayoutNode?[] nodes) =>
        nodes.Where(node => node is not null)!;

    // ---- whole-tree reconstruction --------------------------------------------------------------------

    /// <summary>
    /// Builds a scratch replacement node for a scrubbed literal's display text. The synthetic tokens are
    /// never persisted or re-validated — this tree exists only for one <see cref="LayoutLatexSerializer.Serialize"/>
    /// call, so only each character's <c>Latex</c> matters.
    /// </summary>
    private static LayoutNode BuildReplacement(string valueText)
    {
        string display = valueText.StartsWith('-') ? "(" + valueText + ")" : valueText;
        var tokens = new RecognizedToken[display.Length];
        for (int i = 0; i < display.Length; i++)
        {
            tokens[i] = new RecognizedToken(display[i].ToString(), Array.Empty<Guid>(), default, 1.0);
        }

        return tokens.Length == 1
            ? new LeafNode(tokens[0])
            : new SequenceNode(tokens.Select(token => (LayoutNode)new LeafNode(token)).ToList());
    }

    /// <summary>
    /// Rebuilds <paramref name="node"/>'s ancestor chain down to <paramref name="target"/>, swapping it for
    /// <paramref name="replacement"/> and reconstructing every other node along the path (layout nodes have
    /// no settable properties, so a targeted <c>with</c> is not available) while leaving every node outside
    /// the path untouched.
    /// </summary>
    private static LayoutNode Replace(LayoutNode node, LayoutNode target, LayoutNode replacement)
    {
        if (ReferenceEquals(node, target))
        {
            return replacement;
        }

        return node switch
        {
            LeafNode => node,
            SequenceNode sequence => new SequenceNode(
                sequence.Children.Select(child => Replace(child, target, replacement)).ToList()),
            ImplicitProductNode product => new ImplicitProductNode(
                product.Factors.Select(factor => Replace(factor, target, replacement)).ToList()),
            ScriptNode script => new ScriptNode(
                Replace(script.Base, target, replacement),
                script.Superscript is null ? null : Replace(script.Superscript, target, replacement),
                script.Subscript is null ? null : Replace(script.Subscript, target, replacement)),
            FractionNode fraction => new FractionNode(
                Replace(fraction.Numerator, target, replacement),
                Replace(fraction.Denominator, target, replacement),
                fraction.BarToken),
            RadicalNode radical => new RadicalNode(
                Replace(radical.Radicand, target, replacement),
                radical.RootIndex is null ? null : Replace(radical.RootIndex, target, replacement),
                radical.RadicalToken),
            DelimitedGroupNode group => new DelimitedGroupNode(
                Replace(group.Inner, target, replacement),
                group.OpenToken,
                group.CloseToken),
            FunctionCallNode call => new FunctionCallNode(
                call.FunctionName,
                call.NameTokens,
                Replace(call.Argument, target, replacement)),
            RelationNode relation => new RelationNode(
                Replace(relation.Left, target, replacement),
                relation.RelationToken,
                relation.Right is null ? null : Replace(relation.Right, target, replacement)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(node), node.GetType().Name, "unknown layout node kind"),
        };
    }
}
