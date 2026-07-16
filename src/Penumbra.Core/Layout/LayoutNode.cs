using System.Collections.ObjectModel;

namespace Penumbra.Core.Layout;

/// <summary>
/// Immutable, neutral recursive layout tree — the recognition authority for Phase 5.5 spatial grammar.
/// Recognition produces a <see cref="LayoutNode"/>; Sheet/App may retain it; LaTeX is a serialization of an
/// accepted tree, never the source of truth. Core owns these types so no consumer depends on Recognition or
/// AngouriMath.
/// <para>
/// Ownership contract: every accepted stroke/token is owned <b>exactly once</b>. A <see cref="LeafNode"/>
/// owns a single operand token; structural marks — the fraction bar, the radical sign, bracket glyphs, the
/// relation sign, and the letters spelling a function name — are owned by their structural node, not left as
/// free leaves. <see cref="OwnershipValidator"/> mechanically enforces this against the recognition token
/// list.
/// </para>
/// </summary>
public abstract record LayoutNode
{
    // Non-public constructor: the sealed record set below is closed. External code cannot invent a node kind
    // the serializer and validator do not understand.
    private protected LayoutNode()
    {
    }

    /// <summary>Snapshots a child list into an immutable collection, rejecting a null list or null element.</summary>
    private protected static IReadOnlyList<LayoutNode> Freeze(IReadOnlyList<LayoutNode> children, string paramName)
    {
        ArgumentNullException.ThrowIfNull(children, paramName);
        var copy = new LayoutNode[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            copy[i] = children[i] ?? throw new ArgumentNullException(
                paramName, $"child at index {i} is null");
        }

        return new ReadOnlyCollection<LayoutNode>(copy);
    }

    /// <summary>Snapshots a token list into an immutable collection, rejecting a null list or null element.</summary>
    private protected static IReadOnlyList<RecognizedToken> Freeze(
        IReadOnlyList<RecognizedToken> tokens, string paramName)
    {
        ArgumentNullException.ThrowIfNull(tokens, paramName);
        var copy = new RecognizedToken[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            copy[i] = tokens[i] ?? throw new ArgumentNullException(
                paramName, $"token at index {i} is null");
        }

        return new ReadOnlyCollection<RecognizedToken>(copy);
    }
}

/// <summary>
/// A terminal node owning exactly one operand <see cref="RecognizedToken"/> (a digit, variable, decimal
/// point, operator such as <c>+</c>/<c>-</c>, or an explicit <c>\times</c>/<c>\div</c>). Structural marks are
/// never leaves — they belong to their structural node.
/// </summary>
public sealed record LeafNode : LayoutNode
{
    public LeafNode(RecognizedToken token)
    {
        Token = token ?? throw new ArgumentNullException(nameof(token));
    }

    /// <summary>The single owned symbol.</summary>
    public RecognizedToken Token { get; }
}

/// <summary>
/// A left-to-right baseline run whose children concatenate. Digit adjacency forms a multi-digit number
/// (<c>1</c>,<c>3</c> → <c>13</c>), matching the recognizer's linear assembler discipline; multiplication of
/// distinct factors is modelled by <see cref="ImplicitProductNode"/>, not here.
/// </summary>
public sealed record SequenceNode : LayoutNode
{
    public SequenceNode(IReadOnlyList<LayoutNode> children)
    {
        Children = Freeze(children, nameof(children));
        if (Children.Count == 0)
        {
            throw new ArgumentException("a sequence must have at least one child", nameof(children));
        }
    }

    /// <summary>Ordered baseline children.</summary>
    public IReadOnlyList<LayoutNode> Children { get; }
}

/// <summary>
/// Factors multiplied by adjacency with no written operator (<c>2x</c>, <c>2(x+1)</c>, <c>(x+1)(x-1)</c>).
/// The serializer inserts an explicit product only where silent concatenation would change value
/// (digit-against-digit).
/// </summary>
public sealed record ImplicitProductNode : LayoutNode
{
    public ImplicitProductNode(IReadOnlyList<LayoutNode> factors)
    {
        Factors = Freeze(factors, nameof(factors));
        if (Factors.Count < 2)
        {
            throw new ArgumentException("an implicit product needs at least two factors", nameof(factors));
        }
    }

    /// <summary>Ordered factors, each multiplied against its neighbour.</summary>
    public IReadOnlyList<LayoutNode> Factors { get; }
}

/// <summary>
/// A base with an optional superscript and/or subscript slot (<c>x^2</c>, <c>x_1</c>, <c>e^x</c>). At least
/// one slot is present. Subscript layout is representable, but Phase 5.5 only accepts a semantic subscript
/// where the CAS contract can preserve it — general indexed variables refuse at the parser.
/// </summary>
public sealed record ScriptNode : LayoutNode
{
    public ScriptNode(LayoutNode @base, LayoutNode? superscript, LayoutNode? subscript)
    {
        Base = @base ?? throw new ArgumentNullException(nameof(@base));
        if (superscript is null && subscript is null)
        {
            throw new ArgumentException("a script needs a superscript or a subscript", nameof(superscript));
        }

        Superscript = superscript;
        Subscript = subscript;
    }

    /// <summary>The base the scripts attach to.</summary>
    public LayoutNode Base { get; }

