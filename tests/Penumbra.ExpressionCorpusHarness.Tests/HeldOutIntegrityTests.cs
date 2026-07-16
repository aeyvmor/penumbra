using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class HeldOutIntegrityTests
{
    [Fact]
    public void FrozenHeldOutCases_CannotReuseNormalizedInkAcrossDistinctMetadata()
    {
        ExpressionCaseV1 first = HeldOutCase("heldout-001", "session-heldout-001", secondStrokeEndY: 10);
        ExpressionCaseV1 duplicate = TransformInk(
            HeldOutCase("heldout-002", "session-heldout-002", secondStrokeEndY: 10),
            scale: 2,
            translateX: 100,
            translateY: 50);
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (first, CorpusCaseStatusV1.Frozen, null),
            (duplicate, CorpusCaseStatusV1.Frozen, null));

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateSuite(suite);

        Assert.Contains(errors, error =>
            error.Code == CorpusErrorCode.DuplicateHeldOutInk
            && error.Location == "cases.strokes");
    }

    [Fact]
    public void ContaminatedHeldOutCases_CannotShareOneFrozenReplacement()
    {
        ExpressionCaseV1 first = HeldOutCase("heldout-001", "session-heldout-001", secondStrokeEndY: 10);
        ExpressionCaseV1 second = HeldOutCase("heldout-002", "session-heldout-002", secondStrokeEndY: 8);
        ExpressionCaseV1 replacement = HeldOutCase("heldout-003", "session-heldout-003", secondStrokeEndY: 5);
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (first, CorpusCaseStatusV1.Contaminated, replacement.CaseId),
            (second, CorpusCaseStatusV1.Contaminated, replacement.CaseId),
            (replacement, CorpusCaseStatusV1.Frozen, null));

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateSuite(suite);

        Assert.Contains(errors, error =>
            error.Code == CorpusErrorCode.MissingHeldOutReplacement
            && error.Location == "manifest.entries.replacementCaseId");
    }

    [Fact]
    public void ContaminatedHeldOutCases_WithOneDistinctFrozenReplacementEachRemainValid()
    {
        ExpressionCaseV1 first = HeldOutCase("heldout-001", "session-heldout-001", secondStrokeEndY: 10);
        ExpressionCaseV1 second = HeldOutCase("heldout-002", "session-heldout-002", secondStrokeEndY: 8);
        ExpressionCaseV1 firstReplacement = HeldOutCase(
            "heldout-003", "session-heldout-003", secondStrokeEndY: 5);
        ExpressionCaseV1 secondReplacement = HeldOutCase(
            "heldout-004", "session-heldout-004", secondStrokeEndY: 2);
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (first, CorpusCaseStatusV1.Contaminated, firstReplacement.CaseId),
            (second, CorpusCaseStatusV1.Contaminated, secondReplacement.CaseId),
            (firstReplacement, CorpusCaseStatusV1.Frozen, null),
            (secondReplacement, CorpusCaseStatusV1.Frozen, null));

        Assert.Empty(CorpusValidator.ValidateSuite(suite));
    }

    [Fact]
    public void ContaminatedHeldOutCase_CannotReuseItsNormalizedInkAsReplacement()
    {
        ExpressionCaseV1 original = HeldOutCase("heldout-001", "session-heldout-001", secondStrokeEndY: 10);
        ExpressionCaseV1 replacement = HeldOutCase("heldout-002", "session-heldout-002", secondStrokeEndY: 10);
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (original, CorpusCaseStatusV1.Contaminated, replacement.CaseId),
            (replacement, CorpusCaseStatusV1.Frozen, null));

        Assert.Contains(CorpusValidator.ValidateSuite(suite), error =>
            error.Code == CorpusErrorCode.MissingHeldOutReplacement);
    }

    private static ExpressionCaseV1 HeldOutCase(
        string caseId,
        string sessionId,
        double secondStrokeEndY)
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase(caseId, sessionId);
        return CorpusTestData.WithSecondStrokeEndY(@case, secondStrokeEndY) with
        {
            Partition = CorpusPartitionV1.HeldOut,
        };
    }

    private static ExpressionCaseV1 TransformInk(
        ExpressionCaseV1 @case,
        double scale,
        double translateX,
        double translateY)
    {
        CorpusStrokeV1[] strokes = @case.Strokes.Select(stroke => stroke with
        {
            Samples = stroke.Samples.Select(sample => sample with
            {
                X = sample.X * scale + translateX,
                Y = sample.Y * scale + translateY,
            }).ToArray(),
        }).ToArray();
        RecognizeStepV1 recognize = Assert.IsType<RecognizeStepV1>(@case.Steps[0]);
        ExpectedRegionV1[] regions = recognize.Expected.Regions.Select(region => region with
        {
            Bounds = new CorpusBoundsV1(
                region.Bounds.X * scale + translateX,
                region.Bounds.Y * scale + translateY,
                region.Bounds.Width * scale,
                region.Bounds.Height * scale),
        }).ToArray();
        return @case with
        {
            Strokes = strokes,
            Steps = [recognize with { Expected = recognize.Expected with { Regions = regions } }],
        };
    }
}
