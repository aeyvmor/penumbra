using System.Text;

namespace Penumbra.Core.Layout;

/// <summary>
/// Serializes an accepted <see cref="LayoutNode"/> tree to a LaTeX string in the exact dialect
/// <c>Penumbra.Cas.Latex.LatexToAngouriMath</c> consumes. The overriding guarantee: reading the emitted LaTeX
/// with that translator must never silently change mathematical value. Two boundary hazards are handled
/// explicitly:
/// <list type="bullet">
///   <item>a control word (<c>\pi</c>, <c>\theta</c>, …) directly followed by letters would merge into a
///   phantom name, so a separating space is inserted;</item>
///   <item>digit-against-digit adjacency in a product would read as one multi-digit number, so an explicit
///   <c>\times</c> is inserted rather than relying on silent concatenation.</item>
/// </list>
/// The <c>\lt</c> relation (a recognizer class the translator has no case for, which would otherwise become
/// the value name <c>lt</c>) is serialized as bare <c>&lt;</c>, which the translator carries through safely.
/// </summary>
public static class LayoutLatexSerializer
{
    /// <summary>Serializes <paramref name="root"/> to translator-safe LaTeX.</summary>
    public static string Serialize(LayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Emit(root);
    }

    private static string Emit(LayoutNode node) => node switch
    {
        LeafNode leaf => leaf.Token.Latex,
        SequenceNode sequence => EmitSequence(sequence.Children),
        ImplicitProductNode product => EmitProduct(product.Factors),
        ScriptNode script => EmitScript(script),
        FractionNode fraction => $@"\frac{{{Emit(fraction.Numerator)}}}{{{Emit(fraction.Denominator)}}}",
        RadicalNode radical => EmitRadical(radical),
        DelimitedGroupNode group =>
            $@"\left{group.OpenToken.Latex}{Emit(group.Inner)}\right{group.CloseToken.Latex}",
        FunctionCallNode call => $@"\{call.FunctionName}({Emit(call.Argument)})",
        RelationNode relation => EmitRelation(relation),
        _ => throw new ArgumentOutOfRangeException(
            nameof(node), node.GetType().Name, "unknown layout node kind"),
    };

    /// <summary>
    /// Baseline concatenation matching <c>TokenLatexAssembler</c>'s discipline: digits stay glued so
    /// multi-digit numbers survive, and a control word gets a trailing space so it cannot fuse with a
    /// following letter.
    /// </summary>
    private static string EmitSequence(IReadOnlyList<LayoutNode> children)
    {
        var sb = new StringBuilder();
        foreach (var child in children)
        {
            var fragment = Emit(child);
            if (fragment.Length == 0)
            {
                continue;
            }

            sb.Append(fragment);
            if (EndsWithControlWord(fragment))
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Products carry no operator where the translator's own implicit multiplication is safe, and an explicit
    /// <c>\times</c> only where silent concatenation would change value (digit meeting digit).
    /// </summary>
    private static string EmitProduct(IReadOnlyList<LayoutNode> factors)
    {
        var sb = new StringBuilder();
        var previous = string.Empty;
        foreach (var factor in factors)
        {
            var fragment = Emit(factor);
            if (fragment.Length == 0)
            {
                continue;
            }

            if (previous.Length > 0)
            {
                sb.Append(Separator(previous, fragment));
            }

            sb.Append(fragment);
            previous = fragment;
        }

        return sb.ToString();
    }

    /// <summary>Chooses the safe join between two product fragments.</summary>
    private static string Separator(string left, string right)
    {
        var leftEnd = left[^1];
        var rightStart = right[0];

        // A multi-digit read would swallow the boundary — force an explicit product.
        if (IsNumberChar(leftEnd) && IsNumberChar(rightStart))
        {
            return @"\times ";
        }

        // A control word touching a letter would fuse into a phantom identifier.
        if (char.IsLetter(rightStart) && EndsWithControlWord(left))
        {
            return " ";
        }

        // Value-against-value adjacency: the translator inserts the '*' itself.
        return string.Empty;
    }

    private static string EmitScript(ScriptNode script)
    {
        var sb = new StringBuilder(EmitScriptBase(script.Base));
        if (script.Subscript is not null)
        {
            sb.Append('_').Append('{').Append(Emit(script.Subscript)).Append('}');
        }

        if (script.Superscript is not null)
        {
            sb.Append('^').Append('{').Append(Emit(script.Superscript)).Append('}');
        }

        return sb.ToString();
    }

    /// <summary>
    /// A leaf or an already-delimited group can carry a script directly; anything else is wrapped in braces
    /// (which the translator reads as a group) so the script binds the whole base, not just its last atom.
    /// </summary>
    private static string EmitScriptBase(LayoutNode @base)
    {
        var fragment = Emit(@base);
        return @base is LeafNode or DelimitedGroupNode ? fragment : $"{{{fragment}}}";
    }

    private static string EmitRadical(RadicalNode radical)
    {
        var radicand = Emit(radical.Radicand);
        return radical.RootIndex is null
            ? $@"\sqrt{{{radicand}}}"
            : $@"\sqrt[{Emit(radical.RootIndex)}]{{{radicand}}}";
    }

    private static string EmitRelation(RelationNode relation)
    {
        var sb = new StringBuilder(Emit(relation.Left));
        var op = MapRelation(relation.RelationToken.Latex);
        sb.Append(op);

        var right = relation.Right is null ? string.Empty : Emit(relation.Right);

        // A control-word relation (\leq, …) needs a space before a letter right-hand side, else \leqy fuses.
        if (op.StartsWith('\\') && right.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(right);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Maps a relation glyph to translator-safe LaTeX. <c>\lt</c> has no translator case and would become the
    /// value name <c>lt</c>, so it degrades to bare <c>&lt;</c>; the others are already understood.
    /// </summary>
    private static string MapRelation(string latex) => latex switch
    {
        @"\lt" => "<",
        _ => latex,
    };

    private static bool IsNumberChar(char c) => char.IsDigit(c) || c == '.';

    /// <summary>True when a fragment ends in a <c>\word</c> whose trailing letters would absorb the next letter.</summary>
    private static bool EndsWithControlWord(string fragment)
    {
        var i = fragment.Length - 1;
        if (i < 0 || !char.IsLetter(fragment[i]))
        {
            return false;
        }

        while (i >= 0 && char.IsLetter(fragment[i]))
        {
            i--;
        }

        return i >= 0 && fragment[i] == '\\';
    }
}