    /// <summary>The superscript slot, or null.</summary>
    public LayoutNode? Superscript { get; }

    /// <summary>The subscript slot, or null.</summary>
    public LayoutNode? Subscript { get; }
}

/// <summary>
/// A stacked fraction owning its numerator, denominator, and the horizontal bar glyph. The bar is a
/// structural mark: it is owned here, never emitted as a free leaf.
/// </summary>
public sealed record FractionNode : LayoutNode
{
    public FractionNode(LayoutNode numerator, LayoutNode denominator, RecognizedToken barToken)
    {
        Numerator = numerator ?? throw new ArgumentNullException(nameof(numerator));
        Denominator = denominator ?? throw new ArgumentNullException(nameof(denominator));
        BarToken = barToken ?? throw new ArgumentNullException(nameof(barToken));
    }

    /// <summary>Content above the bar.</summary>
    public LayoutNode Numerator { get; }

    /// <summary>Content below the bar.</summary>
    public LayoutNode Denominator { get; }

    /// <summary>The owned fraction-bar glyph.</summary>
    public RecognizedToken BarToken { get; }
}

/// <summary>
/// A radical owning its radicand, an optional root index (<c>\sqrt[3]{x}</c>), and the radical sign glyph.
/// The radical sign is a structural mark owned here.
/// </summary>
public sealed record RadicalNode : LayoutNode
{
    public RadicalNode(LayoutNode radicand, LayoutNode? rootIndex, RecognizedToken radicalToken)
    {
        Radicand = radicand ?? throw new ArgumentNullException(nameof(radicand));
        RootIndex = rootIndex;
        RadicalToken = radicalToken ?? throw new ArgumentNullException(nameof(radicalToken));
    }

    /// <summary>Content under the radical.</summary>
    public LayoutNode Radicand { get; }

    /// <summary>The optional root index; null for a square root.</summary>
    public LayoutNode? RootIndex { get; }

    /// <summary>The owned radical-sign glyph.</summary>
    public RecognizedToken RadicalToken { get; }
}

/// <summary>
/// A bracketed group owning its inner content and both bracket glyphs. The open/close glyphs are structural
/// marks owned here; their pairing is validated before the tolerant translator ever sees the string.
/// </summary>
public sealed record DelimitedGroupNode : LayoutNode
{
    public DelimitedGroupNode(LayoutNode inner, RecognizedToken openToken, RecognizedToken closeToken)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        OpenToken = openToken ?? throw new ArgumentNullException(nameof(openToken));
        CloseToken = closeToken ?? throw new ArgumentNullException(nameof(closeToken));
    }

    /// <summary>The bracketed content.</summary>
    public LayoutNode Inner { get; }

    /// <summary>The owned opening bracket glyph (<c>(</c> or <c>[</c>).</summary>
    public RecognizedToken OpenToken { get; }

    /// <summary>The owned closing bracket glyph (<c>)</c> or <c>]</c>).</summary>
    public RecognizedToken CloseToken { get; }
}

/// <summary>
/// A named function applied to an argument (<c>\sin(x)</c>). The glyphs spelling the name — separate letter
/// tokens such as <c>s</c>,<c>i</c>,<c>n</c> — are structural marks owned here so the translator reads a
/// single function command rather than a product of letters.
/// </summary>
public sealed record FunctionCallNode : LayoutNode
{
    public FunctionCallNode(
        string functionName, IReadOnlyList<RecognizedToken> nameTokens, LayoutNode argument)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw new ArgumentException("a function call needs a name", nameof(functionName));
        }

        FunctionName = functionName;
        NameTokens = Freeze(nameTokens, nameof(nameTokens));
        if (NameTokens.Count == 0)
        {
            throw new ArgumentException("a function call must own its name glyphs", nameof(nameTokens));
        }

        Argument = argument ?? throw new ArgumentNullException(nameof(argument));
    }

    /// <summary>The canonical function name without a leading backslash (<c>sin</c>, <c>cos</c>, <c>ln</c>).</summary>
    public string FunctionName { get; }

    /// <summary>The owned glyphs spelling the name.</summary>
    public IReadOnlyList<RecognizedToken> NameTokens { get; }

    /// <summary>The function argument.</summary>
    public LayoutNode Argument { get; }
}

/// <summary>
/// A binary relation owning its relation sign (<c>=</c>, <c>\leq</c>, <c>\geq</c>, <c>\neq</c>, <c>\lt</c>).
/// <see cref="Right"/> may be null for a trailing-operator query such as <c>2x^2+3x-1=</c>.
/// </summary>
public sealed record RelationNode : LayoutNode
{
    public RelationNode(LayoutNode left, RecognizedToken relationToken, LayoutNode? right)
    {
        Left = left ?? throw new ArgumentNullException(nameof(left));
        RelationToken = relationToken ?? throw new ArgumentNullException(nameof(relationToken));
        Right = right;
    }

    /// <summary>The left-hand side.</summary>
    public LayoutNode Left { get; }

    /// <summary>The owned relation-sign glyph.</summary>
    public RecognizedToken RelationToken { get; }

    /// <summary>The right-hand side, or null for a trailing-relation query.</summary>
    public LayoutNode? Right { get; }
}
