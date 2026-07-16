using Penumbra.Core;
using Penumbra.Core.Layout;

namespace Penumbra.Recognition;

/// <summary>
/// The final rewritten token list (Stage 0's contextual rewrite always runs, whatever the outcome) plus the
/// spatial grammar's typed verdict for one expression candidate's ordered tokens.
/// </summary>
public sealed record SpatialParseResult(LayoutParseOutcome Outcome, IReadOnlyList<RecognizedToken> Tokens);

/// <summary>
/// Phase 5.5 slice 5: the RECURSIVE spatial layout parser. Consumes one expression candidate's ordered
/// <see cref="RecognizedToken"/> list (plus each token's classifier <see cref="SymbolPrediction"/> for its
/// ranked alternatives) and produces a <see cref="Penumbra.Core.Layout.LayoutParseOutcome"/> — an accepted
/// <see cref="LayoutNode"/> tree, a typed refusal, or a typed ambiguity. It never chooses a parse merely
/// because it would look plausible; every accepted tree is proven against <see cref="OwnershipValidator"/>.
/// <para>
/// <b>Stages</b> (in order — an earlier stage's refusal short-circuits every later one):
/// </para>
/// <list type="number">
///   <item>
///   <b>Contextual rewrite.</b> Unconditional <c>| → 1</c> and the old digit-flanked <c>x → \times</c> rule
///   move here byte-for-byte from the pre-Phase-5.5 recognizer. Alternatives-based <c>0/o</c> and
///   <c>1/l</c> disambiguation — geometry (digit vs. letter neighbours, by ORIGINAL pre-rewrite labels, so a
///   rewrite never cascades into the next symbol's context) picks a preferred reading; the classifier's
///   ranked alternatives supply the competing confidence. A close-margin near-tie with NEUTRAL geometry
///   (no digit or letter neighbour either side) refuses the whole line <see cref="ParseRefusalReason.LowMargin"/>
///   rather than silently guessing; this stage always still returns a best-effort rewritten token list even
///   when it refuses, so Seam-1/UI/gate consumers see the same tokens they always would have.
///   </item>
///   <item>
///   <b>Equals-merge rescue.</b> Two adjacent groups that both classified as <c>-</c>, are vertically
///   stacked (X-overlap of their bounds at least <see cref="EqualsMergeMinXOverlapRatio"/> of the narrower),
///   close vertically (gap at most <see cref="EqualsMergeMaxVerticalGapRatio"/> of the line's reference
///   height), individually flat, and have no third token's ink sitting in the gap between them, are rewritten
///   into ONE <c>=</c> relation token owning both stroke sets (union bounds, confidence = the lower of the
///   two). This recovers the "= drawn as two strokes the segmenter kept apart" case without ever touching the
///   segmenter itself; a genuine fraction bar (whose gap is full of numerator/denominator ink) or two
///   horizontally-separated minus signs are untouched by construction.
///   </item>
///   <item>
///   <b>\sum / \int scan.</b> Sums and integrals are classifiable but not yet semantically supported; any
///   occurrence refuses outright rather than silently flattening.
///   </item>
///   <item><b>Bracket matching.</b> A global stack scan pairs <c>( )</c> and <c>[ ]</c>; an unmatched,
///   crossed, or mismatched-type bracket refuses <see cref="ParseRefusalReason.UnmatchedBracket"/>.</item>
///   <item><b>Double-minus guard.</b> Two adjacent <c>-</c> tokens (any role) refuse
///   <see cref="ParseRefusalReason.UnsupportedNotation"/> — this ink is inherently ambiguous
///   (stray duplicate stroke, a misread <c>=</c> the rescue above didn't recover, …), not a supported
///   double-negative notation.</item>
///   <item><b>Relation split.</b> A single top-level (not inside brackets) <c>= \leq \geq \neq \lt</c> splits
///   the line into left/right recursive spans; a trailing relation (nothing after it) is a valid query with
///   a null right side; more than one relation refuses <see cref="ParseRefusalReason.UnsupportedRelation"/>.</item>
///   <item>
///   <b>Recursive span parsing.</b> Per span (recursively inside every bracket group, function argument,
///   radicand, and script): a <c>-</c>-labelled token whose X-extent spans other tokens both above AND below
///   it becomes a <see cref="FractionNode"/>, its numerator/denominator each independently and recursively
///   parsed; more than one bar candidate, or a numerator/denominator that fails to parse, refuses
///   <see cref="ParseRefusalReason.AmbiguousFractionOwnership"/>. A <c>\sqrt</c> token consumes every
///   following token whose left edge sits within its own horizontal span as its radicand (recursively
///   parsed); no such token, or a radicand that fails to parse, refuses
///   <see cref="ParseRefusalReason.EmptyRadicalOwnership"/>. Otherwise: digits/decimal points glue into one
///   number (unless the next digit is off-baseline — see the script geometry below); matched brackets nest
///   into <see cref="DelimitedGroupNode"/>; a tight run of single Latin letters spelling
///   <c>sin/cos/tan/log/ln</c> with an owned argument becomes a <see cref="FunctionCallNode"/>; consecutive
///   operand atoms with no written operator between them become an <see cref="ImplicitProductNode"/>; a
///   leading minus, or a minus right after another operator/relation/open-bracket, is unary; a binary
///   operator with no left operand, or a trailing binary operator with nothing after it, refuses
///   <see cref="ParseRefusalReason.UnsupportedNotation"/>. After building each operand atom, the token
///   immediately following it is tested for script geometry (below); a clear superscript recurses into a
///   <see cref="ScriptNode"/>, a subscript-positioned token always refuses
///   <see cref="ParseRefusalReason.GeneralSubscript"/> (Phase 5.5 has no CAS-safe subscript semantics), and a
///   margin-band offset refuses <see cref="ParseRefusalReason.UncertainScript"/> rather than guess.
///   </item>
///   <item><b>Ownership proof.</b> Every accepted tree is run through <see cref="OwnershipValidator"/>
///   against the (rewritten) token list; a violation refuses <see cref="ParseRefusalReason.LostStroke"/> or
///   <see cref="ParseRefusalReason.DoubleOwnership"/> instead of ever handing back a tree that lost or
///   double-owns a stroke. This should be unreachable if the stages above are correct — it is the safety net.</item>
/// </list>
/// <para>
/// <b>Script geometry (edge-based, not center-based).</b> A base's bounds and a candidate token's bounds are
/// compared via their EDGES against the base's vertical midpoint, not their centers: a candidate is a
/// superscript when its BOTTOM edge sits above the base's midpoint, and a subscript when its TOP edge sits
/// below it. This is deliberate: a baseline descender letter (<c>y</c>, <c>g</c>) has a low CENTER (the
/// descender loop pulls it down) but its TOP aligns with a neighbouring letter's x-height — the old
/// center-offset guard misread that as a script; the edge test does not, because the descender's top never
/// crosses the midpoint. An ascender (<c>b</c>, <c>d</c>, <c>t</c>, <c>l</c>) is handled symmetrically: its
/// BOTTOM sits on the baseline like its neighbour's, so the bottom-edge superscript test never crosses either.
/// A candidate must also be meaningfully smaller than the base (below <see cref="ScriptClearSizeRatio"/> of
/// its height) to confidently accept; a midpoint-crossing same-sized glyph is a script-versus-separate-line
/// ambiguity and refuses rather than flattening. Operator/relation/punctuation tokens
/// (<c>+ - = \times \div \leq \geq \neq \lt . , ( ) [ ]</c>) are NEVER script candidates in either role and
/// never trigger any of this — a short <c>=</c> or <c>-</c> drawn mid-line next to a tall letter is exempt by
/// construction.
/// </para>
/// <para>
/// Tokens flagged <see cref="RecognizedToken.Rejected"/> (out-of-distribution) are dropped before grammar
/// processing — they must not appear anywhere in an accepted tree (<see cref="OwnershipValidator"/>'s
/// contract) — since a rejected symbol already trips <see cref="RecognitionGate"/>'s confidence/OOD gate
/// independently of structural acceptance.
/// </para>
/// </summary>
public static class SpatialLayoutParser
{
    /// <summary>
    /// Maximum horizontal gap (as a fraction of reference height) between two glyphs for them to count as
    /// "tight" word geometry (function-name letters, or a no-parens function argument).
    /// </summary>
    private const double TightGapRatio = 0.6;

