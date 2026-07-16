using System.Text.Json.Serialization;

namespace Penumbra.ExpressionCorpus;

public static class CorpusFormatV1
{
    public const string CaseFormat = "penumbra-expression-case";
    public const string ManifestFormat = "penumbra-expression-manifest";
    public const int SchemaVersion = 1;
}

public enum CorpusPartitionV1
{
    Development,
    HeldOut,
}

public enum CorpusCaptureSourceV1
{
    Synthetic,
    UserRealPen,
}

public enum CorpusDataClassificationV1
{
    PublicSynthetic,
    PrivateOwnedInk,
}

public enum CorpusDeviceClassV1
{
    Unknown,
    Mouse,
    ActivePen,
    PassiveStylus,
    Synthetic,
}

public enum CorpusPressureModeV1
{
    Unavailable,
    Normalized,
}

public enum CorpusCaptureApiV1
{
    Unknown,
    AvaloniaPointer,
    ImportedPenDocument,
    HandAuthored,
}

public enum CorpusConsentBasisV1
{
    ExplicitUserCaptureCheckpoint,
}

public enum CorpusRightsBasisV1
{
    UserAuthoredContributorOwned,
}

public enum CorpusConsentScopeV1
{
    PrivateLocalRegression,
    PrivateGitVersioning,
    PrivateRemoteBackup,
}

public enum CorpusCaseStatusV1
{
    Development,
    Frozen,
    Contaminated,
}

public enum CorpusContaminationReasonV1
{
    InspectedForFix,
    ThresholdTuning,
    DirectImplementationTarget,
}

public enum LayoutKindV1
{
    Token,
    Sequence,
    Script,
    Fraction,
    Radical,
    DelimitedGroup,
    FunctionCall,
    ImplicitProduct,
    Relation,
}

public enum LayoutRoleV1
{
    Item,
    Base,
    Superscript,
    Subscript,
    Numerator,
    Denominator,
    Radicand,
    RootIndex,
    Body,
    Function,
    Argument,
    Factor,
    Left,
    Right,
}

public enum CorpusFailureCategoryV1
{
    Segmentation,
    SymbolClassification,
    SpatialRelation,
    Assembly,
    Cas,
    Sheet,
    Persistence,
    UiIntegration,
    Graph,
    CorpusFormat,
    Infrastructure,
}

public enum CorpusRefusalCodeV1
{
    LowConfidence,
    OutOfDistribution,
    SegmentationAmbiguity,
    SpatialAmbiguity,
    MalformedStructure,
    UnsupportedNotation,
    UnownedStroke,
    DuplicateStrokeOwnership,
    UnsafeCasOperation,
    ExplicitSolveTargetRequired,
    Unknown,
}

public enum CorpusEvaluationKindV1
{
    Pending,
    Number,
    Symbolic,
    Solution,
    Boolean,
    Error,
}

public enum CorpusSheetRoleV1
{
    Definition,
    Query,
    Statement,
}

public enum CorpusSaveModeV1
{
    Explicit,
    Autosave,
}

public enum CorpusRecoveryDamageV1
{
    CorruptCurrent,
    MissingCurrent,
    StaleTemporaryCandidate,
}

public enum CorpusOpenStatusV1
{
    OpenedCurrent,
    BackupRecoveryCandidate,
    NotFound,
    Invalid,
}

public enum CorpusGraphDecisionV1
{
    Graph,
    Refuse,
}

public enum CorpusStampDecisionV1
{
    Append,
    Replace,
    Refuse,
}

public enum CorpusCapabilityV1
{
    Recognition,
    RecursiveLayout,
    Cas,
    Sheet,
    Persistence,
    Stamp,
    Taffy,
    Graph,
    UiIntegration,
}

public sealed record ExpressionCaseV1(
    [property: JsonRequired] string Format,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string CorpusVersion,
    [property: JsonRequired] int CaseRevision,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] CorpusPartitionV1 Partition,
    [property: JsonRequired] CaptureMetadataV1 Capture,
    [property: JsonRequired] IReadOnlyList<CorpusStrokeV1> Strokes,
    [property: JsonRequired] IReadOnlyList<string> InitialStrokeIds,
    [property: JsonRequired] IReadOnlyList<CorpusStepV1> Steps);

