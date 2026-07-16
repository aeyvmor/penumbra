using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 5.5 slice 4: the linear spatial grammar. Synthetic token/geometry fixtures — no ONNX model
/// involved — exercise <see cref="SpatialLayoutParser"/> directly at the level it actually operates on:
/// one expression candidate's ordered <see cref="RecognizedToken"/> list plus each token's
/// <see cref="SymbolPrediction"/>. <see cref="LineBuilder"/> auto-positions tokens left-to-right on a
/// shared baseline (height 20, y 0) with a gap comfortably above the function-word tightness threshold
/// (12 = 0.6 * refHeight 20); individual fixtures override geometry only where the test is specifically
/// about geometry (the vertical guard, tight function-word letters).
/// </summary>
public sealed class SpatialLayoutParserTests
{
    // ---- mandatory positive fixtures --------------------------------------------------------------------

    [Fact]
    public void LinearCoefficientEquation_AcceptsWithImplicitProductAndTrailingRhs()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("2").Add("x").Add("+").Add("5").Add("=").Add("1").Add("3")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("2x+5=13", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void ProductOfTwoDelimitedGroups_AcceptsAsImplicitProductWithTrailingRelation()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("(").Add("x").Add("+").Add("1").Add(")")
            .Add("(").Add("x").Add("-").Add("1").Add(")")
            .Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(
            @"\left(x+1\right)\left(x-1\right)=",
            LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void FunctionCallWithParens_OwnsNameGlyphsAndBrackets_SinOfX()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("y").Add("=")
            .Add("s", gap: 16).Add("i", gap: 3).Add("n", gap: 3)
            .Add("(", gap: 16).Add("x", gap: 3).Add(")", gap: 3)
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"y=\sin(x)", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        Assert.IsType<RelationNode>(result.Outcome.Root);
        var relation = (RelationNode)result.Outcome.Root!;
        Assert.IsType<FunctionCallNode>(relation.Right);
        Assert.Equal("sin", ((FunctionCallNode)relation.Right!).FunctionName);
    }

    [Fact]
    public void DecimalPlusInteger_GluesDigitsAndDecimalPointIntoOneNumber()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("2").Add(".").Add("5").Add("+").Add("1").Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("2.5+1=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void UnaryMinus_FoldsIntoOperandRatherThanSplittingTheSequence()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("-").Add("3").Add("+").Add("5").Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("-3+5=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void PiTimesVariable_AcceptsAsImplicitProductWithSpaceAfterControlWord()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add(@"\pi").Add("x")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\pi x", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        Assert.IsType<ImplicitProductNode>(result.Outcome.Root);
    }

    [Fact]
    public void DigitAdjacentToDelimitedGroup_AcceptsAsImplicitProduct()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("2").Add("(").Add("x").Add("+").Add("1").Add(")")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"2\left(x+1\right)", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void NotEqualRelation_AcceptsWithBareRightOperand()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("x").Add(@"\neq").Add("5")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"x\neq 5", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void LtRelation_SerializesToBareLessThan()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("2").Add(@"\lt").Add("3")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("2<3", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void TightLettersNotSpellingAFunctionWord_StayPlainVariables()
    {
        // "cow" is tight letter geometry but not a recognized function word — must read as a product of
        // three variables, never attempted as a function call and never refused.
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("c").Add("o", gap: 3).Add("w", gap: 3)
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("cow", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        Assert.IsType<ImplicitProductNode>(result.Outcome.Root);
    }

    [Fact]
    public void TightFunctionWordWithNoParens_TakesOneTightFollowingAtomAsArgument()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("s").Add("i", gap: 3).Add("n", gap: 3).Add("x", gap: 3)
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\sin(x)", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    // ---- vertical geometry guard / superscript geometry ---------------------------------------------------

    [Fact]
    public void RaisedSmallerToken_AcceptsAsSuperscript_InsteadOfSilentFlatRead()
    {
        // Slice 4 pinned this geometry as an outright refusal (UncertainScript), because the linear parser
        // had no recursive script representation yet — accepting a flat "x2" (-> x*2) would have been the
        // classic silent-wrong trap. Slice 5 lands ScriptNode: this SAME clearly-scripted geometry (bottom
        // edge well above the base's midpoint, height well under ScriptClearSizeRatio) now recurses into a
        // trustworthy x^2 instead of refusing — the old blanket refusal is replaced by an accept/margin/none
        // band (see RaisedToken_MarginSizeBand_StillRefusesUncertainScript below for the genuinely uncertain
        // case that still refuses).
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken raisedTwo = Tok("2", x: 16, width: 8, height: 8, y: -14);
        var tokens = new[] { x, raisedTwo };
        var predictions = new[] { Pred("x"), Pred("2") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x^{2}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        Assert.IsType<ScriptNode>(result.Outcome.Root);
        var script = (ScriptNode)result.Outcome.Root!;
        Assert.Same(raisedTwo, ((LeafNode)script.Superscript!).Token);
        Assert.Null(script.Subscript);
    }

    [Fact]
    public void RaisedToken_MarginSizeBand_StillRefusesUncertainScript()
    {
        // Between "confidently a script" (ScriptClearSizeRatio) and "basically the same size" is a genuinely
        // uncertain band: the candidate crosses the base's midpoint edge but isn't small enough to call
        // confidently. This must still refuse rather than guess either way.
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken marginal = Tok("2", x: 16, width: 8, height: 17, y: -10);   // ratio 0.85: Clear<r<Margin
        var tokens = new[] { x, marginal };
        var predictions = new[] { Pred("x"), Pred("2") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UncertainScript, result.Outcome.Reason);
    }

    [Fact]
    public void RaisedSameSizeToken_RefusesScriptVersusSeparateLineAmbiguity()
    {
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken raisedPeer = Tok("2", x: 16, width: 12, height: 20, y: -18);
        RecognizedToken[] tokens = { x, raisedPeer };
        SymbolPrediction[] predictions = { Pred("x"), Pred("2") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UncertainScript, result.Outcome.Reason);
    }

    [Fact]
    public void RaisedSmallTokenFarToTheRight_RefusesScriptVersusSeparateLineAmbiguity()
    {
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken distant = Tok("2", x: 80, width: 8, height: 8, y: -14);
        RecognizedToken[] tokens = { x, distant };
        SymbolPrediction[] predictions = { Pred("x"), Pred("2") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UncertainScript, result.Outcome.Reason);
    }

    [Fact]
    public void LoweredSmallerToken_RefusesGeneralSubscript()
    {
        // Phase 5.5 has no CAS-safe semantic subscript: any clearly subscript-positioned token refuses,
        // it never silently collapses into flat adjacency or gets dropped.
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken loweredOne = Tok("1", x: 16, width: 8, height: 8, y: 16);   // top(16) > mid(10)
        var tokens = new[] { x, loweredOne };
        var predictions = new[] { Pred("x"), Pred("1") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.GeneralSubscript, result.Outcome.Reason);
    }

    [Fact]
    public void LoweredToken_MarginSizeBand_RefusesUncertainScript()
    {
        // A lowered glyph whose size sits in the accept/refuse margin is not a proven semantic subscript.
        // Report geometry uncertainty rather than claiming the stronger GeneralSubscript diagnosis.
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken marginal = Tok("1", x: 16, width: 8, height: 17, y: 12);
        var tokens = new[] { x, marginal };
        var predictions = new[] { Pred("x"), Pred("1") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UncertainScript, result.Outcome.Reason);
    }

    [Fact]
    public void OrdinaryCoBaselineLine_DoesNotTripTheVerticalGuard()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("2").Add("3").Add("+").Add("7").Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("23+7=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    // ---- Part A bug fixes: descender/ascender letters are not scripts ---------------------------------------

    [Fact]
    public void DescenderLetter_NextToTallLetter_IsNotReadAsAScript_XyEqualsAccepts()
    {
        // 'y' has a low CENTER (the descender loop pulls it down) but its TOP aligns with 'x's x-height —
        // the old center-offset guard misread this as a script; the edge test must not.
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("x").Add("y", height: 26).Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("xy=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        Assert.IsType<ImplicitProductNode>(((RelationNode)result.Outcome.Root!).Left);
    }

    [Fact]
    public void CoefficientThenDescenderVariable_RelatedToAnotherCoefficientVariable_20yEquals5x()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("2").Add("0").Add("y", height: 26).Add("=").Add("5").Add("x")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("20y=5x", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void CoefficientThenDescenderVariable_MinusCoefficient_TrailingRelation_7yMinus2Equals()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("7").Add("y", height: 26).Add("-").Add("2").Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("7y-2=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void CoefficientThenVariable_TrailingRelation_5xEqualsAccepts()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("5").Add("x").Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("5x=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void DescenderVariable_AsRelationLeftHandSide_WithScriptedRightHandSide_YEqualsXSquared()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("y", height: 26).Add("=").Add("x")
            .Build();
        // Append a raised, smaller exponent "2" positioned relative to the trailing "x" only.
        RecognizedToken exponent = Tok("2", x: tokens[^1].Bounds.X + tokens[^1].Bounds.Width, width: 8, height: 8, y: -14);
        RecognizedToken[] withExponent = tokens.Append(exponent).ToArray();
        SymbolPrediction[] predsWithExponent = predictions.Append(Pred("2")).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(withExponent, predsWithExponent);

        AssertAcceptedAndOwned(result, withExponent);
        Assert.Equal("y=x^{2}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void AscenderLetter_NextToOrdinaryLetter_IsNotReadAsASuperscript()
    {
        // 'b' has a HIGH center (the ascender stem pulls it up) but its BOTTOM sits on the same baseline as
        // 'x's — the bottom-edge superscript test must never cross for it.
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("x").Add("b", height: 26, yOffset: -6)
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("xb", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        Assert.IsType<ImplicitProductNode>(result.Outcome.Root);
    }

    // ---- Part A bug fixes: operators/relations are never script candidates ----------------------------------

    [Fact]
    public void ShortEqualsDrawnLow_BesideTallLetter_NeverTriggersTheGuard()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("x").Add("=", height: 4, yOffset: 14).Add("5")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x=5", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void ShortEqualsDrawnHigh_BesideTallLetter_NeverTriggersTheGuard()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("x").Add("=", height: 4, yOffset: -8).Add("5")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x=5", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void ShortMinusDrawnLow_BesideTallLetter_NeverTriggersTheGuard()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("x").Add("-", height: 3, yOffset: 15).Add("5").Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x-5=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    // ---- Part A bug fix: equals-merge rescue ------------------------------------------------------------

    [Fact]
    public void TwoStackedFlatMinusTokens_MergeIntoOneEqualsRelation()
    {
        // A hand-drawn '=' the segmenter kept as two separate short/flat '-' groups: stacked (X-overlap),
        // close (small vertical gap), nothing else sitting between them.
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken topBar = Tok("-", x: 16, width: 12, height: 3, y: 6);
        RecognizedToken bottomBar = Tok("-", x: 16, width: 12, height: 3, y: 14);
        RecognizedToken five = Tok("5", x: 32, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { x, topBar, bottomBar, five };
        SymbolPrediction[] predictions = { Pred("x"), Pred("-"), Pred("-"), Pred("5") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Accepted, result.Outcome.Kind);
        // The rescue drops the token count from 4 to 3: the two bars fuse into one '=' token.
        Assert.Equal(3, result.Tokens.Count);
        Assert.Equal("=", result.Tokens[1].Latex);
        Assert.Equal(2, result.Tokens[1].SourceStrokeIds.Count);
        Assert.Equal(Math.Min(topBar.Confidence, bottomBar.Confidence), result.Tokens[1].Confidence, 9);
        OwnershipValidationResult validation = OwnershipValidator.Validate(result.Outcome.Root!, result.Tokens);
        Assert.True(validation.IsValid, string.Join("; ", validation.Violations.Select(v => $"{v.Kind}: {v.Detail}")));
        Assert.Equal("x=5", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void TwoStackedFlatMinusTokens_UseOperandHeightForTheCloseGapBand()
    {
        // The two bars must not collapse the reference-height estimate to their own 1px height. With a
        // normal 20px operand, a 12px bar-to-bar gap is inside the documented 0.7 * ref-height band.
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken topBar = Tok("-", x: 16, width: 12, height: 1, y: 3);
        RecognizedToken bottomBar = Tok("-", x: 16, width: 12, height: 1, y: 16);
        RecognizedToken five = Tok("5", x: 32, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { x, topBar, bottomBar, five };
        SymbolPrediction[] predictions = { Pred("x"), Pred("-"), Pred("-"), Pred("5") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Accepted, result.Outcome.Kind);
        Assert.Equal("x=5", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void TwoStackedRejectedMinusTokens_PreserveRejectionWhenMerged()
    {
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken topBar = Tok("-", x: 16, width: 12, height: 2, y: 5, rejected: true);
        RecognizedToken bottomBar = Tok("-", x: 16, width: 12, height: 2, y: 13);
        RecognizedToken five = Tok("5", x: 32, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { x, topBar, bottomBar, five };
        SymbolPrediction[] predictions =
        {
            Pred("x"), Pred("-", rejected: true), Pred("-"), Pred("5"),
        };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        RecognizedToken merged = Assert.Single(result.Tokens, token => token.Latex == "=");
        Assert.True(merged.Rejected);
    }

    [Fact]
    public void TwoHorizontallySeparatedMinusSigns_StayTwoMinuses_RefusesDoubleMinus()
    {
        // Side-by-side (not stacked): no meaningful X-overlap, so the equals-merge rescue must not fire —
        // this stays two genuine minus signs, and the existing double-minus guard refuses it as before.
        RecognizedToken three = Tok("3", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken minusA = Tok("-", x: 16, width: 10, height: 3, y: 8);
        RecognizedToken minusB = Tok("-", x: 40, width: 10, height: 3, y: 8);
        RecognizedToken five = Tok("5", x: 56, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { three, minusA, minusB, five };
        SymbolPrediction[] predictions = { Pred("3"), Pred("-"), Pred("-"), Pred("5") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnsupportedNotation, result.Outcome.Reason);
        Assert.Equal(4, result.Tokens.Count);   // untouched — no merge happened.
    }

    // ---- Part B: superscripts ---------------------------------------------------------------------------

    [Fact]
    public void MultiDigitExponent_GluesIntoOneRaisedNumber_XTo21()
    {
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken two = Tok("2", x: 16, width: 7, height: 8, y: -14);
        RecognizedToken one = Tok("1", x: 24, width: 7, height: 8, y: -14);
        RecognizedToken[] tokens = { x, two, one };
        SymbolPrediction[] predictions = { Pred("x"), Pred("2"), Pred("1") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x^{21}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        var script = (ScriptNode)result.Outcome.Root!;
        Assert.IsType<SequenceNode>(script.Superscript);
    }

    [Fact]
    public void ExponentWithInternalOperator_RecursesFully_XTo2Plus1()
    {
        // The exponent's own '+' is drawn small/raised too — mandate: "x^{2+1}" (a nested expression inside
        // the script, not just a glued number run).
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken two = Tok("2", x: 16, width: 7, height: 8, y: -14);
        RecognizedToken plus = Tok("+", x: 24, width: 7, height: 8, y: -14);
        RecognizedToken one = Tok("1", x: 32, width: 7, height: 8, y: -14);
        RecognizedToken[] tokens = { x, two, plus, one };
        SymbolPrediction[] predictions = { Pred("x"), Pred("2"), Pred("+"), Pred("1") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x^{2+1}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void NestedSuperscript_Recurses_XToTwoToThree()
    {
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken two = Tok("2", x: 16, width: 8, height: 10, y: -10);
        RecognizedToken three = Tok("3", x: 25, width: 5, height: 5, y: -18);
        RecognizedToken[] tokens = { x, two, three };
        SymbolPrediction[] predictions = { Pred("x"), Pred("2"), Pred("3") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x^{2^{3}}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void BracketedGroupBase_TakesASuperscript_ParenXPlusOneCloseParenSquared()
    {
        RecognizedToken open = Tok("(", x: 0, width: 8, height: 20, y: 0);
        RecognizedToken x = Tok("x", x: 10, width: 12, height: 20, y: 0);
        RecognizedToken plus = Tok("+", x: 24, width: 12, height: 20, y: 0);
        RecognizedToken one = Tok("1", x: 38, width: 12, height: 20, y: 0);
        RecognizedToken close = Tok(")", x: 52, width: 8, height: 20, y: 0);
        RecognizedToken two = Tok("2", x: 62, width: 7, height: 8, y: -14);
        RecognizedToken[] tokens = { open, x, plus, one, close, two };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\left(x+1\right)^{2}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void QuadraticPolynomial_OnlyFirstTermScripted_TrailingRelation_2XSquaredPlus3XMinus1Equals()
    {
        RecognizedToken two = Tok("2", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken x1 = Tok("x", x: 12, width: 12, height: 20, y: 0);
        RecognizedToken sq = Tok("2", x: 24, width: 7, height: 8, y: -14);
        RecognizedToken plus = Tok("+", x: 33, width: 12, height: 20, y: 0);
        RecognizedToken three = Tok("3", x: 47, width: 12, height: 20, y: 0);
        RecognizedToken x2 = Tok("x", x: 59, width: 12, height: 20, y: 0);
        RecognizedToken minus = Tok("-", x: 73, width: 12, height: 20, y: 0);
        RecognizedToken one = Tok("1", x: 87, width: 12, height: 20, y: 0);
        RecognizedToken relation = Tok("=", x: 101, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { two, x1, sq, plus, three, x2, minus, one, relation };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("2x^{2}+3x-1=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void QuadraticEquation_XSquaredMinusFiveXPlusSixEqualsZero()
    {
        RecognizedToken x1 = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken sq = Tok("2", x: 13, width: 7, height: 8, y: -12);
        RecognizedToken minus = Tok("-", x: 22, width: 10, height: 3, y: 9);
        RecognizedToken five = Tok("5", x: 34, width: 12, height: 20, y: 0);
        RecognizedToken x2 = Tok("x", x: 47, width: 12, height: 20, y: 0);
        RecognizedToken plus = Tok("+", x: 61, width: 10, height: 10, y: 5);
        RecognizedToken six = Tok("6", x: 73, width: 12, height: 20, y: 0);
        RecognizedToken relation = Tok("=", x: 87, width: 12, height: 5, y: 8);
        RecognizedToken zero = Tok("0", x: 101, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { x1, sq, minus, five, x2, plus, six, relation, zero };
        SymbolPrediction[] predictions = tokens.Select(token => Pred(token.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("x^{2}-5x+6=0", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void BracketedGroupSquared_WithTrailingQueryRelation()
    {
        RecognizedToken open = Tok("(", x: 0, width: 8, height: 20, y: 0);
        RecognizedToken x = Tok("x", x: 10, width: 12, height: 20, y: 0);
        RecognizedToken plus = Tok("+", x: 24, width: 10, height: 10, y: 5);
        RecognizedToken one = Tok("1", x: 36, width: 12, height: 20, y: 0);
        RecognizedToken close = Tok(")", x: 50, width: 8, height: 20, y: 0);
        RecognizedToken sq = Tok("2", x: 60, width: 7, height: 8, y: -12);
        RecognizedToken relation = Tok("=", x: 70, width: 12, height: 5, y: 8);
        RecognizedToken[] tokens = { open, x, plus, one, close, sq, relation };
        SymbolPrediction[] predictions = tokens.Select(token => Pred(token.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\left(x+1\right)^{2}=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    // ---- Part B: fractions -------------------------------------------------------------------------------

    [Fact]
    public void StackedFraction_XPlusOneOverTwo_AcceptsAsFractionNode()
    {
        RecognizedToken open = Tok("(", x: 0, width: 8, height: 20, y: 0);
        RecognizedToken x = Tok("x", x: 10, width: 12, height: 20, y: 0);
        RecognizedToken plus = Tok("+", x: 24, width: 12, height: 20, y: 0);
        RecognizedToken one = Tok("1", x: 38, width: 12, height: 20, y: 0);
        RecognizedToken close = Tok(")", x: 52, width: 8, height: 20, y: 0);
        RecognizedToken bar = Tok("-", x: 0, width: 60, height: 2, y: 24);
        RecognizedToken two = Tok("2", x: 24, width: 12, height: 20, y: 30);
        RecognizedToken[] tokens = { open, x, plus, one, close, bar, two };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\frac{\left(x+1\right)}{2}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        Assert.IsType<FractionNode>(result.Outcome.Root);
    }

    [Fact]
    public void StackedFraction_XOverXPlusOne_DenominatorIsTheBracketedGroup()
    {
        RecognizedToken x = Tok("x", x: 24, width: 12, height: 20, y: 0);
        RecognizedToken bar = Tok("-", x: 0, width: 60, height: 2, y: 24);
        RecognizedToken open = Tok("(", x: 0, width: 8, height: 20, y: 30);
        RecognizedToken denomX = Tok("x", x: 10, width: 12, height: 20, y: 30);
        RecognizedToken plus = Tok("+", x: 24, width: 12, height: 20, y: 30);
        RecognizedToken one = Tok("1", x: 38, width: 12, height: 20, y: 30);
        RecognizedToken close = Tok(")", x: 52, width: 8, height: 20, y: 30);
        RecognizedToken[] tokens = { x, bar, open, denomX, plus, one, close };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\frac{x}{\left(x+1\right)}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void StackedFraction_ScriptedNumerator_XSquaredMinusOneOverXMinusOne()
    {
        // Nesting mandate: a fraction with a scripted (recursive) numerator.
        RecognizedToken open = Tok("(", x: 0, width: 8, height: 20, y: 0);
        RecognizedToken x = Tok("x", x: 10, width: 12, height: 20, y: 0);
        RecognizedToken sq = Tok("2", x: 24, width: 6, height: 6, y: -8);
        RecognizedToken minus1 = Tok("-", x: 32, width: 10, height: 20, y: 0);
        RecognizedToken one1 = Tok("1", x: 44, width: 12, height: 20, y: 0);
        RecognizedToken close = Tok(")", x: 58, width: 8, height: 20, y: 0);
        RecognizedToken bar = Tok("-", x: 0, width: 70, height: 2, y: 24);
        RecognizedToken open2 = Tok("(", x: 0, width: 8, height: 20, y: 30);
        RecognizedToken x2 = Tok("x", x: 10, width: 12, height: 20, y: 30);
        RecognizedToken minus2 = Tok("-", x: 24, width: 10, height: 20, y: 30);
        RecognizedToken one2 = Tok("1", x: 36, width: 12, height: 20, y: 30);
        RecognizedToken close2 = Tok(")", x: 50, width: 8, height: 20, y: 30);
        RecognizedToken[] tokens =
        {
            open, x, sq, minus1, one1, close, bar, open2, x2, minus2, one2, close2,
        };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(
            @"\frac{\left(x^{2}-1\right)}{\left(x-1\right)}",
            LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void NestedFraction_InNumerator_ParsesRecursively()
    {
        RecognizedToken one = Tok("1", x: 23, width: 8, height: 10, y: 0);
        RecognizedToken innerBar = Tok("-", x: 15, width: 30, height: 2, y: 18);
        RecognizedToken two = Tok("2", x: 23, width: 8, height: 10, y: 30);
        RecognizedToken outerBar = Tok("-", x: 0, width: 60, height: 2, y: 50);
        RecognizedToken three = Tok("3", x: 25, width: 8, height: 10, y: 62);
        RecognizedToken[] tokens = { one, innerBar, two, outerBar, three };
        SymbolPrediction[] predictions = tokens.Select(token => Pred(token.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(
            @"\frac{\frac{1}{2}}{3}",
            LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void TwoFractionBarCandidates_NearTie_RefusesAmbiguousFractionOwnership()
    {
        RecognizedToken a = Tok("2", x: 0, width: 40, height: 20, y: 0);
        RecognizedToken bar1 = Tok("-", x: 0, width: 40, height: 2, y: 24);
        RecognizedToken b = Tok("3", x: 0, width: 40, height: 20, y: 30);
        RecognizedToken bar2 = Tok("-", x: 0, width: 40, height: 2, y: 54);
        RecognizedToken c = Tok("4", x: 0, width: 40, height: 20, y: 60);
        RecognizedToken[] tokens = { a, bar1, b, bar2, c };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.AmbiguousFractionOwnership, result.Outcome.Reason);
    }

    [Fact]
    public void FractionBarWithOneStraddlingSide_RefusesAmbiguousFractionOwnership()
    {
        // The upper token is visibly above the bar but intrudes too far through its edge to establish
        // unambiguous ownership. Reading this as flat subtraction would be a silent-wrong fallback.
        RecognizedToken upper = Tok("x", x: 4, width: 12, height: 20, y: 8);
        RecognizedToken bar = Tok("-", x: 0, width: 24, height: 2, y: 24);
        RecognizedToken lower = Tok("2", x: 6, width: 12, height: 20, y: 34);
        RecognizedToken[] tokens = { upper, bar, lower };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.AmbiguousFractionOwnership, result.Outcome.Reason);
    }

    [Fact]
    public void FractionBarBesideNumeratorSubtraction_IsNotRescuedAsEquals()
    {
        // Real X-order can place a numerator '-' immediately before the bar when they share a left edge.
        // The definite above/below ownership of the wider bar must win over the two-minus '=' rescue.
        RecognizedToken x = Tok("x", x: 0, width: 10, height: 18, y: 0);
        RecognizedToken numeratorMinus = Tok("-", x: 5, width: 20, height: 2, y: 8);
        RecognizedToken bar = Tok("-", x: 5, width: 53, height: 2, y: 24);
        RecognizedToken denominator = Tok("2", x: 24, width: 10, height: 18, y: 34);
        RecognizedToken one = Tok("1", x: 42, width: 10, height: 18, y: 0);
        RecognizedToken[] tokens = { x, numeratorMinus, bar, denominator, one };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\frac{x-1}{2}", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void FractionBarWithMalformedNumerator_RefusesAmbiguousFractionOwnership()
    {
        // The numerator side has a dangling binary operator ("x+" with nothing after it) — spans content
        // above and below, so the bar is detected, but the numerator sub-parse itself cannot succeed.
        RecognizedToken x = Tok("x", x: 0, width: 12, height: 20, y: 0);
        RecognizedToken plus = Tok("+", x: 14, width: 12, height: 20, y: 0);
        RecognizedToken bar = Tok("-", x: 0, width: 40, height: 2, y: 24);
        RecognizedToken two = Tok("2", x: 10, width: 12, height: 20, y: 30);
        RecognizedToken[] tokens = { x, plus, bar, two };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.AmbiguousFractionOwnership, result.Outcome.Reason);
    }

    // ---- Part B: radicals ---------------------------------------------------------------------------------

    [Fact]
    public void Radical_SimpleDigitRadicand_TrailingRelation_Sqrt9Equals()
    {
        RecognizedToken sqrt = Tok(@"\sqrt", x: 0, width: 20, height: 20, y: 0);
        RecognizedToken nine = Tok("9", x: 4, width: 12, height: 20, y: 0);
        RecognizedToken relation = Tok("=", x: 24, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { sqrt, nine, relation };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\sqrt{9}=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void Radical_ExpressionRadicand_TrailingRelation_SqrtXPlusOneEquals()
    {
        RecognizedToken sqrt = Tok(@"\sqrt", x: 0, width: 50, height: 20, y: 0);
        RecognizedToken x = Tok("x", x: 4, width: 12, height: 20, y: 0);
        RecognizedToken plus = Tok("+", x: 18, width: 12, height: 20, y: 0);
        RecognizedToken one = Tok("1", x: 32, width: 12, height: 20, y: 0);
        RecognizedToken relation = Tok("=", x: 54, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { sqrt, x, plus, one, relation };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\sqrt{x+1}=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void Radical_PowerInsideRadicand_MandatoryNesting_SqrtXSquaredPlusOneEquals()
    {
        RecognizedToken sqrt = Tok(@"\sqrt", x: 0, width: 66, height: 20, y: 0);
        RecognizedToken x = Tok("x", x: 4, width: 12, height: 20, y: 0);
        RecognizedToken sq = Tok("2", x: 18, width: 6, height: 6, y: -8);
        RecognizedToken plus = Tok("+", x: 28, width: 12, height: 20, y: 0);
        RecognizedToken one = Tok("1", x: 44, width: 12, height: 20, y: 0);
        RecognizedToken relation = Tok("=", x: 70, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { sqrt, x, sq, plus, one, relation };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal(@"\sqrt{x^{2}+1}=", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
        var relationNode = (RelationNode)result.Outcome.Root!;
        Assert.IsType<RadicalNode>(relationNode.Left);
    }

    [Fact]
    public void Radical_NoTokenWithinSpan_RefusesEmptyRadicalOwnership()
    {
        // The '=' sits outside the radical's horizontal reach — nothing is left to own as a radicand.
        RecognizedToken sqrt = Tok(@"\sqrt", x: 0, width: 10, height: 20, y: 0);
        RecognizedToken relation = Tok("=", x: 40, width: 12, height: 20, y: 0);
        RecognizedToken[] tokens = { sqrt, relation };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.EmptyRadicalOwnership, result.Outcome.Reason);
    }

    [Fact]
    public void Radical_RadicandExtendingMateriallyBeyondSpan_RefusesOwnership()
    {
        RecognizedToken sqrt = Tok(@"\sqrt", x: 0, width: 20, height: 20, y: 0);
        RecognizedToken wide = Tok("x", x: 8, width: 30, height: 20, y: 0);
        RecognizedToken[] tokens = { sqrt, wide };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.EmptyRadicalOwnership, result.Outcome.Reason);
    }

    [Fact]
    public void Radical_RaisedRootIndexCandidate_RefusesHonestly()
    {
        RecognizedToken index = Tok("3", x: 0, width: 6, height: 7, y: -8);
        RecognizedToken sqrt = Tok(@"\sqrt", x: 5, width: 30, height: 22, y: 0);
        RecognizedToken x = Tok("x", x: 14, width: 10, height: 18, y: 2);
        RecognizedToken[] tokens = { index, sqrt, x };
        SymbolPrediction[] predictions = tokens.Select(t => Pred(t.Latex)).ToArray();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.EmptyRadicalOwnership, result.Outcome.Reason);
    }

    // ---- mandatory negative fixtures ----------------------------------------------------------------------

    [Fact]
    public void UnmatchedOpenBracket_Refuses()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("(").Add("x").Add("+").Add("1")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnmatchedBracket, result.Outcome.Reason);
    }

    [Fact]
    public void CrossedBrackets_Refuses()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("(").Add("[").Add("x").Add(")").Add("]")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnmatchedBracket, result.Outcome.Reason);
    }

    [Fact]
    public void MultipleRelationSignsOnOneLine_RefusesUnsupportedRelation()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("x").Add("=").Add("1").Add("=").Add("2")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnsupportedRelation, result.Outcome.Reason);
    }

    [Fact]
    public void FunctionWordWithNoOwnedArgument_RefusesAmbiguousFunctionWord()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("s").Add("i", gap: 3).Add("n", gap: 3)
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.AmbiguousFunctionWord, result.Outcome.Reason);
    }

    [Fact]
    public void SqrtToken_RefusesEmptyRadicalOwnership_RatherThanFlatRead()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add(@"\sqrt").Add("9").Add("=")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.EmptyRadicalOwnership, result.Outcome.Reason);
    }

    [Fact]
    public void SumToken_RefusesUnsupportedNotation()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder().Add(@"\sum").Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnsupportedNotation, result.Outcome.Reason);
    }

    [Fact]
    public void IntToken_RefusesUnsupportedNotation()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder().Add(@"\int").Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnsupportedNotation, result.Outcome.Reason);
    }

    [Fact]
    public void TrailingBinaryOperatorWithNothingAfter_RefusesUnsupportedNotation()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder().Add("3").Add("+").Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnsupportedNotation, result.Outcome.Reason);
    }

    [Fact]
    public void DoubleMinus_RefusesUnsupportedNotation()
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = new LineBuilder()
            .Add("3").Add("-").Add("-").Add("5")
            .Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnsupportedNotation, result.Outcome.Reason);
    }

    // ---- alternatives-based 0/o and 1/l contextual disambiguation ----------------------------------------

    [Fact]
    public void ZeroOhConfusion_DigitContextWithCloseMargin_RewritesToZero()
    {
        var strokeId = new[] { Guid.NewGuid() };
        RecognizedToken[] tokens =
        {
            Tok("1", x: 0),
            Tok("o", x: 16, confidence: 0.6),
            Tok("5", x: 32),
        };
        SymbolPrediction[] predictions =
        {
            Pred("1"),
            Pred("o", confidence: 0.6, alternatives: new[] { new SymbolAlternative("0", 0.5) }),
            Pred("5"),
        };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("0", result.Tokens[1].Latex);
        Assert.Equal("105", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void ZeroOhConfusion_LargeMargin_KeepsTopOne()
    {
        RecognizedToken[] tokens =
        {
            Tok("1", x: 0),
            Tok("o", x: 16, confidence: 0.9),
            Tok("5", x: 32),
        };
        SymbolPrediction[] predictions =
        {
            Pred("1"),
            Pred("o", confidence: 0.9, alternatives: new[] { new SymbolAlternative("0", 0.05) }),
            Pred("5"),
        };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("o", result.Tokens[1].Latex);
        Assert.Equal("1o5", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void OneLConfusion_LetterContextWithCloseMargin_RewritesToL()
    {
        RecognizedToken[] tokens =
        {
            Tok("a", x: 0),
            Tok("1", x: 16, confidence: 0.55),
            Tok("c", x: 32),
        };
        SymbolPrediction[] predictions =
        {
            Pred("a"),
            Pred("1", confidence: 0.55, alternatives: new[] { new SymbolAlternative("l", 0.5) }),
            Pred("c"),
        };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("l", result.Tokens[1].Latex);
        Assert.Equal("alc", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void OneLConfusion_LargeMargin_KeepsTopOne()
    {
        RecognizedToken[] tokens =
        {
            Tok("a", x: 0),
            Tok("1", x: 16, confidence: 0.95),
            Tok("c", x: 32),
        };
        SymbolPrediction[] predictions =
        {
            Pred("a"),
            Pred("1", confidence: 0.95, alternatives: new[] { new SymbolAlternative("l", 0.1) }),
            Pred("c"),
        };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("1", result.Tokens[1].Latex);
        Assert.Equal("a1c", LayoutLatexSerializer.Serialize(result.Outcome.Root!));
    }

    [Fact]
    public void ZeroOhConfusion_NeutralGeometryNearTie_RefusesAmbiguousLowMargin()
    {
        // A lone symbol with no digit/letter neighbour on either side: geometry offers no verdict, and a
        // near-exact statistical tie must refuse rather than silently guess.
        RecognizedToken[] tokens = { Tok("o", x: 0, confidence: 0.52) };
        SymbolPrediction[] predictions =
        {
            Pred("o", confidence: 0.52, alternatives: new[] { new SymbolAlternative("0", 0.48) }),
        };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        Assert.Equal(ParseOutcomeKind.Ambiguous, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.LowMargin, result.Outcome.Reason);
    }

    [Fact]
    public void NoAlternativesSupplied_NeverRewrites_TestFakesUnaffected()
    {
        // A classifier that never fills Alternatives (every existing test fake) must see zero behavior
        // change: rule 3 requires competing evidence, and finds none.
        RecognizedToken[] tokens = { Tok("1", x: 0), Tok("o", x: 16), Tok("5", x: 32) };
        SymbolPrediction[] predictions = { Pred("1"), Pred("o"), Pred("5") };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        Assert.Equal("o", result.Tokens[1].Latex);
    }

    // ---- linear-arithmetic parity: accepted plain lines match TokenLatexAssembler exactly -----------------

    [Theory]
    [InlineData(new object[] { new[] { "1", "+", "1", "=" } })]
    [InlineData(new object[] { new[] { "2", "3", "+", "7", "=" } })]
    [InlineData(new object[] { new[] { "9", "-", "4", "=" } })]
    [InlineData(new object[] { new[] { "5", "=" } })]
    [InlineData(new object[] { new[] { "1", "2", "3" } })]
    public void PlainArithmeticLines_TreeSerializationMatchesFlatTokenAssembly(string[] labels)
    {
        var line = new LineBuilder();
        foreach (string label in labels)
        {
            line.Add(label);
        }

        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = line.Build();

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        AssertAcceptedAndOwned(result, tokens);
        string treeLatex = LayoutLatexSerializer.Serialize(result.Outcome.Root!);
        string flatLatex = TokenLatexAssembler.Assemble(labels);
        Assert.Equal(flatLatex, treeLatex);
    }

    // ---- rejected (OOD) tokens are excluded, never owned ---------------------------------------------------

    [Fact]
    public void RejectedToken_IsExcludedFromTheTree_AndNeverOwned()
    {
        RecognizedToken[] tokens =
        {
            Tok("2", x: 0),
            Tok("+", x: 16),
            Tok("?", x: 32, rejected: true),
        };
        SymbolPrediction[] predictions = { Pred("2"), Pred("+"), Pred("?", rejected: true) };

        SpatialParseResult result = SpatialLayoutParser.Parse(tokens, predictions);

        // "2+" with the OOD token dropped is a trailing binary operator: refused, not silently accepted.
        Assert.Equal(ParseOutcomeKind.Refused, result.Outcome.Kind);
        Assert.Equal(ParseRefusalReason.UnsupportedNotation, result.Outcome.Reason);
    }

    // ---- test helpers --------------------------------------------------------------------------------------

    private static void AssertAcceptedAndOwned(SpatialParseResult result, IReadOnlyList<RecognizedToken> inputTokens)
    {
        Assert.Equal(ParseOutcomeKind.Accepted, result.Outcome.Kind);
        Assert.NotNull(result.Outcome.Root);
        // Stage 0's contextual rewrite only ever relabels a token in place — it never adds, drops, or
        // reorders one.
        Assert.Equal(inputTokens.Count, result.Tokens.Count);
        OwnershipValidationResult validation = OwnershipValidator.Validate(result.Outcome.Root!, result.Tokens);
        Assert.True(validation.IsValid, string.Join(
            "; ", validation.Violations.Select(v => $"{v.Kind}: {v.Detail}")));
    }

    private static RecognizedToken Tok(
        string latex, double x, double width = 12, double height = 20, double y = 0,
        double confidence = 1.0, bool rejected = false) =>
        new(latex, new[] { Guid.NewGuid() }, new InkBounds(x, y, width, height), confidence, rejected);

    private static SymbolPrediction Pred(
        string label, double confidence = 1.0, bool rejected = false,
        IReadOnlyList<SymbolAlternative>? alternatives = null) =>
        new(label, confidence, Rejected: rejected, Alternatives: alternatives);

    /// <summary>Auto-positions tokens left-to-right on a shared baseline with a non-tight default gap.</summary>
    private sealed class LineBuilder
    {
        private readonly List<RecognizedToken> _tokens = new();
        private readonly List<SymbolPrediction> _predictions = new();
        private double _cursor;

        public LineBuilder Add(
            string latex,
            double gap = 16,
            double width = 12,
            double height = 20,
            double yOffset = 0,
            double confidence = 1.0,
            bool rejected = false,
            IReadOnlyList<SymbolAlternative>? alternatives = null)
        {
            double x = _tokens.Count == 0 ? 0 : _cursor + gap;
            _tokens.Add(new RecognizedToken(
                latex, new[] { Guid.NewGuid() }, new InkBounds(x, yOffset, width, height), confidence, rejected));
            _predictions.Add(new SymbolPrediction(latex, confidence, Rejected: rejected, Alternatives: alternatives));
            _cursor = x + width;
            return this;
        }

        public (RecognizedToken[] Tokens, SymbolPrediction[] Predictions) Build() =>
            (_tokens.ToArray(), _predictions.ToArray());
    }
}
