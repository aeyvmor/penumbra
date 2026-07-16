namespace Penumbra.ExpressionCorpus;

internal static class CorpusRefusalSemanticsV1
{
    public static bool IsValid(CorpusFailureCategoryV1 stage, CorpusRefusalCodeV1 reason) =>
        Enum.IsDefined(stage)
        && Enum.IsDefined(reason)
        && reason switch
        {
            CorpusRefusalCodeV1.LowConfidence or CorpusRefusalCodeV1.OutOfDistribution =>
                stage == CorpusFailureCategoryV1.SymbolClassification,
            CorpusRefusalCodeV1.SegmentationAmbiguity =>
                stage == CorpusFailureCategoryV1.Segmentation,
            CorpusRefusalCodeV1.SpatialAmbiguity
                or CorpusRefusalCodeV1.UnownedStroke
                or CorpusRefusalCodeV1.DuplicateStrokeOwnership =>
                stage == CorpusFailureCategoryV1.SpatialRelation,
            CorpusRefusalCodeV1.MalformedStructure =>
                stage is CorpusFailureCategoryV1.SpatialRelation or CorpusFailureCategoryV1.Assembly,
            CorpusRefusalCodeV1.UnsupportedNotation =>
                stage is CorpusFailureCategoryV1.SpatialRelation
                    or CorpusFailureCategoryV1.Assembly
                    or CorpusFailureCategoryV1.Cas,
            CorpusRefusalCodeV1.UnsafeCasOperation
                or CorpusRefusalCodeV1.ExplicitSolveTargetRequired =>
                stage == CorpusFailureCategoryV1.Cas,
            _ => false,
        };
}