public sealed record CaptureMetadataV1(
    [property: JsonRequired] CorpusCaptureSourceV1 Source,
    [property: JsonRequired] CorpusDataClassificationV1 DataClassification,
    [property: JsonRequired] string WriterId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] CorpusDeviceClassV1 DeviceClass,
    [property: JsonRequired] CorpusPressureModeV1 PressureMode,
    [property: JsonRequired] CorpusCaptureApiV1 CaptureApi,
    [property: JsonRequired] string CaptureBuild,
    [property: JsonRequired] CaptureConsentV1? Consent);

public sealed record CaptureConsentV1(
    [property: JsonRequired] int PolicyVersion,
    [property: JsonRequired] CorpusConsentBasisV1 Basis,
    [property: JsonRequired] CorpusRightsBasisV1 RightsBasis,
    [property: JsonRequired] IReadOnlyList<CorpusConsentScopeV1> Scopes,
    [property: JsonRequired] bool PrivateRemoteStorageAllowed,
    [property: JsonRequired] bool PrivateModelTrainingAllowed,
    [property: JsonRequired] bool PublicRedistributionAllowed,
    [property: JsonRequired] DateTimeOffset RecordedAtUtc);

public sealed record CorpusStrokeV1(
    [property: JsonRequired] string StrokeId,
    [property: JsonRequired] long? StartOffsetTicks,
    [property: JsonRequired] IReadOnlyList<CorpusSampleV1> Samples);

public readonly record struct CorpusSampleV1(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] long ElapsedTicks,
    [property: JsonRequired] double Pressure);

public readonly record struct CorpusBoundsV1(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Width,
    [property: JsonRequired] double Height);

public sealed record ExpectedPageV1(
    [property: JsonRequired] IReadOnlyList<ExpectedRegionV1> Regions,
    [property: JsonRequired] ExpectedSheetV1? Sheet);

public sealed record ExpectedRegionV1(
    [property: JsonRequired] string RegionKey,
    [property: JsonRequired] IReadOnlyList<string> StrokeIds,
    [property: JsonRequired] CorpusBoundsV1 Bounds,
    [property: JsonRequired] double BoundsTolerance,
    [property: JsonRequired] ExpectedRegionExpectationV1 Expectation);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(AcceptedRegionExpectationV1), "accepted")]
[JsonDerivedType(typeof(RefusedRegionExpectationV1), "refused")]
public abstract record ExpectedRegionExpectationV1;

public sealed record AcceptedRegionExpectationV1(
    [property: JsonRequired] string Latex,
    [property: JsonRequired] IReadOnlyList<ExpectedTokenV1> Tokens,
    [property: JsonRequired] ExpectedLayoutNodeV1 Layout,
    [property: JsonRequired] ExpectedEvaluationV1? Cas) : ExpectedRegionExpectationV1;

public sealed record RefusedRegionExpectationV1(
    [property: JsonRequired] CorpusFailureCategoryV1 FirstStage,
    [property: JsonRequired] CorpusRefusalCodeV1 Reason) : ExpectedRegionExpectationV1;

public sealed record ExpectedTokenV1(
    [property: JsonRequired] string TokenId,
    [property: JsonRequired] string Latex,
    [property: JsonRequired] IReadOnlyList<string> SourceStrokeIds);

public sealed record ExpectedLayoutNodeV1(
    [property: JsonRequired] LayoutKindV1 Kind,
    [property: JsonRequired] IReadOnlyList<string> OwnedTokenIds,
    [property: JsonRequired] IReadOnlyList<ExpectedLayoutEdgeV1> Children);

public sealed record ExpectedLayoutEdgeV1(
    [property: JsonRequired] LayoutRoleV1 Role,
    [property: JsonRequired] ExpectedLayoutNodeV1 Node);

public sealed record ExpectedEvaluationV1(
    [property: JsonRequired] CorpusEvaluationKindV1 Kind,
    [property: JsonRequired] bool IsComputed,
    [property: JsonRequired] string CanonicalValue);

