using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penumbra.ExpressionCorpus;

public enum CorpusRunProfileV1
{
    DiagnosticAccuracy,
    Slice3DevelopmentReadiness,
}

public enum CorpusCoverageFeatureV1
{
    RealPenCapture,
    AcceptedRecognition,
    ExpectedRefusal,
    MultiLineDependency,
    Erase,
    Rewrite,
    Undo,
    Redo,
    Stamp,
    Taffy,
    Autosave,
    SaveReopen,
    Recovery,
    GraphInput,
}

public sealed record CorpusMetricSummaryV1(
    string Operation,
    int SampleCount,
    int CompletedCount,
    int CancelledCount,
    int RefusedCount,
    int FailedCount,
    double? CompletedMillisecondsP50,
    double? CompletedMillisecondsP95);

public sealed record CorpusRunReport(
    int ReportVersion,
    string RunnerFingerprint,
    string PipelineFingerprint,
    string ModelFingerprint,
    string CorpusFingerprint,
    double? RecognitionThreshold,
    CorpusPartitionV1 Partition,
    CorpusRunProfileV1 Profile,
    int ValidatedCaseCount,
    int ExecutedCaseCount,
    int CheckpointCount,
    int ExactExpressionNumerator,
    int ExactExpressionDenominator,
    double? ExactExpressionRate,
    double RequiredExactExpressionRate,
    int AcceptedCount,
    int AcceptedWrongCount,
    int RefusalCount,
    int ExpectedRefusalPassCount,
    int UnexpectedRefusalCount,
    int UnexpectedAcceptanceCount,
    int RefusalMismatchCount,
    int StructuralMismatchCount,
    IReadOnlyDictionary<CorpusFailureCategoryV1, int> Failures,
    IReadOnlyDictionary<CorpusCapabilityV1, int> UnavailableCapabilities,
    IReadOnlyList<CorpusMetricSummaryV1> Metrics,
    bool MetricCoverageRequired,
    long MetricObservationTotal,
    int MetricObservationRetained,
    long MetricObservationDropped,
    int MissingMetricOperationCount,
    IReadOnlyDictionary<CorpusCoverageFeatureV1, int> Coverage,
    int MissingCoverageFeatureCount,
    int LatencyBudgetViolationCount,
    bool InfrastructureValid,
    bool AccuracyPassed,
    bool CoveragePassed,
    bool LatencyPassed,
    bool ProfilePassed,
    bool ReadinessPassed);

public static class CorpusReportJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(CorpusRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
