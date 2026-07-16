using System.Collections.ObjectModel;

namespace Penumbra.Core.Layout;

/// <summary>The class of an ownership breach found by <see cref="OwnershipValidator"/>.</summary>
public enum OwnershipViolationKind
{
    /// <summary>A non-rejected recognition token is owned by no node in the tree.</summary>
    LostToken,

    /// <summary>A token is owned by more than one node (leaf or structural mark).</summary>
    DoubleOwnership,

    /// <summary>The tree owns a token the recognition result does not list as a non-rejected symbol
    /// (a rejected/out-of-distribution glyph, or a foreign instance).</summary>
    UnknownOwnedToken,

    /// <summary>Two owned tokens share a source stroke id — stroke ownership is not disjoint.</summary>
    OverlappingStrokes,
}

/// <summary>A single ownership breach, carrying the offending token and a human-readable detail.</summary>
public sealed record OwnershipViolation(
    OwnershipViolationKind Kind, RecognizedToken Token, string Detail);

/// <summary>
/// Typed outcome of <see cref="OwnershipValidator.Validate"/>. A control-flow result, never an exception:
/// callers branch on <see cref="IsValid"/> and inspect <see cref="Violations"/>.
/// </summary>
public sealed record OwnershipValidationResult
{
    private OwnershipValidationResult(IReadOnlyList<OwnershipViolation> violations)
    {
        Violations = violations;
    }

    /// <summary>True when every non-rejected token is owned exactly once with disjoint strokes.</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>Every breach found; empty when valid.</summary>
    public IReadOnlyList<OwnershipViolation> Violations { get; }

    /// <summary>The canonical valid result.</summary>
    public static OwnershipValidationResult Valid { get; } =
        new(new ReadOnlyCollection<OwnershipViolation>(Array.Empty<OwnershipViolation>()));

    /// <summary>Wraps a non-empty violation set; returns <see cref="Valid"/> when empty.</summary>
    public static OwnershipValidationResult FromViolations(IReadOnlyList<OwnershipViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return violations.Count == 0
            ? Valid
            : new OwnershipValidationResult(
                new ReadOnlyCollection<OwnershipViolation>(violations.ToArray()));
    }
}

/// <summary>
/// Mechanically verifies the ownership invariant that makes a layout tree trustworthy: given a tree root and
/// the recognition token list, every non-rejected token must be owned exactly once (as a leaf operand or a
/// structural mark), no token may be lost or owned twice, and no two owned tokens may share a stroke.
/// Rejected (out-of-distribution) tokens are excluded from the required set and must not appear in the tree.
/// </summary>
public static class OwnershipValidator
{
    /// <summary>
    /// Validates <paramref name="root"/> against <paramref name="resultTokens"/> (the full recognition token
    /// list, rejected entries included — they are filtered internally).
    /// </summary>
    public static OwnershipValidationResult Validate(
        LayoutNode root, IReadOnlyList<RecognizedToken> resultTokens)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resultTokens);

        var owned = new List<RecognizedToken>();
        Collect(root, owned);

        var violations = new List<OwnershipViolation>();

        // Ownership multiplicity, keyed by reference identity so two distinct tokens with equal fields are
        // still two tokens.
        var ownershipCounts = new Dictionary<RecognizedToken, int>(ReferenceEqualityComparer.Instance);
        foreach (var token in owned)
        {
            ownershipCounts[token] = ownershipCounts.TryGetValue(token, out var n) ? n + 1 : 1;
        }

        foreach (var (token, count) in ownershipCounts)
        {
            if (count > 1)
            {
                violations.Add(new OwnershipViolation(
                    OwnershipViolationKind.DoubleOwnership, token,
                    $"token '{token.Latex}' is owned {count} times"));
            }
        }

        var required = new HashSet<RecognizedToken>(ReferenceEqualityComparer.Instance);
        foreach (var token in resultTokens)
        {
            if (!token.Rejected)
            {
                required.Add(token);
            }
        }

        // Lost: a required token no node owns.
        foreach (var token in required)
        {
            if (!ownershipCounts.ContainsKey(token))
            {
                violations.Add(new OwnershipViolation(
                    OwnershipViolationKind.LostToken, token,
                    $"non-rejected token '{token.Latex}' is owned by no node"));
            }
        }

        // Unknown: the tree owns something outside the required set (a rejected glyph or a foreign token).
        foreach (var token in ownershipCounts.Keys)
        {
            if (!required.Contains(token))
            {
                var reason = token.Rejected
                    ? $"rejected token '{token.Latex}' must not be owned"
                    : $"owned token '{token.Latex}' is not in the recognition result";
                violations.Add(new OwnershipViolation(
                    OwnershipViolationKind.UnknownOwnedToken, token, reason));
            }
        }

        // Stroke disjointness across every owned token.
        var strokeOwner = new Dictionary<Guid, RecognizedToken>();
        foreach (var token in owned)
        {
            foreach (var strokeId in token.SourceStrokeIds)
            {
                if (strokeOwner.TryGetValue(strokeId, out var first))
                {
                    if (!ReferenceEquals(first, token))
                    {
                        violations.Add(new OwnershipViolation(
                            OwnershipViolationKind.OverlappingStrokes, token,
                            $"stroke {strokeId} is shared by '{first.Latex}' and '{token.Latex}'"));
                    }
                }
                else
                {
                    strokeOwner[strokeId] = token;
                }
            }
        }

        return OwnershipValidationResult.FromViolations(violations);
    }

    /// <summary>Depth-first collection of every token the tree owns, marks included.</summary>
    private static void Collect(LayoutNode node, List<RecognizedToken> owned)
    {
        switch (node)
        {
            case LeafNode leaf:
                owned.Add(leaf.Token);
                break;

            case SequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    Collect(child, owned);
                }

                break;

            case ImplicitProductNode product:
                foreach (var factor in product.Factors)
                {
                    Collect(factor, owned);
                }

                break;

            case ScriptNode script:
                Collect(script.Base, owned);
                if (script.Superscript is not null)
                {
                    Collect(script.Superscript, owned);
                }

                if (script.Subscript is not null)
                {
                    Collect(script.Subscript, owned);
                }

                break;

            case FractionNode fraction:
                Collect(fraction.Numerator, owned);
                Collect(fraction.Denominator, owned);
                owned.Add(fraction.BarToken);
                break;

            case RadicalNode radical:
                Collect(radical.Radicand, owned);
                if (radical.RootIndex is not null)
                {
                    Collect(radical.RootIndex, owned);
                }

                owned.Add(radical.RadicalToken);
                break;

            case DelimitedGroupNode group:
                owned.Add(group.OpenToken);
                Collect(group.Inner, owned);
                owned.Add(group.CloseToken);
                break;

            case FunctionCallNode call:
                foreach (var nameToken in call.NameTokens)
                {
                    owned.Add(nameToken);
                }

                Collect(call.Argument, owned);
                break;

            case RelationNode relation:
                Collect(relation.Left, owned);
                owned.Add(relation.RelationToken);
                if (relation.Right is not null)
                {
                    Collect(relation.Right, owned);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(node), node.GetType().Name, "unknown layout node kind");
        }
    }
}