public sealed record ExpectedSheetV1(
    [property: JsonRequired] IReadOnlyList<ExpectedSheetNodeV1> Nodes,
    [property: JsonRequired] IReadOnlyList<string> ChangedRegionKeys,
    [property: JsonRequired] IReadOnlyList<string> CausallyAffectedRegionKeys);

public sealed record ExpectedSheetNodeV1(
    [property: JsonRequired] string RegionKey,
    [property: JsonRequired] CorpusSheetRoleV1 Role,
    [property: JsonRequired] string? DefinedSymbol,
    [property: JsonRequired] IReadOnlyList<string> FreeVariables,
    [property: JsonRequired] bool IsConflict,
    [property: JsonRequired] ExpectedEvaluationV1? Result);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AddInkStepV1), "addInk")]
[JsonDerivedType(typeof(EraseStepV1), "erase")]
[JsonDerivedType(typeof(RewriteStepV1), "rewrite")]
[JsonDerivedType(typeof(UndoStepV1), "undo")]
[JsonDerivedType(typeof(RedoStepV1), "redo")]
[JsonDerivedType(typeof(RecognizeStepV1), "recognize")]
[JsonDerivedType(typeof(StampStepV1), "stamp")]
[JsonDerivedType(typeof(TaffyProbeStepV1), "taffyProbe")]
[JsonDerivedType(typeof(SaveStepV1), "save")]
[JsonDerivedType(typeof(CloseFlushStepV1), "closeFlush")]
[JsonDerivedType(typeof(ReopenStepV1), "reopen")]
[JsonDerivedType(typeof(RecoverStepV1), "recover")]
[JsonDerivedType(typeof(GraphStepV1), "graph")]
public abstract record CorpusStepV1([property: JsonRequired] string StepId);

public sealed record AddInkStepV1(
    string StepId,
    [property: JsonRequired] IReadOnlyList<string> StrokeIds) : CorpusStepV1(StepId);

public sealed record EraseStepV1(
    string StepId,
    [property: JsonRequired] IReadOnlyList<string> StrokeIds) : CorpusStepV1(StepId);

public sealed record RewriteStepV1(
    string StepId,
    [property: JsonRequired] IReadOnlyList<string> RemovedStrokeIds,
    [property: JsonRequired] IReadOnlyList<string> AddedStrokeIds) : CorpusStepV1(StepId);

public sealed record UndoStepV1(string StepId) : CorpusStepV1(StepId);

public sealed record RedoStepV1(string StepId) : CorpusStepV1(StepId);

public sealed record RecognizeStepV1(
    string StepId,
    [property: JsonRequired] ExpectedPageV1 Expected) : CorpusStepV1(StepId);

public readonly record struct CorpusPointV1(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y);

public sealed record StampStepV1(
    string StepId,
    [property: JsonRequired] string SourceRegionKey,
    [property: JsonRequired] CorpusPointV1 GestureDelta,
    [property: JsonRequired] CorpusPointV1 DropPoint,
    [property: JsonRequired] CorpusStampDecisionV1 ExpectedDecision,
    [property: JsonRequired] double? ExpectedScale,
    [property: JsonRequired] IReadOnlyList<string> ExpectedRemovedStrokeIds,
    [property: JsonRequired] IReadOnlyList<CorpusStrokeV1> ExpectedAddedStrokes) : CorpusStepV1(StepId);

public sealed record LayoutPathSegmentV1(
    [property: JsonRequired] LayoutRoleV1 Role,
    [property: JsonRequired] int Index);

public sealed record TaffyProbeStepV1(
    string StepId,
    [property: JsonRequired] string RegionKey,
    [property: JsonRequired] IReadOnlyList<LayoutPathSegmentV1> LayoutPath,
    [property: JsonRequired] IReadOnlyList<string> SourceStrokeIds,
    [property: JsonRequired] CorpusPointV1 HitPointWorld,
    [property: JsonRequired] double CumulativeScreenDeltaX,
    [property: JsonRequired] double CanvasScale,
    [property: JsonRequired] string TrialLatex,
    [property: JsonRequired] ExpectedSheetV1 ExpectedSheet) : CorpusStepV1(StepId);

