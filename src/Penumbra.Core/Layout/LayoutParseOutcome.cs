namespace Penumbra.Core.Layout;

/// <summary>Top-level verdict of the spatial parser.</summary>
public enum ParseOutcomeKind
{
    /// <summary>A trustworthy tree was produced; <see cref="LayoutParseOutcome.Root"/> is present.</summary>
    Accepted,

    /// <summary>The notation is unsupported or structurally broken; no tree is offered.</summary>
    Refused,

    /// <summary>Geometry admits more than one credible parse; the parser declines to guess.</summary>
    Ambiguous,
}

/// <summary>
/// Machine-readable reason a parse was not accepted. Designed so <c>RecognitionGate</c> can fold structural
/// refusal into its existing confidence gate. <see cref="None"/> is reserved for the accepted case.
/// </summary>
public enum ParseRefusalReason
{
    /// <summary>Not refused (an accepted outcome).</summary>
    None,

    /// <summary>A bracket is unmatched, crossed, or multiply owned.</summary>
    UnmatchedBracket,

    /// <summary>A superscript/subscript versus separate-line placement could not be resolved.</summary>
    UncertainScript,

    /// <summary>A general indexed subscript the CAS cannot preserve (e.g. <c>x_1</c>).</summary>
    GeneralSubscript,

    /// <summary>Fraction numerator/denominator/bar ownership is malformed or a minus/bar near-tie.</summary>
    AmbiguousFractionOwnership,

    /// <summary>A radical with empty or uncertain radicand/root-index ownership.</summary>
    EmptyRadicalOwnership,

    /// <summary>A function word versus single-letter-product reading could not be separated.</summary>
    AmbiguousFunctionWord,

    /// <summary>Adjacent digits versus an implicit product could not be resolved.</summary>
    DigitProductAmbiguity,

    /// <summary>A relation the current translator closure cannot honour safely.</summary>
    UnsupportedRelation,

    /// <summary>Notation outside the shipped grammar (<c>\sum</c>, <c>\int</c>, matrices, plain <c>&gt;</c>, …).</summary>
    UnsupportedNotation,

    /// <summary>A candidate parse loses a non-rejected stroke.</summary>
    LostStroke,

    /// <summary>A candidate parse owns a stroke or token twice.</summary>
    DoubleOwnership,

    /// <summary>The winning parse did not beat its runner-up by the required score margin.</summary>
    LowMargin,
}

/// <summary>
/// Typed accepted/refused/ambiguous parse contract carried by the recognition result. A layout
/// <see cref="Root"/> exists only on <see cref="ParseOutcomeKind.Accepted"/>; a refusal/ambiguity carries a
/// <see cref="Reason"/> and optional <see cref="Detail"/> and never a tree — LaTeX is serialized only from an
/// accepted tree.
/// </summary>
public sealed record LayoutParseOutcome
{
    private LayoutParseOutcome(ParseOutcomeKind kind, ParseRefusalReason reason, LayoutNode? root, string? detail)
    {
        Kind = kind;
        Reason = reason;
        Root = root;
        Detail = detail;
    }

    /// <summary>The verdict.</summary>
    public ParseOutcomeKind Kind { get; }

    /// <summary>The refusal/ambiguity category; <see cref="ParseRefusalReason.None"/> when accepted.</summary>
    public ParseRefusalReason Reason { get; }

    /// <summary>The accepted tree, or null for a refusal/ambiguity.</summary>
    public LayoutNode? Root { get; }

    /// <summary>Optional human-readable context; never fed to the CAS.</summary>
    public string? Detail { get; }

    /// <summary>Convenience: true only for an accepted outcome (implies a non-null <see cref="Root"/>).</summary>
    public bool IsAccepted => Kind == ParseOutcomeKind.Accepted;

    /// <summary>Builds an accepted outcome around a non-null tree.</summary>
    public static LayoutParseOutcome Accepted(LayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new LayoutParseOutcome(ParseOutcomeKind.Accepted, ParseRefusalReason.None, root, detail: null);
    }

    /// <summary>Builds a refusal with a concrete, non-<see cref="ParseRefusalReason.None"/> reason.</summary>
    public static LayoutParseOutcome Refused(ParseRefusalReason reason, string? detail = null)
    {
        if (reason == ParseRefusalReason.None)
        {
            throw new ArgumentException("a refusal needs a concrete reason", nameof(reason));
        }

        return new LayoutParseOutcome(ParseOutcomeKind.Refused, reason, root: null, detail);
    }

    /// <summary>Builds an ambiguity with a concrete, non-<see cref="ParseRefusalReason.None"/> reason.</summary>
    public static LayoutParseOutcome Ambiguous(ParseRefusalReason reason, string? detail = null)
    {
        if (reason == ParseRefusalReason.None)
        {
            throw new ArgumentException("an ambiguity needs a concrete reason", nameof(reason));
        }

        return new LayoutParseOutcome(ParseOutcomeKind.Ambiguous, reason, root: null, detail);
    }
}