    /// <summary>
    /// Maximum confidence gap between top-1 and a same-confusion-set alternative for geometry to be allowed
    /// to override a confident top-1 read. Deliberately generous enough to let clear geometric context break
    /// a moderately-close call, but small enough that a strongly confident top-1 (e.g. 0.95 vs 0.05) is never
    /// silently overridden by a weak geometric signal.
    /// </summary>
    private const double RewriteMarginThreshold = 0.2;

    /// <summary>
    /// Maximum confidence gap for a near-exact statistical tie under NEUTRAL geometry (no digit or letter
    /// neighbour on either side) to refuse <see cref="ParseRefusalReason.LowMargin"/> rather than silently
    /// keeping top-1. Deliberately much tighter than <see cref="RewriteMarginThreshold"/> — this is the "two
    /// structurally different readings score within margin" case, not a context-assisted call.
    /// </summary>
    private const double TieMarginThreshold = 0.05;

    /// <summary>A candidate below this fraction of its base's height is confidently a script, not baseline
    /// noise — chosen so ordinary same-baseline ink with modest per-glyph size variance never qualifies,
    /// while a genuinely raised/lowered symbol (whose height is typically well under its base's) clears it
    /// comfortably.</summary>
    private const double ScriptClearSizeRatio = 0.8;

    /// <summary>Largest horizontal gap between a base and its first script glyph (and between subsequent
    /// script glyphs), relative to the base height. A farther raised glyph is a separate-line ambiguity.</summary>
    private const double ScriptMaxHorizontalGapRatio = 0.75;

    /// <summary>Minimum fraction of the narrower of a candidate fraction-bar's width and a neighbour's width
    /// that must overlap in X for the neighbour to count as sitting under/over the bar.</summary>
    private const double FractionBarOverlapRatio = 0.5;

    /// <summary>A structural fraction bar must remain visibly flat. This blocks a tall handwritten minus
    /// from acquiring numerator/denominator ownership merely because nearby ink is vertically noisy.</summary>
    private const double FractionBarAspectRatio = 3.0;

    /// <summary>Small edge tolerance for clear above/below ownership, scaled by the baseline reference
    /// height. Larger intrusions enter the ambiguity band instead of silently becoming subtraction.</summary>
    private const double FractionClearEdgeToleranceRatio = 0.05;

    /// <summary>Minimum center displacement for a vertically separated but edge-overlapping token to count
    /// as ambiguous fraction-side evidence rather than ordinary baseline jitter.</summary>
    private const double FractionAmbiguousOffsetRatio = 0.2;

    /// <summary>When several definite bars exist, a recursive outer fraction must be uniquely wider than
    /// the next bar by this factor. Similar-width candidates remain an ownership near-tie and refuse.</summary>
    private const double NestedFractionWidthMargin = 1.25;

    /// <summary>Minimum fraction of the narrower of two stacked <c>-</c> tokens' widths that must overlap in
    /// X for the equals-merge rescue to consider them the two bars of one <c>=</c>.</summary>
    private const double EqualsMergeMinXOverlapRatio = 0.6;

    /// <summary>Maximum vertical gap (as a fraction of the line's reference height) between two stacked
    /// <c>-</c> tokens for the equals-merge rescue to fire.</summary>
    private const double EqualsMergeMaxVerticalGapRatio = 0.7;

    /// <summary>Maximum height (as a fraction of the line's reference height) for a <c>-</c> token to count
    /// as "flat" — a genuine bar of a split <c>=</c>, not some taller unrelated mark.</summary>
    private const double EqualsMergeMaxHeightRatio = 0.5;

    private static readonly string[] FunctionWords = { "sin", "cos", "tan", "log", "ln" };

    /// <summary>Parses one expression candidate's tokens into a typed layout outcome.</summary>
    public static SpatialParseResult Parse(
        IReadOnlyList<RecognizedToken> tokens, IReadOnlyList<SymbolPrediction> predictions)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(predictions);
        if (tokens.Count != predictions.Count)
        {
            throw new ArgumentException(
                "tokens and predictions must be the same length (one prediction per token).",
                nameof(predictions));
        }

        if (tokens.Count == 0)
        {
            return new SpatialParseResult(
                LayoutParseOutcome.Refused(ParseRefusalReason.UnsupportedNotation, "empty candidate"),
                tokens);
        }

        (RecognizedToken[] rewritten, bool ambiguousRewrite) = ContextualRewrite(tokens, predictions);
        if (ambiguousRewrite)
        {
            return new SpatialParseResult(
                LayoutParseOutcome.Ambiguous(
                    ParseRefusalReason.LowMargin,
                    "a 0/o or 1/l reading is a near-exact tie with neutral geometry"),
                rewritten);
        }

        RecognizedToken[] merged = MergeStackedMinusIntoEquals(rewritten);