public sealed record SaveStepV1(
    string StepId,
    [property: JsonRequired] string StoreSlot,
    [property: JsonRequired] CorpusSaveModeV1 Mode) : CorpusStepV1(StepId);

public sealed record CloseFlushStepV1(
    string StepId,
    [property: JsonRequired] string StoreSlot) : CorpusStepV1(StepId);

public sealed record ReopenStepV1(
    string StepId,
    [property: JsonRequired] string StoreSlot,
    [property: JsonRequired] CorpusOpenStatusV1 ExpectedStatus,
    [property: JsonRequired] ExpectedPageV1? Expected) : CorpusStepV1(StepId);

public sealed record RecoverStepV1(
    string StepId,
    [property: JsonRequired] string StoreSlot,
    [property: JsonRequired] CorpusRecoveryDamageV1 Damage,
    [property: JsonRequired] CorpusOpenStatusV1 ExpectedStatus,
    [property: JsonRequired] ExpectedPageV1 Expected) : CorpusStepV1(StepId);

public sealed record ExpectedGraphSampleV1(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Tolerance);

public sealed record GraphStepV1(
    string StepId,
    [property: JsonRequired] string SourceRegionKey,
    [property: JsonRequired] double DomainMin,
    [property: JsonRequired] double DomainMax,
    [property: JsonRequired] int SampleCount,
    [property: JsonRequired] CorpusGraphDecisionV1 ExpectedDecision,
    [property: JsonRequired] string? ExpectedVariable,
    [property: JsonRequired] IReadOnlyList<ExpectedGraphSampleV1> ExpectedSamples) : CorpusStepV1(StepId);

public sealed record CorpusManifestV1(
    [property: JsonRequired] string Format,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string CorpusVersion,
    [property: JsonRequired] IReadOnlyList<CorpusManifestEntryV1> Entries);

public sealed record CorpusManifestEntryV1(
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] CorpusPartitionV1 Partition,
    [property: JsonRequired] string RelativePath,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] CorpusCaseStatusV1 Status,
    [property: JsonRequired] CorpusContaminationReasonV1? ContaminationReason,
    [property: JsonRequired] string? ReplacementCaseId);

public sealed record ExpressionCorpusSuite(
    CorpusManifestV1 Manifest,
    IReadOnlyList<ExpressionCaseV1> Cases,
    string ManifestSha256);

public sealed record ActualPageV1(
    IReadOnlyList<ActualRegionV1> Regions,
    ActualSheetV1? Sheet);

public sealed record ActualRegionV1(
    string RuntimeRegionHandle,
    IReadOnlyList<string> StrokeIds,
    ActualRegionOutcomeV1 Outcome,
    CorpusBoundsV1? Bounds = null);

public abstract record ActualRegionOutcomeV1;

public sealed record AcceptedRegionActualV1(
    string Latex,
    IReadOnlyList<ActualTokenV1> Tokens,
    ActualLayoutNodeV1? Layout,
    ExpectedEvaluationV1? Cas) : ActualRegionOutcomeV1;

public sealed record RefusedRegionActualV1(
    CorpusFailureCategoryV1 FirstStage,
    CorpusRefusalCodeV1 Reason) : ActualRegionOutcomeV1;

public sealed record ActualTokenV1(
    string Latex,
    IReadOnlyList<string> SourceStrokeIds,
    double Confidence,
    bool Rejected);

public sealed record ActualLayoutNodeV1(
    LayoutKindV1 Kind,
    IReadOnlyList<int> OwnedTokenIndexes,
    IReadOnlyList<ActualLayoutEdgeV1> Children);

public sealed record ActualLayoutEdgeV1(
    LayoutRoleV1 Role,
    ActualLayoutNodeV1 Node);

public sealed record ActualSheetV1(
    IReadOnlyList<ActualSheetNodeV1> Nodes,
    IReadOnlyList<string> ChangedRegionHandles,
    IReadOnlyList<string> CausallyAffectedRegionHandles);

public sealed record ActualSheetNodeV1(
    string RuntimeRegionHandle,
    IReadOnlyList<string> StrokeIds,
    CorpusSheetRoleV1 Role,
    string? DefinedSymbol,
    IReadOnlyList<string> FreeVariables,
    bool IsConflict,
    ExpectedEvaluationV1? Result);

public sealed record ExpressionScenarioInputV1(
    CorpusCaptureSourceV1 Source,
    CorpusDeviceClassV1 DeviceClass,
    CorpusPressureModeV1 PressureMode,
    IReadOnlyList<CorpusStrokeV1> Strokes,
    IReadOnlyList<string> InitialStrokeIds);

public abstract record ScenarioActionV1;

public sealed record AddInkActionV1(IReadOnlyList<string> StrokeIds) : ScenarioActionV1;

public sealed record EraseActionV1(IReadOnlyList<string> StrokeIds) : ScenarioActionV1;

public sealed record RewriteActionV1(
    IReadOnlyList<string> RemovedStrokeIds,
    IReadOnlyList<string> AddedStrokeIds) : ScenarioActionV1;

public sealed record UndoActionV1 : ScenarioActionV1;

public sealed record RedoActionV1 : ScenarioActionV1;

public sealed record RecognizeActionV1 : ScenarioActionV1;

public sealed record StampActionV1(
    string SourceRegionHandle,
    CorpusPointV1 GestureDelta,
    CorpusPointV1 DropPoint) : ScenarioActionV1;

public sealed record TaffyProbeActionV1(
    CorpusPointV1 HitPointWorld,
    double CumulativeScreenDeltaX,
    double CanvasScale) : ScenarioActionV1;

public sealed record SaveActionV1(
    string StoreSlot,
    CorpusSaveModeV1 Mode) : ScenarioActionV1;

public sealed record CloseFlushActionV1(string StoreSlot) : ScenarioActionV1;

public sealed record ReopenActionV1(string StoreSlot) : ScenarioActionV1;

public sealed record RecoverActionV1(
    string StoreSlot,
    CorpusRecoveryDamageV1 Damage) : ScenarioActionV1;

public sealed record GraphActionV1(
    string SourceRegionHandle,
    double DomainMin,
    double DomainMax,
    int SampleCount) : ScenarioActionV1;

public sealed record ActualDocumentStateV1(
    IReadOnlyList<string> LiveStrokeIds,
    IReadOnlyList<string> UserInkStrokeIds,
    IReadOnlyList<string> SynthesizedStrokeIds);

public abstract record StepActualV1;

public sealed record MutationActualV1(ActualDocumentStateV1 State) : StepActualV1;

public sealed record StampActualV1(
    CorpusStampDecisionV1 Decision,
    double? AppliedScale,
    IReadOnlyList<CorpusStrokeV1> SourceStrokes,
    IReadOnlyList<string> RemovedStrokeIds,
    IReadOnlyList<CorpusStrokeV1> AddedStrokes,
    ActualDocumentStateV1 State) : StepActualV1;

public sealed record RecognizeActualV1(ActualPageV1 Actual) : StepActualV1;

public sealed record PersistenceWriteActualV1(bool Completed) : StepActualV1;

public sealed record PersistenceOpenActualV1(
    CorpusOpenStatusV1 Status,
    ActualDocumentStateV1 State,
    ActualPageV1? Page) : StepActualV1;

public sealed record TaffyProbeActualV1(
    string TrialLatex,
    ActualSheetV1 Sheet) : StepActualV1;

public sealed record GraphActualV1(
    CorpusGraphDecisionV1 Decision,
    string? Variable,
    IReadOnlyList<ActualGraphSampleV1> Samples) : StepActualV1;

public sealed record ActualGraphSampleV1(double X, double Y);

public sealed record CapabilityUnavailableActualV1(
    CorpusCapabilityV1 Capability) : StepActualV1;

public sealed record FailedStepActualV1(
    CorpusFailureCategoryV1 Category,
    string ErrorCode) : StepActualV1;