        try
        {
            var parser = new Parser(merged);
            LayoutNode root = parser.ParseLine();

            OwnershipValidationResult validation = OwnershipValidator.Validate(root, merged);
            if (!validation.IsValid)
            {
                OwnershipViolation violation = validation.Violations[0];
                return new SpatialParseResult(
                    LayoutParseOutcome.Refused(MapViolation(violation.Kind), violation.Detail),
                    merged);
            }

            return new SpatialParseResult(LayoutParseOutcome.Accepted(root), merged);
        }
        catch (LayoutRefusalException refusal)
        {
            return new SpatialParseResult(
                LayoutParseOutcome.Refused(refusal.Reason, refusal.Detail),
                merged);
        }
    }

    private static ParseRefusalReason MapViolation(OwnershipViolationKind kind) => kind switch
    {
        OwnershipViolationKind.LostToken => ParseRefusalReason.LostStroke,
        _ => ParseRefusalReason.DoubleOwnership,
    };

    // ---- Stage 0: contextual rewrite --------------------------------------------------------------------

    private enum ContextVerdict
    {
        PrefersDigit,
        PrefersLetter,
        Neutral,
    }

    private static (RecognizedToken[] Rewritten, bool Ambiguous) ContextualRewrite(
        IReadOnlyList<RecognizedToken> tokens, IReadOnlyList<SymbolPrediction> predictions)
    {
        int n = tokens.Count;
        var originalLabels = new string[n];
        for (int i = 0; i < n; i++)
        {
            originalLabels[i] = tokens[i].Latex;
        }

        var finalLabels = (string[])originalLabels.Clone();
        bool ambiguous = false;

        for (int i = 0; i < n; i++)
        {
            string label = originalLabels[i];

            // Unconditional: '|' has no valid reading in this grammar, always a drawn '1'.
            if (label == "|")
            {
                finalLabels[i] = "1";
                continue;
            }

            // Structural (unchanged from the pre-Phase-5.5 recognizer, now geometry-guarded): 'x' strictly
            // between two SAME-BASELINE digits is the multiplication cross, not the variable. A digit that
            // is actually a raised/lowered script (e.g. the exponent in "2x^2") must not count as a flanking
            // digit for this rule — "2x^2" is a coefficient times a scripted variable, not "2×2".
            if (label == "x" && i > 0 && i < n - 1 && IsAsciiDigit(originalLabels[i - 1]) && IsAsciiDigit(originalLabels[i + 1])
                && !IsOffBaseline(tokens[i].Bounds, tokens[i - 1].Bounds)
                && !IsOffBaseline(tokens[i].Bounds, tokens[i + 1].Bounds))
            {
                finalLabels[i] = @"\times";
                continue;
            }

            string? pairOther = ConfusionPairOther(label);
            if (pairOther is null)
            {
                continue;
            }

            SymbolAlternative? alternative = FindAlternative(predictions[i].Alternatives, pairOther);
            if (alternative is null)
            {
                continue;   // no competing evidence for the other reading — keep top-1.
            }

            double margin = Math.Max(0, predictions[i].Confidence - alternative.Value.Confidence);
            ContextVerdict verdict = ClassifyContext(originalLabels, i);
            string digitMember = IsAsciiDigit(label) ? label : pairOther;
            string letterMember = digitMember == label ? pairOther : label;
            string? preferred = verdict switch
            {
                ContextVerdict.PrefersDigit => digitMember,
                ContextVerdict.PrefersLetter => letterMember,
                _ => null,
            };

            if (preferred == label)
            {
                continue;   // context agrees with top-1.
            }

            if (preferred == pairOther)
            {
                if (margin <= RewriteMarginThreshold)
                {
                    finalLabels[i] = pairOther;
                }
                continue;   // else: context disagrees but not strongly enough to override a confident top-1.
            }

            // Neutral geometry: only a near-exact tie is worth refusing over.
            if (margin <= TieMarginThreshold)
            {
                ambiguous = true;
            }
        }

        var rewritten = new RecognizedToken[n];
        for (int i = 0; i < n; i++)
        {
            rewritten[i] = finalLabels[i] == originalLabels[i]
                ? tokens[i]
                : tokens[i] with { Latex = finalLabels[i] };
        }

        return (rewritten, ambiguous);
    }

    private static string? ConfusionPairOther(string label) => label switch
    {
        "0" => "o",
        "o" => "0",
        "1" => "l",
        "l" => "1",
        _ => null,
    };

    private static SymbolAlternative? FindAlternative(IReadOnlyList<SymbolAlternative>? alternatives, string label)
    {
        if (alternatives is null)
        {
            return null;
        }

        foreach (SymbolAlternative alternative in alternatives)
        {
            if (string.Equals(alternative.Label, label, StringComparison.Ordinal))
            {
                return alternative;
            }
        }

        return null;
    }

    private static ContextVerdict ClassifyContext(IReadOnlyList<string> originalLabels, int i)
    {
        bool leftDigit = i > 0 && IsAsciiDigit(originalLabels[i - 1]);
        bool rightDigit = i < originalLabels.Count - 1 && IsAsciiDigit(originalLabels[i + 1]);
        bool leftLetter = i > 0 && IsLetterLike(originalLabels[i - 1]);
        bool rightLetter = i < originalLabels.Count - 1 && IsLetterLike(originalLabels[i + 1]);

        bool digitSignal = leftDigit || rightDigit;
        bool letterSignal = leftLetter || rightLetter;

        if (digitSignal && !letterSignal)
        {
            return ContextVerdict.PrefersDigit;
        }

        if (letterSignal && !digitSignal)
        {
            return ContextVerdict.PrefersLetter;
        }

        return ContextVerdict.Neutral;
    }

    private static bool IsAsciiDigit(string label) => label.Length == 1 && char.IsAsciiDigit(label[0]);

    private static bool IsLetterLike(string label) =>
        (label.Length == 1 && char.IsAsciiLetter(label[0])) || label is @"\pi" or @"\theta" or @"\alpha";

    private static bool IsNumberGlueEligible(string label) => IsAsciiDigit(label) || label == ".";

    // ---- Stage 0b: equals-merge rescue -------------------------------------------------------------------

    /// <summary>
    /// Two adjacent tokens both labelled <c>-</c> that look like the split halves of one hand-drawn <c>=</c>
    /// (stacked, close, flat, nothing else sitting between them) are rewritten into a single <c>=</c> token
    /// owning both stroke sets. Runs once over the whole (already Stage-0-rewritten) token list, left to
    /// right, consuming a merged pair as one unit.
    /// </summary>
    private static RecognizedToken[] MergeStackedMinusIntoEquals(RecognizedToken[] tokens)
    {
        if (tokens.Length < 2)
        {
            return tokens;
        }

        double refHeight = ComputeBaselineRefHeight(tokens);
        var result = new List<RecognizedToken>(tokens.Length);
        int i = 0;
        while (i < tokens.Length)
        {
            if (i + 1 < tokens.Length && CanMergeIntoEquals(tokens, i, i + 1, refHeight))
            {
                RecognizedToken a = tokens[i];
                RecognizedToken b = tokens[i + 1];
                InkBounds union = UnionBounds(a.Bounds, b.Bounds);
                List<Guid> mergedIds = a.SourceStrokeIds.Concat(b.SourceStrokeIds).ToList();
                double confidence = Math.Min(a.Confidence, b.Confidence);
                result.Add(new RecognizedToken(
                    "=", mergedIds, union, confidence, Rejected: a.Rejected || b.Rejected));
                i += 2;
                continue;
            }

            result.Add(tokens[i]);
            i++;
        }

        return result.ToArray();
    }

    private static bool CanMergeIntoEquals(RecognizedToken[] tokens, int i, int j, double refHeight)
    {
        RecognizedToken a = tokens[i];
        RecognizedToken b = tokens[j];
        if (a.Latex != "-" || b.Latex != "-")
        {
            return false;
        }


        // Fraction ownership is stronger structural evidence than a two-minus rescue. In particular, a
        // numerator subtraction stroke can be X-adjacent to the actual fraction bar after reading-order
        // sorting; never consume either stroke when one already spans content on both vertical sides.
        if (AnalyzeFractionBar(tokens, i, 0, tokens.Length, refHeight).HasTwoSidedEvidence
            || AnalyzeFractionBar(tokens, j, 0, tokens.Length, refHeight).HasTwoSidedEvidence)
        {
            return false;
        }

        if (a.Bounds.Height > EqualsMergeMaxHeightRatio * refHeight
            || b.Bounds.Height > EqualsMergeMaxHeightRatio * refHeight)
        {
            return false;   // not flat enough to be a bare bar of a split '='.
        }

        double overlap = Math.Min(a.Bounds.X + a.Bounds.Width, b.Bounds.X + b.Bounds.Width)
            - Math.Max(a.Bounds.X, b.Bounds.X);
        double narrower = Math.Min(a.Bounds.Width, b.Bounds.Width);
        if (narrower <= 0 || overlap / narrower < EqualsMergeMinXOverlapRatio)
        {
            return false;
        }

        double gap = Math.Max(0, Math.Max(a.Bounds.Y, b.Bounds.Y)
            - Math.Min(a.Bounds.Y + a.Bounds.Height, b.Bounds.Y + b.Bounds.Height));
        if (gap > EqualsMergeMaxVerticalGapRatio * refHeight)
        {
            return false;
        }

        return !HasContentBetween(tokens, a, b);
    }

    /// <summary>True when some OTHER token's ink sits in the horizontal/vertical gap between <paramref
    /// name="a"/> and <paramref name="b"/> — the safeguard that keeps a genuine fraction bar (whose gap is
    /// full of numerator/denominator ink) from ever being eaten by the equals-merge rescue.</summary>
    private static bool HasContentBetween(RecognizedToken[] tokens, RecognizedToken a, RecognizedToken b)
    {
        double gapTop = Math.Min(a.Bounds.Y + a.Bounds.Height, b.Bounds.Y + b.Bounds.Height);
        double gapBottom = Math.Max(a.Bounds.Y, b.Bounds.Y);
        double left = Math.Max(a.Bounds.X, b.Bounds.X);
        double right = Math.Min(a.Bounds.X + a.Bounds.Width, b.Bounds.X + b.Bounds.Width);

        foreach (RecognizedToken t in tokens)
        {
            if (ReferenceEquals(t, a) || ReferenceEquals(t, b))
            {
                continue;
            }

            bool verticallyBetween = t.Bounds.Y < gapBottom && t.Bounds.Y + t.Bounds.Height > gapTop;
            bool horizontallyOverlaps = t.Bounds.X < right && t.Bounds.X + t.Bounds.Width > left;
            if (verticallyBetween && horizontallyOverlaps)
            {
                return true;
            }
        }

        return false;
    }

    private static InkBounds UnionBounds(InkBounds a, InkBounds b)
    {
        double xMin = Math.Min(a.X, b.X);
        double yMin = Math.Min(a.Y, b.Y);
        double xMax = Math.Max(a.X + a.Width, b.X + b.Width);
        double yMax = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new InkBounds(xMin, yMin, xMax - xMin, yMax - yMin);
    }

    private static double ComputeRefHeight(IReadOnlyList<RecognizedToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return 1.0;
        }

        double[] heights = tokens.Select(t => t.Bounds.Height).OrderBy(h => h).ToArray();
        int mid = heights.Length / 2;
        double median = heights.Length % 2 == 1
            ? heights[mid]
            : (heights[mid - 1] + heights[mid]) / 2.0;
        return median > 0 ? median : 1.0;
    }

    /// <summary>
    /// Structural bars must not define their own scale. Prefer non-minus glyph heights for the equals and
    /// fraction bands; fall back to the ordinary all-token median only for an all-bar candidate.
    /// </summary>
    private static double ComputeBaselineRefHeight(IReadOnlyList<RecognizedToken> tokens)
    {
        RecognizedToken[] baseline = tokens
            .Where(token => token.Latex != "-" && token.Bounds.Height > 0)
            .ToArray();
        return baseline.Length > 0 ? ComputeRefHeight(baseline) : ComputeRefHeight(tokens);
    }

    private enum FractionSide
    {
        None,
        ClearAbove,
        ClearBelow,
        AmbiguousAbove,
        AmbiguousBelow,
    }

    private sealed record FractionEvidence(
        List<int> ClearAbove,
        List<int> ClearBelow,
        List<int> AmbiguousAbove,
        List<int> AmbiguousBelow)
    {
        public bool IsDefinite => ClearAbove.Count > 0 && ClearBelow.Count > 0;

        public bool HasTwoSidedEvidence =>
            (ClearAbove.Count > 0 || AmbiguousAbove.Count > 0)
            && (ClearBelow.Count > 0 || AmbiguousBelow.Count > 0);

        public bool IsNearTie => HasTwoSidedEvidence && !IsDefinite;
    }

    private static FractionEvidence AnalyzeFractionBar(
        IReadOnlyList<RecognizedToken> tokens, int barIndex, int start, int end, double refHeight)
    {
        var clearAbove = new List<int>();
        var clearBelow = new List<int>();
        var ambiguousAbove = new List<int>();
        var ambiguousBelow = new List<int>();
        RecognizedToken bar = tokens[barIndex];

        bool flat = bar.Latex == "-"
            && bar.Bounds.Height >= 0
            && bar.Bounds.Width >= Math.Max(1.0, bar.Bounds.Height) * FractionBarAspectRatio;
        if (!flat)
        {
            return new FractionEvidence(clearAbove, clearBelow, ambiguousAbove, ambiguousBelow);
        }

        double barMid = bar.Bounds.Y + bar.Bounds.Height / 2.0;
        double edgeTolerance = FractionClearEdgeToleranceRatio * refHeight;
        double ambiguousOffset = FractionAmbiguousOffsetRatio * refHeight;

        for (int i = start; i < end; i++)
        {
            if (i == barIndex || !OverlapsXByNarrower(bar.Bounds, tokens[i].Bounds, FractionBarOverlapRatio))
            {
                continue;
            }

            InkBounds other = tokens[i].Bounds;
            double otherMid = other.Y + other.Height / 2.0;
            FractionSide side;
            if (otherMid < barMid)
            {
                side = other.Y + other.Height <= bar.Bounds.Y + edgeTolerance
                    ? FractionSide.ClearAbove
                    : barMid - otherMid >= ambiguousOffset
                        ? FractionSide.AmbiguousAbove
                        : FractionSide.None;
            }
            else if (otherMid > barMid)
            {
                side = other.Y >= bar.Bounds.Y + bar.Bounds.Height - edgeTolerance
                    ? FractionSide.ClearBelow
                    : otherMid - barMid >= ambiguousOffset
                        ? FractionSide.AmbiguousBelow
                        : FractionSide.None;
            }
            else
            {
                side = FractionSide.None;
            }

            switch (side)
            {
                case FractionSide.ClearAbove:
                    clearAbove.Add(i);
                    break;
                case FractionSide.ClearBelow:
                    clearBelow.Add(i);
                    break;
                case FractionSide.AmbiguousAbove:
                    ambiguousAbove.Add(i);
                    break;
                case FractionSide.AmbiguousBelow:
                    ambiguousBelow.Add(i);
                    break;
            }
        }

        return new FractionEvidence(clearAbove, clearBelow, ambiguousAbove, ambiguousBelow);
    }

    private static bool OverlapsXByNarrower(InkBounds a, InkBounds b, double requiredRatio)
    {
        double overlap = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        double narrower = Math.Min(a.Width, b.Width);
        return narrower > 0 && Math.Max(0, overlap) / narrower >= requiredRatio;
    }

    // ---- script geometry (shared by the vertical guard and the recursive superscript builder) -------------

    private enum ScriptBand
    {
        None,
        Margin,
        Clear,
    }

    /// <summary>
    /// Edge-based (not center-based) script test: a candidate is a superscript when its BOTTOM edge sits
    /// above the base's vertical midpoint, and a subscript when its TOP edge sits below it. A descender's
    /// low center never trips this (its top still aligns with the base); an ascender's high center never
    /// trips it either (its bottom still sits on the base's baseline). Also requires the candidate to be
    /// meaningfully smaller than the base — the accept/margin/none size bands are what separate a confident
    /// script from a genuinely uncertain one from plain same-size baseline adjacency.
    /// </summary>
    private static ScriptBand ClassifyScriptBand(InkBounds baseBounds, InkBounds candidateBounds, bool superscript)
    {
        if (baseBounds.Height <= 0)
        {
            return ScriptBand.None;
        }

        double baseMidpoint = baseBounds.Y + baseBounds.Height / 2.0;
        bool crosses = superscript
            ? candidateBounds.Y + candidateBounds.Height < baseMidpoint
            : candidateBounds.Y > baseMidpoint;
        if (!crosses)
        {
            return ScriptBand.None;
        }

        double sizeRatio = candidateBounds.Height / baseBounds.Height;
        if (sizeRatio < ScriptClearSizeRatio)
        {
            return ScriptBand.Clear;
        }

        return ScriptBand.Margin;
    }

    /// <summary>
    /// Operators, relations, and punctuation are never script candidates in either role (base or candidate)
    /// and never trigger any script geometry test — a short <c>=</c> or <c>-</c> naturally sits mid-line next
    /// to a tall letter.
    /// </summary>
    private static bool IsScriptExempt(string label) => label is
        "+" or "-" or "=" or @"\times" or @"\div" or @"\leq" or @"\geq" or @"\neq" or @"\lt"
        or "." or "," or "(" or ")" or "[" or "]";

    private static bool IsOffBaseline(InkBounds anchor, InkBounds candidate) =>
        ClassifyScriptBand(anchor, candidate, superscript: true) != ScriptBand.None
        || ClassifyScriptBand(anchor, candidate, superscript: false) != ScriptBand.None;

    // ---- refusal signal ----------------------------------------------------------------------------------

    private sealed class LayoutRefusalException : Exception
    {
        public LayoutRefusalException(ParseRefusalReason reason, string? detail)
            : base(detail)
        {
            Reason = reason;
            Detail = detail;
        }

        public ParseRefusalReason Reason { get; }

        public string? Detail { get; }
    }

    // ---- atoms ---------------------------------------------------------------------------------------------

    private readonly record struct Atom(bool IsOperator, LayoutNode? Node, RecognizedToken? OperatorToken);

    private static Atom Operand(LayoutNode node) => new(false, node, null);

    private static Atom OperatorAtom(RecognizedToken token) => new(true, null, token);

    /// <summary>A resolved fraction structure for one span: the bar's index, the token indices its
    /// numerator/denominator consumed (so the main atom scan skips them), and the already-recursively-parsed
    /// numerator/denominator trees.</summary>
    private sealed record FractionPlan(
        int BarIndex, HashSet<int> AboveIndices, HashSet<int> BelowIndices,
        LayoutNode Numerator, LayoutNode Denominator);

    // ---- stages 3-7: bracket matching, double-minus guard, relation split, and recursive span parsing -----

    private sealed class Parser
    {
        private readonly RecognizedToken[] _tokens;   // non-rejected working tokens, in candidate order
        private readonly int[] _partner;
        private readonly double _refHeight;

        public Parser(IReadOnlyList<RecognizedToken> allTokens)
        {
            _tokens = allTokens.Where(token => !token.Rejected).ToArray();
            _refHeight = ComputeRefHeight(_tokens);

            RunSpecialTokenScan();
            _partner = BuildBracketPartners();
            RunDoubleMinusGuard();
        }

        public LayoutNode ParseLine()
        {
            int n = _tokens.Length;
            int relationIndex = FindTopLevelRelation();
            if (relationIndex < 0)
            {
                return ParseSpan(0, n);
            }

            LayoutNode left = ParseSpan(0, relationIndex);
            LayoutNode? right = relationIndex + 1 < n ? ParseSpan(relationIndex + 1, n) : null;
            return new RelationNode(left, _tokens[relationIndex], right);
        }

        private void RunSpecialTokenScan()
        {
            for (int i = 0; i < _tokens.Length; i++)
            {
                RecognizedToken token = _tokens[i];
                if (token.Latex is @"\sum" or @"\int")
                {
                    throw new LayoutRefusalException(
                        ParseRefusalReason.UnsupportedNotation,
                        $"{token.Latex} is classifiable but not yet semantically supported");
                }

                if (token.Latex == @"\sqrt" && i > 0 && LooksLikeUnsupportedRootIndex(_tokens[i - 1], token))
                {
                    throw new LayoutRefusalException(
                        ParseRefusalReason.EmptyRadicalOwnership,
                        "a raised root-index candidate is not supported safely in this phase");
                }
            }
        }

        private static bool LooksLikeUnsupportedRootIndex(RecognizedToken candidate, RecognizedToken radical)
        {
            double radicalMid = radical.Bounds.Y + radical.Bounds.Height / 2.0;
            bool raised = candidate.Bounds.Y + candidate.Bounds.Height < radicalMid;
            bool smaller = radical.Bounds.Height > 0
                && candidate.Bounds.Height < radical.Bounds.Height * ScriptClearSizeRatio;
            bool nearRootCorner = candidate.Bounds.X < radical.Bounds.X + radical.Bounds.Width * 0.35
                && candidate.Bounds.X + candidate.Bounds.Width <= radical.Bounds.X + radical.Bounds.Width * 0.5;
            return raised && smaller && nearRootCorner;
        }

        private int[] BuildBracketPartners()
        {
            int n = _tokens.Length;
            var partner = new int[n];
            Array.Fill(partner, -1);
            var stack = new Stack<(int Index, string Label)>();
            for (int i = 0; i < n; i++)
            {
                string label = _tokens[i].Latex;
                if (label is "(" or "[")
                {
                    stack.Push((i, label));
                }
                else if (label is ")" or "]")
                {
                    string expectedOpen = label == ")" ? "(" : "[";
                    if (stack.Count == 0)
                    {
                        throw new LayoutRefusalException(
                            ParseRefusalReason.UnmatchedBracket, "a closing bracket has no opener");
                    }

                    (int openIndex, string openLabel) = stack.Pop();
                    if (openLabel != expectedOpen)
                    {
                        throw new LayoutRefusalException(
                            ParseRefusalReason.UnmatchedBracket, "brackets cross or mismatch type");
                    }

                    partner[openIndex] = i;
                    partner[i] = openIndex;
                }
            }

            if (stack.Count > 0)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.UnmatchedBracket, "an opening bracket has no closer");
            }

            return partner;
        }

        private void RunDoubleMinusGuard()
        {
            for (int i = 0; i < _tokens.Length - 1; i++)
            {
                if (_tokens[i].Latex == "-" && _tokens[i + 1].Latex == "-")
                {
                    bool structuralBar = AnalyzeFractionBar(
                            _tokens, i, 0, _tokens.Length, _refHeight).IsDefinite
                        || AnalyzeFractionBar(
                            _tokens, i + 1, 0, _tokens.Length, _refHeight).IsDefinite;
                    if (structuralBar)
                    {
                        continue;
                    }

                    throw new LayoutRefusalException(
                        ParseRefusalReason.UnsupportedNotation, "two adjacent minus signs are not supported");
                }
            }
        }

        private static bool IsRelation(string label) => label is "=" or @"\leq" or @"\geq" or @"\neq" or @"\lt";

        private int FindTopLevelRelation()
        {
            int found = -1;
            int i = 0;
            int n = _tokens.Length;
            while (i < n)
            {
                string label = _tokens[i].Latex;
                if (label is "(" or "[")
                {
                    i = _partner[i] + 1;   // skip the whole nested group — a relation inside brackets isn't top-level.
                    continue;
                }

                if (IsRelation(label))
                {
                    if (found >= 0)
                    {
                        throw new LayoutRefusalException(
                            ParseRefusalReason.UnsupportedRelation, "more than one relation on the line");
                    }

                    found = i;
                }

                i++;
            }

            return found;
        }

        // ---- fraction-bar detection (runs once per span, before the linear atom scan) ---------------------

        private FractionPlan? TryBuildFractionPlan(int start, int end)
        {
            var candidates = new List<int>();
            bool nearTie = false;
            for (int k = start; k < end; k++)
            {
                if (_tokens[k].Latex != "-")
                {
                    continue;
                }

                FractionEvidence evidence = AnalyzeFractionBar(_tokens, k, start, end, _refHeight);
                if (evidence.IsDefinite)
                {
                    candidates.Add(k);
                }
                else if (evidence.IsNearTie)
                {
                    nearTie = true;
                }
            }

            if (nearTie)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.AmbiguousFractionOwnership,
                    "a minus/fraction-bar candidate sits inside the ownership margin");
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            int barIndex = SelectOuterFractionBar(candidates, start, end);
            FractionEvidence accepted = AnalyzeFractionBar(_tokens, barIndex, start, end, _refHeight);
            List<int> aboveIdx = accepted.ClearAbove;
            List<int> belowIdx = accepted.ClearBelow;

            List<RecognizedToken> numeratorTokens = aboveIdx
                .OrderBy(idx => _tokens[idx].Bounds.X)
                .Select(idx => _tokens[idx])
                .ToList();
            List<RecognizedToken> denominatorTokens = belowIdx
                .OrderBy(idx => _tokens[idx].Bounds.X)
                .Select(idx => _tokens[idx])
                .ToList();

            LayoutNode numerator;
            LayoutNode denominator;
            try
            {
                numerator = new Parser(numeratorTokens.ToArray()).ParseSpan(0, numeratorTokens.Count);
                denominator = new Parser(denominatorTokens.ToArray()).ParseSpan(0, denominatorTokens.Count);
            }
            catch (LayoutRefusalException)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.AmbiguousFractionOwnership,
                    "a fraction's numerator or denominator did not parse cleanly");
            }

            return new FractionPlan(barIndex, new HashSet<int>(aboveIdx), new HashSet<int>(belowIdx), numerator, denominator);
        }

        private int SelectOuterFractionBar(IReadOnlyList<int> candidates, int start, int end)
        {
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            int[] byWidth = candidates
                .OrderByDescending(index => _tokens[index].Bounds.Width)
                .ToArray();
            int widest = byWidth[0];
            double runnerUpWidth = _tokens[byWidth[1]].Bounds.Width;
            if (runnerUpWidth <= 0
                || _tokens[widest].Bounds.Width < runnerUpWidth * NestedFractionWidthMargin)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.AmbiguousFractionOwnership,
                    "multiple similarly-sized fraction bars compete for ownership");
            }

            FractionEvidence outer = AnalyzeFractionBar(_tokens, widest, start, end, _refHeight);
            var ownedSides = new HashSet<int>(outer.ClearAbove.Concat(outer.ClearBelow));
            InkBounds outerBounds = _tokens[widest].Bounds;
            foreach (int nested in byWidth.Skip(1))
            {
                InkBounds nestedBounds = _tokens[nested].Bounds;
                bool containedInX = nestedBounds.X >= outerBounds.X
                    && nestedBounds.X + nestedBounds.Width <= outerBounds.X + outerBounds.Width;
                if (!containedInX || !ownedSides.Contains(nested))
                {
                    throw new LayoutRefusalException(
                        ParseRefusalReason.AmbiguousFractionOwnership,
                        "multiple fraction bars are not cleanly nested under one outer owner");
                }
            }

            return widest;
        }

        private static bool IsFractionOwned(FractionPlan? fraction, int index) =>
            fraction is not null
            && (fraction.BarIndex == index
                || fraction.AboveIndices.Contains(index)
                || fraction.BelowIndices.Contains(index));

        // ---- radical consumption -----------------------------------------------------------------------

        private (LayoutNode Node, int NextIndex) ConsumeRadical(int i, int end, FractionPlan? fraction)
        {
            RecognizedToken sqrtToken = _tokens[i];
            double reach = sqrtToken.Bounds.X + sqrtToken.Bounds.Width;
            int j = i + 1;
            while (j < end && !IsFractionOwned(fraction, j) && IsWithinRadicalSpan(sqrtToken, _tokens[j]))
            {
                j++;
            }

            if (j < end
                && !IsFractionOwned(fraction, j)
                && _tokens[j].Bounds.X < reach
                && !IsRelation(_tokens[j].Latex))
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.EmptyRadicalOwnership,
                    "a radicand token crosses the radical's horizontal ownership boundary");
            }

            if (j == i + 1)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.EmptyRadicalOwnership,
                    "a radical has no radicand token within its horizontal span");
            }

            LayoutNode radicand;
            try
            {
                radicand = ParseSpan(i + 1, j);
            }
            catch (LayoutRefusalException)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.EmptyRadicalOwnership,
                    "a radical's radicand did not parse cleanly");
            }

            return (new RadicalNode(radicand, rootIndex: null, sqrtToken), j);
        }

        private bool IsWithinRadicalSpan(RecognizedToken radical, RecognizedToken candidate)
        {
            double tolerance = 0.1 * _refHeight;
            return candidate.Bounds.X >= radical.Bounds.X - tolerance
                && candidate.Bounds.X + candidate.Bounds.Width <=
                    radical.Bounds.X + radical.Bounds.Width + tolerance;
        }

        // ---- superscript/subscript attachment ------------------------------------------------------------

        /// <summary>
        /// After an operand atom is built spanning <c>[atomStart, atomEnd)</c>, tests the token right after
        /// it for script geometry against the atom's own union bounds. A clear superscript consumes a maximal
        /// run of further clear-band (or already-consumed-by-fraction-boundary-respecting) tokens and
        /// recurses into a <see cref="ScriptNode"/>; a subscript-positioned token always refuses
        /// <see cref="ParseRefusalReason.GeneralSubscript"/>; a margin-band offset refuses
        /// <see cref="ParseRefusalReason.UncertainScript"/>; anything else (exempt, out of span, flat,
        /// same-size) leaves the atom unchanged.
        /// </summary>
        private (LayoutNode Node, int NextIndex) TryAttachScript(
            LayoutNode baseNode, int atomStart, int atomEnd, int spanEnd, FractionPlan? fraction)
        {
            if (atomEnd >= spanEnd || IsFractionOwned(fraction, atomEnd))
            {
                return (baseNode, atomEnd);
            }

            string nextLabel = _tokens[atomEnd].Latex;
            if (IsScriptExempt(nextLabel))
            {
                return (baseNode, atomEnd);
            }

            InkBounds anchor = UnionBounds(atomStart, atomEnd);
            InkBounds candidateBounds = _tokens[atomEnd].Bounds;

            ScriptBand superBand = ClassifyScriptBand(anchor, candidateBounds, superscript: true);
            ScriptBand subBand = ClassifyScriptBand(anchor, candidateBounds, superscript: false);

            if (superBand == ScriptBand.None && subBand == ScriptBand.None)
            {
                return (baseNode, atomEnd);
            }

            if (HorizontalGap(anchor, candidateBounds) > ScriptMaxHorizontalGapRatio * anchor.Height)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.UncertainScript,
                    "a vertically displaced glyph is too far from its possible script base");
            }

            if (subBand == ScriptBand.Margin)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.UncertainScript,
                    "a lowered token's size/offset is too close to call between plain baseline text and a subscript");
            }

            if (subBand == ScriptBand.Clear)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.GeneralSubscript,
                    "a subscript-positioned token is not semantically supported in this phase");
            }

            if (superBand == ScriptBand.Margin)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.UncertainScript,
                    "a raised token's size/offset is too close to call between plain baseline text and a script");
            }

            int j = atomEnd + 1;
            while (j < spanEnd && !IsFractionOwned(fraction, j))
            {
                ScriptBand continuation = ClassifyScriptBand(
                    anchor, _tokens[j].Bounds, superscript: true);
                ScriptBand lowered = ClassifyScriptBand(
                    anchor, _tokens[j].Bounds, superscript: false);
                if (continuation == ScriptBand.None && lowered == ScriptBand.None)
                {
                    break;
                }

                if (continuation != ScriptBand.Clear
                    || HorizontalGap(_tokens[j - 1].Bounds, _tokens[j].Bounds)
                        > ScriptMaxHorizontalGapRatio * anchor.Height)
                {
                    throw new LayoutRefusalException(
                        ParseRefusalReason.UncertainScript,
                        "a possible multi-glyph script leaves the clear ownership band");
                }

                j++;
            }

            LayoutNode superscript;
            try
            {
                superscript = ParseSpan(atomEnd, j);
            }
            catch (LayoutRefusalException)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.UncertainScript, "a superscript's own content did not parse cleanly");
            }

            return (new ScriptNode(baseNode, superscript, subscript: null), j);
        }

        private static double HorizontalGap(InkBounds left, InkBounds right) =>
            Math.Max(0, right.X - (left.X + left.Width));

        private InkBounds UnionBounds(int start, int end)
        {
            double xMin = double.PositiveInfinity, yMin = double.PositiveInfinity;
            double xMax = double.NegativeInfinity, yMax = double.NegativeInfinity;
            for (int k = start; k < end; k++)
            {
                InkBounds b = _tokens[k].Bounds;
                xMin = Math.Min(xMin, b.X);
                yMin = Math.Min(yMin, b.Y);
                xMax = Math.Max(xMax, b.X + b.Width);
                yMax = Math.Max(yMax, b.Y + b.Height);
            }

            return new InkBounds(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        // ---- span parsing ---------------------------------------------------------------------------------

        private LayoutNode ParseSpan(int start, int end)
        {
            FractionPlan? fraction = TryBuildFractionPlan(start, end);
            List<Atom> atoms = BuildAtoms(start, end, fraction);
            return ClusterAtoms(atoms);
        }

        private List<Atom> BuildAtoms(int start, int end, FractionPlan? fraction)
        {
            var atoms = new List<Atom>();
            int i = start;
            while (i < end)
            {
                if (fraction is not null && fraction.BarIndex == i)
                {
                    atoms.Add(Operand(new FractionNode(fraction.Numerator, fraction.Denominator, _tokens[i])));
                    i++;
                    continue;
                }

                if (fraction is not null && (fraction.AboveIndices.Contains(i) || fraction.BelowIndices.Contains(i)))
                {
                    i++;   // already owned by the fraction plan's numerator/denominator sub-parse.
                    continue;
                }

                string label = _tokens[i].Latex;

                if (label == @"\sqrt")
                {
                    (LayoutNode radicalNode, int radicalNext) = ConsumeRadical(i, end, fraction);
                    (LayoutNode attached, int attachedNext) = TryAttachScript(radicalNode, i, radicalNext, end, fraction);
                    atoms.Add(Operand(attached));
                    i = attachedNext;
                    continue;
                }

                if (IsNumberGlueEligible(label))
                {
                    int j = i;
                    var run = new List<RecognizedToken>();
                    while (j < end && IsNumberGlueEligible(_tokens[j].Latex) && !IsFractionOwned(fraction, j))
                    {
                        bool isDecimalPoint = _tokens[j].Latex == ".";
                        if (run.Count > 0 && !isDecimalPoint && IsOffBaseline(run[0].Bounds, _tokens[j].Bounds))
                        {
                            break;   // an off-baseline digit is a script (exponent/subscript), not this number.
                        }

                        run.Add(_tokens[j]);
                        j++;
                    }

                    LayoutNode numberNode = BuildNumberNode(run);
                    (LayoutNode attached, int attachedNext) = TryAttachScript(numberNode, i, j, end, fraction);
                    atoms.Add(Operand(attached));
                    i = attachedNext;
                    continue;
                }

                if (label is "(" or "[")
                {
                    int close = _partner[i];
                    if (close == i + 1)
                    {
                        throw new LayoutRefusalException(
                            ParseRefusalReason.UnsupportedNotation, "an empty bracket group has no content");
                    }

                    LayoutNode inner = ParseSpan(i + 1, close);
                    LayoutNode groupNode = new DelimitedGroupNode(inner, _tokens[i], _tokens[close]);
                    (LayoutNode attached, int attachedNext) = TryAttachScript(groupNode, i, close + 1, end, fraction);
                    atoms.Add(Operand(attached));
                    i = attachedNext;
                    continue;
                }

                if (label is ")" or "]")
                {
                    // Unreachable given a valid global partner map (every closer is skipped-to via its
                    // opener above); kept as a defensive refusal rather than an index crash.
                    throw new LayoutRefusalException(
                        ParseRefusalReason.UnmatchedBracket, "encountered a closing bracket out of context");
                }

                if (label is "+" or "-" or @"\times" or @"\div" or "/")
                {
                    atoms.Add(OperatorAtom(_tokens[i]));
                    i++;
                    continue;
                }

                if (IsSingleLatinLetter(label))
                {
                    (int consumed, string? functionName) = TryMatchFunctionWord(i, end);
                    if (functionName is not null)
                    {
                        (LayoutNode funcNode, int funcNext) = ConsumeFunctionCall(i, consumed, functionName, end);
                        (LayoutNode attached, int attachedNext) = TryAttachScript(funcNode, i, funcNext, end, fraction);
                        atoms.Add(Operand(attached));
                        i = attachedNext;
                        continue;
                    }

                    LayoutNode leafNode = new LeafNode(_tokens[i]);
                    (LayoutNode attachedLeaf, int attachedLeafNext) = TryAttachScript(leafNode, i, i + 1, end, fraction);
                    atoms.Add(Operand(attachedLeaf));
                    i = attachedLeafNext;
                    continue;
                }

                // Variable-like control words (\pi, \theta, \alpha) and any other unmodeled glyph: a plain
                // leaf operand — the safest non-crashing default outside the mandated grammar surface.
                LayoutNode fallbackLeaf = new LeafNode(_tokens[i]);
                (LayoutNode attachedFallback, int attachedFallbackNext) =
                    TryAttachScript(fallbackLeaf, i, i + 1, end, fraction);
                atoms.Add(Operand(attachedFallback));
                i = attachedFallbackNext;
            }

            return atoms;
        }

        private (LayoutNode Node, int NextIndex) ConsumeFunctionCall(
            int start, int nameLength, string functionName, int end)
        {
            var nameTokens = new List<RecognizedToken>(nameLength + 2);
            for (int k = start; k < start + nameLength; k++)
            {
                nameTokens.Add(_tokens[k]);
            }

            int argStart = start + nameLength;
            LayoutNode argument;
            int next;
            if (argStart < end && _tokens[argStart].Latex is "(" or "[")
            {
                int close = _partner[argStart];
                if (close == argStart + 1)
                {
                    throw new LayoutRefusalException(
                        ParseRefusalReason.UnsupportedNotation, "a function's argument group is empty");
                }

                argument = ParseSpan(argStart + 1, close);
                nameTokens.Add(_tokens[argStart]);
                nameTokens.Add(_tokens[close]);
                next = close + 1;
            }
            else if (argStart < end
                     && IsTight(_tokens[start + nameLength - 1], _tokens[argStart])
                     && IsArgumentStart(_tokens[argStart].Latex))
            {
                (LayoutNode argNode, int argEnd) = BuildSingleOperandAtom(argStart, end);
                argument = argNode;
                next = argEnd;
            }
            else
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.AmbiguousFunctionWord,
                    $"'{functionName}' has no owned argument");
            }

            return (new FunctionCallNode(functionName, nameTokens, argument), next);
        }

        private (LayoutNode Node, int NextIndex) BuildSingleOperandAtom(int start, int end)
        {
            string label = _tokens[start].Latex;
            if (IsNumberGlueEligible(label))
            {
                int j = start;
                var run = new List<RecognizedToken>();
                while (j < end && IsNumberGlueEligible(_tokens[j].Latex))
                {
                    run.Add(_tokens[j]);
                    j++;
                }

                return (BuildNumberNode(run), j);
            }

            // A single variable/greek letter — deliberately not re-attempting function-word matching here
            // (a bounded heuristic; chained bare-argument function calls are outside mandated scope).
            return (new LeafNode(_tokens[start]), start + 1);
        }

        private (int Consumed, string? Name) TryMatchFunctionWord(int start, int end)
        {
            foreach (string word in FunctionWords)
            {
                int len = word.Length;
                if (start + len > end)
                {
                    continue;
                }

                bool matches = true;
                for (int k = 0; k < len; k++)
                {
                    string label = _tokens[start + k].Latex;
                    if (!IsSingleLatinLetter(label) || label[0] != word[k])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                bool tightRun = true;
                for (int k = 0; k < len - 1; k++)
                {
                    if (!IsTight(_tokens[start + k], _tokens[start + k + 1]))
                    {
                        tightRun = false;
                        break;
                    }
                }

                if (tightRun)
                {
                    return (len, word);
                }
            }

            return (0, null);
        }

        private bool IsTight(RecognizedToken a, RecognizedToken b)
        {
            double gap = b.Bounds.X - (a.Bounds.X + a.Bounds.Width);
            return gap <= TightGapRatio * _refHeight;
        }

        private static bool IsSingleLatinLetter(string label) => label.Length == 1 && char.IsAsciiLetter(label[0]);

        private static bool IsArgumentStart(string label) =>
            IsSingleLatinLetter(label) || label is @"\pi" or @"\theta" or @"\alpha" || IsNumberGlueEligible(label);

        private static LayoutNode BuildNumberNode(List<RecognizedToken> run) => run.Count == 1
            ? new LeafNode(run[0])
            : new SequenceNode(run.Select(token => (LayoutNode)new LeafNode(token)).ToList());

        // Groups a flat atom stream into the top-level baseline sequence: consecutive operand atoms with no
        // operator between them multiply implicitly; a leading minus (or one right after another
        // operator/relation/open-bracket — the latter falls out naturally because that position is always
        // "first atom of a recursively-parsed span") is unary and folds into its operand rather than
        // splitting the run; every other operator is strictly binary and requires an operand on both sides.
        private static LayoutNode ClusterAtoms(List<Atom> atoms)
        {
            bool expectOperand = true;
            RecognizedToken? pendingUnary = null;
            var currentRun = new List<LayoutNode>();
            var topChildren = new List<LayoutNode>();

            void FlushRun()
            {
                if (currentRun.Count == 0 && pendingUnary is null)
                {
                    return;
                }

                if (currentRun.Count == 0 && pendingUnary is not null)
                {
                    // Defensive: unreachable given the end-of-span expectOperand check below always catches
                    // a dangling unary minus first.
                    throw new LayoutRefusalException(
                        ParseRefusalReason.UnsupportedNotation, "a unary minus has no operand");
                }

                LayoutNode node = currentRun.Count == 1
                    ? currentRun[0]
                    : new ImplicitProductNode(currentRun.ToArray());
                if (pendingUnary is not null)
                {
                    node = new SequenceNode(new LayoutNode[] { new LeafNode(pendingUnary!), node });
                    pendingUnary = null;
                }

                topChildren.Add(node);
                currentRun.Clear();
            }

            foreach (Atom atom in atoms)
            {
                if (atom.IsOperator)
                {
                    RecognizedToken token = atom.OperatorToken!;
                    if (token.Latex == "-")
                    {
                        if (expectOperand)
                        {
                            if (pendingUnary is not null)
                            {
                                // Defensive: the global double-minus guard already refuses this case.
                                throw new LayoutRefusalException(
                                    ParseRefusalReason.UnsupportedNotation, "two adjacent minus signs");
                            }

                            pendingUnary = token;
                            continue;
                        }

                        FlushRun();
                        topChildren.Add(new LeafNode(token));
                        expectOperand = true;
                        continue;
                    }

                    // '+', '\times', '\div', '/': always strictly binary — never a valid unary/leading form.
                    if (expectOperand)
                    {
                        throw new LayoutRefusalException(
                            ParseRefusalReason.UnsupportedNotation,
                            $"'{token.Latex}' has no left operand");
                    }

                    FlushRun();
                    topChildren.Add(new LeafNode(token));
                    expectOperand = true;
                    continue;
                }

                currentRun.Add(atom.Node!);
                expectOperand = false;
            }

            if (expectOperand)
            {
                throw new LayoutRefusalException(
                    ParseRefusalReason.UnsupportedNotation, "a trailing operator has no right operand");
            }

            FlushRun();

            if (topChildren.Count == 0)
            {
                throw new LayoutRefusalException(ParseRefusalReason.UnsupportedNotation, "an expression is empty");
            }

            return topChildren.Count == 1 ? topChildren[0] : new SequenceNode(topChildren);
        }
    }
}
