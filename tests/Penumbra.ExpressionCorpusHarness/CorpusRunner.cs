using System.Security.Cryptography;
using System.Text;
using Penumbra.Core;

namespace Penumbra.ExpressionCorpus;

public sealed record CorpusRunOptions(
    CorpusPartitionV1 Partition,
    int MetricsCapacity = 100_000,
    double? RequiredExactExpressionRate = null,
    bool RequireMetricCoverage = true,
    CorpusRunProfileV1 Profile = CorpusRunProfileV1.DiagnosticAccuracy,
    TimeSpan? StepTimeout = null,
    TimeSpan? CaseTimeout = null,
    TimeSpan? SuiteTimeout = null,
    TimeSpan? DisposalTimeout = null);

public sealed class ExpressionCorpusRunner
{
    public const string RunnerFingerprint = "expression-corpus-runner-v2";
    public static readonly TimeSpan MaximumStepTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumCaseTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumSuiteTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumDisposalTimeout = TimeSpan.FromSeconds(5);

    public async Task<CorpusRunReport> RunAsync(
        ExpressionCorpusSuite suite,
        IExpressionScenarioRuntimeFactory runtimeFactory,
        CorpusRunOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MetricsCapacity is <= 0 or > CorpusResourceLimitsV1.MaximumMetricCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Metrics capacity must be in [1, {CorpusResourceLimitsV1.MaximumMetricCapacity}].");
        }
        if (!Enum.IsDefined(options.Partition))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Corpus partition must be defined.");
        }
        if (!Enum.IsDefined(options.Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Corpus run profile must be defined.");
        }
        if (options.Profile == CorpusRunProfileV1.Slice3DevelopmentReadiness
            && (options.Partition != CorpusPartitionV1.Development || !options.RequireMetricCoverage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The Slice 3 readiness profile requires the development partition and metric coverage.");
        }
        TimeSpan stepTimeout = ResolveTimeout(options.StepTimeout, MaximumStepTimeout, nameof(options.StepTimeout));
        TimeSpan caseTimeout = ResolveTimeout(options.CaseTimeout, MaximumCaseTimeout, nameof(options.CaseTimeout));
        TimeSpan suiteTimeout = ResolveTimeout(options.SuiteTimeout, MaximumSuiteTimeout, nameof(options.SuiteTimeout));
        TimeSpan disposalTimeout = ResolveTimeout(
            options.DisposalTimeout,
            MaximumDisposalTimeout,
            nameof(options.DisposalTimeout));

        double partitionExactRateFloor = options.Partition == CorpusPartitionV1.HeldOut ? 0.85 : 1.0;
        double requiredExactRate = options.RequiredExactExpressionRate ?? partitionExactRateFloor;
        if (!double.IsFinite(requiredExactRate)
            || requiredExactRate < partitionExactRateFloor
            || requiredExactRate > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Required exact-expression rate cannot weaken the partition gate profile.");
        }

        var failures = Enum.GetValues<CorpusFailureCategoryV1>()
            .ToDictionary(category => category, _ => 0);
        var unavailableCapabilities = Enum.GetValues<CorpusCapabilityV1>()
            .ToDictionary(capability => capability, _ => 0);
        using var suiteDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        suiteDeadline.CancelAfter(suiteTimeout);
        RuntimeIdentity runtimeIdentity;
        try
        {
            runtimeIdentity = await ReadRuntimeIdentityAsync(
                runtimeFactory,
                stepTimeout,
                suiteDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            runtimeIdentity = RuntimeIdentity.Invalid;
        }
        IReadOnlyList<CorpusValidationError> validation = CorpusValidator.ValidateSuite(suite);
        if (validation.Count > 0)
        {
            failures[CorpusFailureCategoryV1.CorpusFormat] = validation.Count;
            return BuildReport(
                suite, runtimeIdentity, options.Partition, options.Profile, requiredExactRate,
                failures, unavailableCapabilities, [],
                validatedCases: 0, executedCases: 0, checkpoints: 0,
                exactNumerator: 0, exactDenominator: 0, accepted: 0, acceptedWrong: 0,
                refusals: 0, expectedRefusals: 0, unexpectedRefusals: 0,
                unexpectedAcceptances: 0, refusalMismatches: 0, structuralMismatches: 0,
                metricTotal: 0, metricRetained: 0, missingMetricOperations: 0,
                coverage: EmptyCoverage(), missingCoverageFeatures: RequiredCoverage(options.Profile).Count,
                latencyBudgetViolations: 0,
                metricCoverageRequired: options.RequireMetricCoverage,
                infrastructureValid: false,
                corpusValidated: false);
        }

        Dictionary<string, CorpusManifestEntryV1> entries = suite.Manifest.Entries.ToDictionary(
            entry => entry.CaseId,
            StringComparer.Ordinal);
        ExpressionCaseV1[] eligible = suite.Cases
            .Where(item => item.Partition == options.Partition)
            .Where(item => options.Partition == CorpusPartitionV1.Development
                ? entries[item.CaseId].Status == CorpusCaseStatusV1.Development
                : entries[item.CaseId].Status == CorpusCaseStatusV1.Frozen)
            .ToArray();

        IReadOnlyDictionary<CorpusCoverageFeatureV1, int> coverage = CountCoverage(eligible);
        int missingCoverageFeatures = RequiredCoverage(options.Profile)
            .Count(feature => coverage[feature] == 0);

        int expectedCheckpointCount = eligible.Sum(CountCheckpoints);
        var requiredMetrics = new Dictionary<MetricOperation, int>();
        foreach (CorpusStepV1 step in eligible.SelectMany(item => item.Steps))
        {
            AddRequiredMetrics(step, requiredMetrics);
        }

        var metrics = new CountingBoundedMetricsSink(options.MetricsCapacity);
        int executedCases = 0;
        int checkpoints = 0;
        int exactNumerator = 0;
        int exactDenominator = 0;
        int accepted = 0;
        int acceptedWrong = 0;
        int refusals = 0;
        int expectedRefusals = 0;
        int unexpectedRefusals = 0;
        int unexpectedAcceptances = 0;
        int refusalMismatches = 0;
        int structuralMismatches = 0;
        bool infrastructureValid = true;

        if (eligible.Length == 0 || expectedCheckpointCount == 0)
        {
            failures[CorpusFailureCategoryV1.CorpusFormat]++;
            structuralMismatches++;
        }
        if (!runtimeIdentity.IsValid || !ValidSha256(suite.ManifestSha256))
        {
            failures[CorpusFailureCategoryV1.Infrastructure]++;
            infrastructureValid = false;
        }

        void ApplyPageComparison(ExpectedPageV1 expected, ActualPageV1 actual)
        {
            checkpoints++;
            if (!ActualResultContractV1.IsPageValid(actual))
            {
                failures[CorpusFailureCategoryV1.Infrastructure]++;
                structuralMismatches++;
                infrastructureValid = false;
                return;
            }
            PageComparison page = CorpusComparison.Compare(
                expected,
                actual,
                runtimeIdentity.ComparisonThreshold);
            foreach (ObservationComparison comparison in page.Observations)
            {
                if (comparison.ExpectedAccepted)
                {
                    exactDenominator++;
                    if (comparison.RecognitionExact)
                    {
                        exactNumerator++;
                    }
                }
                if (comparison.ActualAccepted)
                {
                    accepted++;
                }
                if (comparison.ActualAccepted && comparison.AcceptedWrong)
                {
                    acceptedWrong++;
                }
                if (comparison.ActualRefused)
                {
                    refusals++;
                }
                if (comparison.ExpectedRefusalPass)
                {
                    expectedRefusals++;
                }
                if (comparison.UnexpectedRefusal)
                {
                    unexpectedRefusals++;
                }
                if (comparison.UnexpectedAcceptance)
                {
                    unexpectedAcceptances++;
                }
                if (comparison.HasExpectedObservation
                    && !comparison.ExpectedAccepted
                    && comparison.ActualRefused
                    && !comparison.ExpectedRefusalPass)
                {
                    refusalMismatches++;
                }
                if (!comparison.HasExpectedObservation
                    || (comparison.HasExpectedObservation
                        && !comparison.ActualAccepted
                        && !comparison.ActualRefused))
                {
                    structuralMismatches++;
                }
                if (comparison.PrimaryFailure is { } category)
                {
                    failures[category]++;
                }
            }

            foreach (CorpusFailureCategoryV1 category in page.AdditionalFailures)
            {
                failures[category]++;
                if (!page.Observations.Any(item => item.AcceptedWrong))
                {
                    structuralMismatches++;
                }
            }
        }

        foreach (ExpressionCaseV1 @case in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (suiteDeadline.IsCancellationRequested)
            {
                failures[CorpusFailureCategoryV1.Infrastructure]++;
                infrastructureValid = false;
                break;
            }
            bool caseReachedEnd = true;
            IExpressionScenarioRuntime? runtime = null;
            using var caseDeadline = CancellationTokenSource.CreateLinkedTokenSource(suiteDeadline.Token);
            caseDeadline.CancelAfter(caseTimeout);
            try
            {
                var input = new ExpressionScenarioInputV1(
                    @case.Capture.Source,
                    @case.Capture.DeviceClass,
                    @case.Capture.PressureMode,
                    @case.Strokes,
                    @case.InitialStrokeIds);
                var expectedState = new ExpectedScenarioState(@case);
                runtime = await AwaitBoundedAsync(
                    Task.Run(() => runtimeFactory.Create(input, metrics), CancellationToken.None),
                    stepTimeout,
                    caseDeadline.Token).ConfigureAwait(false);
                foreach (CorpusStepV1 step in @case.Steps)
                {
                    caseDeadline.Token.ThrowIfCancellationRequested();
                    ScenarioActionV1 action = expectedState.CreateAction(step);
                    StepActualV1 actual = await AwaitBoundedAsync(
                        Task.Run(
                            () => runtime.ApplyAsync(action, caseDeadline.Token),
                            CancellationToken.None),
                        stepTimeout,
                        caseDeadline.Token).ConfigureAwait(false);

                    if (actual is CapabilityUnavailableActualV1 unavailable)
                    {
                        if (Enum.IsDefined(unavailable.Capability))
                        {
                            unavailableCapabilities[unavailable.Capability]++;
                        }
                        failures[CorpusFailureCategoryV1.Infrastructure]++;
                        structuralMismatches++;
                        infrastructureValid = false;
                        caseReachedEnd = false;
                        break;
                    }
                    if (actual is FailedStepActualV1 failed)
                    {
                        CorpusFailureCategoryV1 category = Enum.IsDefined(failed.Category)
                            && IsBoundedText(failed.ErrorCode)
                            ? failed.Category
                            : CorpusFailureCategoryV1.Infrastructure;
                        failures[category]++;
                        structuralMismatches++;
                        if (category is CorpusFailureCategoryV1.Infrastructure
                            or CorpusFailureCategoryV1.CorpusFormat)
                        {
                            infrastructureValid = false;
                        }
                        caseReachedEnd = false;
                        break;
                    }
                    if (!StepActualContractValid(actual))
                    {
                        failures[CorpusFailureCategoryV1.Infrastructure]++;
                        structuralMismatches++;
                        infrastructureValid = false;
                        caseReachedEnd = false;
                        break;
                    }

                    bool stampEvidenceExact = step is not StampStepV1 stampStep
                        || actual is StampActualV1 stampActual
                            && expectedState.BindStampResult(stampStep, stampActual);
                    expectedState.Advance(step);
                    bool compatible = true;
                    switch (step, actual)
                    {
                        case (RecognizeStepV1 recognize, RecognizeActualV1 recognition):
                            ExpectedPageV1 translatedRecognition =
                                expectedState.TranslateExpectedPage(recognize.Expected);
                            ApplyPageComparison(translatedRecognition, recognition.Actual);
                            expectedState.ObserveRegions(translatedRecognition, recognition.Actual);
                            break;
                        case (AddInkStepV1 or EraseStepV1 or RewriteStepV1
                            or UndoStepV1 or RedoStepV1, MutationActualV1 mutation):
                            if (!expectedState.DocumentStateEquals(mutation.State))
                            {
                                failures[CorpusFailureCategoryV1.UiIntegration]++;
                                structuralMismatches++;
                            }
                            break;
                        case (StampStepV1, StampActualV1 stamped):
                            if (!stampEvidenceExact || !expectedState.DocumentStateEquals(stamped.State))
                            {
                                failures[CorpusFailureCategoryV1.UiIntegration]++;
                                structuralMismatches++;
                            }
                            break;
                        case (SaveStepV1 or CloseFlushStepV1, PersistenceWriteActualV1 write):
                            if (!write.Completed)
                            {
                                failures[CorpusFailureCategoryV1.Persistence]++;
                                structuralMismatches++;
                            }
                            break;
                        case (ReopenStepV1 reopen, PersistenceOpenActualV1 opened):
                            if (opened.Status != reopen.ExpectedStatus
                                || !expectedState.DocumentStateEquals(opened.State))
                            {
                                failures[CorpusFailureCategoryV1.Persistence]++;
                                structuralMismatches++;
                            }
                            if (reopen.Expected is not null)
                            {
                                if (opened.Page is null)
                                {
                                    failures[CorpusFailureCategoryV1.Persistence]++;
                                    structuralMismatches++;
                                }
                                else
                                {
                                    ExpectedPageV1 translatedReopen =
                                        expectedState.TranslateExpectedPage(reopen.Expected);
                                    ApplyPageComparison(translatedReopen, opened.Page);
                                    expectedState.ObserveRegions(translatedReopen, opened.Page);
                                }
                            }
                            break;
                        case (RecoverStepV1 recover, PersistenceOpenActualV1 recovered):
                            if (recovered.Status != recover.ExpectedStatus
                                || !expectedState.DocumentStateEquals(recovered.State)
                                || recovered.Page is null)
                            {
                                failures[CorpusFailureCategoryV1.Persistence]++;
                                structuralMismatches++;
                            }
                            else
                            {
                                ExpectedPageV1 translatedRecovery =
                                    expectedState.TranslateExpectedPage(recover.Expected);
                                ApplyPageComparison(translatedRecovery, recovered.Page);
                                expectedState.ObserveRegions(translatedRecovery, recovered.Page);
                            }
                            break;
                        case (TaffyProbeStepV1 taffy, TaffyProbeActualV1 probe):
                            if (!string.Equals(taffy.TrialLatex, probe.TrialLatex, StringComparison.Ordinal)
                                || !CorpusComparison.SheetEquals(
                                    taffy.ExpectedSheet,
                                    probe.Sheet,
                                    expectedState.RegionStrokes,
                                    expectedState.RegionHandles))
                            {
                                failures[CorpusFailureCategoryV1.Sheet]++;
                                structuralMismatches++;
                            }
                            break;
                        case (GraphStepV1 graph, GraphActualV1 graphActual):
                            if (!GraphEquals(graph, graphActual))
                            {
                                failures[CorpusFailureCategoryV1.Graph]++;
                                structuralMismatches++;
                            }
                            break;
                        default:
                            compatible = false;
                            failures[CorpusFailureCategoryV1.Infrastructure]++;
                            infrastructureValid = false;
                            structuralMismatches++;
                            break;
                    }

                    if (!compatible)
                    {
                        caseReachedEnd = false;
                        break;
                    }
                }

                if (caseReachedEnd)
                {
                    executedCases++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failures[CorpusFailureCategoryV1.Infrastructure]++;
                infrastructureValid = false;
            }
            finally
            {
                if (runtime is not null)
                {
                    try
                    {
                        Task disposal = Task.Run(
                            async () => await runtime.DisposeAsync().ConfigureAwait(false),
                            CancellationToken.None);
                        await AwaitBoundedAsync(
                            disposal,
                            disposalTimeout,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        failures[CorpusFailureCategoryV1.Infrastructure]++;
                        infrastructureValid = false;
                    }
                }
            }
        }

        (long metricTotal, LocalMetricsSnapshot snapshot) = metrics.SealAndCapture();
        long metricDropped = Math.Max(0, metricTotal - snapshot.SampleCount);
        if (metricDropped > 0)
        {
            failures[CorpusFailureCategoryV1.Infrastructure]++;
            infrastructureValid = false;
        }

        int missingMetricOperations = 0;
        if (options.RequireMetricCoverage)
        {
            foreach ((MetricOperation operation, int minimumCount) in requiredMetrics)
            {
                if (snapshot.SummaryFor(operation).CompletedCount < minimumCount)
                {
                    missingMetricOperations++;
                }
            }
            if (missingMetricOperations > 0)
            {
                failures[CorpusFailureCategoryV1.Infrastructure]++;
                infrastructureValid = false;
            }
        }

        CorpusMetricSummaryV1[] metricSummaries = snapshot.Summaries
            .Select(ToReportSummary)
            .ToArray();
        int latencyBudgetViolations = options.Profile == CorpusRunProfileV1.Slice3DevelopmentReadiness
            ? CountLatencyBudgetViolations(metricSummaries)
            : 0;
        if (executedCases != eligible.Length || checkpoints != expectedCheckpointCount)
        {
            failures[CorpusFailureCategoryV1.Infrastructure]++;
            infrastructureValid = false;
        }

        return BuildReport(
            suite, runtimeIdentity, options.Partition, options.Profile, requiredExactRate,
            failures, unavailableCapabilities, metricSummaries,
            eligible.Length, executedCases, checkpoints,
            exactNumerator, exactDenominator, accepted, acceptedWrong, refusals,
            expectedRefusals, unexpectedRefusals, unexpectedAcceptances,
            refusalMismatches, structuralMismatches,
            metricTotal, snapshot.SampleCount, missingMetricOperations,
            coverage, missingCoverageFeatures, latencyBudgetViolations,
            options.RequireMetricCoverage,
            infrastructureValid,
            corpusValidated: true);
    }

    private static CorpusRunReport BuildReport(
        ExpressionCorpusSuite suite,
        RuntimeIdentity runtimeIdentity,
        CorpusPartitionV1 partition,
        CorpusRunProfileV1 profile,
        double requiredExactRate,
        IReadOnlyDictionary<CorpusFailureCategoryV1, int> failures,
        IReadOnlyDictionary<CorpusCapabilityV1, int> unavailableCapabilities,
        IReadOnlyList<CorpusMetricSummaryV1> metrics,
        int validatedCases,
        int executedCases,
        int checkpoints,
        int exactNumerator,
        int exactDenominator,
        int accepted,
        int acceptedWrong,
        int refusals,
        int expectedRefusals,
        int unexpectedRefusals,
        int unexpectedAcceptances,
        int refusalMismatches,
        int structuralMismatches,
        long metricTotal,
        int metricRetained,
        int missingMetricOperations,
        IReadOnlyDictionary<CorpusCoverageFeatureV1, int> coverage,
        int missingCoverageFeatures,
        int latencyBudgetViolations,
        bool metricCoverageRequired,
        bool infrastructureValid,
        bool corpusValidated)
    {
        double? exactRate = exactDenominator == 0 ? null : (double)exactNumerator / exactDenominator;
        bool accuracyPassed = infrastructureValid
            && validatedCases > 0
            && executedCases == validatedCases
            && checkpoints > 0
            && exactRate is not null
            && exactRate.Value >= requiredExactRate
            && acceptedWrong == 0
            && unexpectedAcceptances == 0
            && refusalMismatches == 0
            && structuralMismatches == 0
            && failures[CorpusFailureCategoryV1.CorpusFormat] == 0
            && failures[CorpusFailureCategoryV1.Infrastructure] == 0;
        bool coveragePassed = profile == CorpusRunProfileV1.DiagnosticAccuracy
            || missingCoverageFeatures == 0;
        bool latencyPassed = profile == CorpusRunProfileV1.DiagnosticAccuracy
            || latencyBudgetViolations == 0;
        bool profilePassed = accuracyPassed && coveragePassed && latencyPassed;
        bool readinessPassed = profile == CorpusRunProfileV1.Slice3DevelopmentReadiness
            && profilePassed;
        return new CorpusRunReport(
            ReportVersion: 2,
            RunnerFingerprint,
            runtimeIdentity.PipelineFingerprint,
            runtimeIdentity.ModelFingerprint,
            corpusValidated ? SafeSha256(suite.ManifestSha256) : new string('0', 64),
            runtimeIdentity.ReportedThreshold,
            partition,
            profile,
            validatedCases,
            executedCases,
            checkpoints,
            exactNumerator,
            exactDenominator,
            exactRate,
            requiredExactRate,
            accepted,
            acceptedWrong,
            refusals,
            expectedRefusals,
            unexpectedRefusals,
            unexpectedAcceptances,
            refusalMismatches,
            structuralMismatches,
            failures,
            unavailableCapabilities,
            metrics,
            metricCoverageRequired,
            metricTotal,
            metricRetained,
            Math.Max(0, metricTotal - metricRetained),
            missingMetricOperations,
            coverage,
            missingCoverageFeatures,
            latencyBudgetViolations,
            infrastructureValid,
            accuracyPassed,
            coveragePassed,
            latencyPassed,
            profilePassed,
            readinessPassed);
    }

    private static int CountCheckpoints(ExpressionCaseV1 @case) => @case.Steps.Sum(step => step switch
    {
        RecognizeStepV1 => 1,
        ReopenStepV1 { Expected: not null } => 1,
        RecoverStepV1 => 1,
        _ => 0,
    });

    private static IReadOnlyDictionary<CorpusCoverageFeatureV1, int> EmptyCoverage() =>
        Enum.GetValues<CorpusCoverageFeatureV1>().ToDictionary(feature => feature, _ => 0);

    private static IReadOnlyList<CorpusCoverageFeatureV1> RequiredCoverage(CorpusRunProfileV1 profile) =>
        profile == CorpusRunProfileV1.Slice3DevelopmentReadiness
            ? Enum.GetValues<CorpusCoverageFeatureV1>()
            : [];

    private static IReadOnlyDictionary<CorpusCoverageFeatureV1, int> CountCoverage(
        IReadOnlyList<ExpressionCaseV1> cases)
    {
        Dictionary<CorpusCoverageFeatureV1, int> counts = Enum.GetValues<CorpusCoverageFeatureV1>()
            .ToDictionary(feature => feature, _ => 0);

        void Add(CorpusCoverageFeatureV1 feature, int amount = 1) => counts[feature] += amount;
        void AddPage(ExpectedPageV1? page)
        {
            if (page?.Regions is null)
            {
                return;
            }
            Add(CorpusCoverageFeatureV1.AcceptedRecognition,
                page.Regions.Count(region => region?.Expectation is AcceptedRegionExpectationV1));
            Add(CorpusCoverageFeatureV1.ExpectedRefusal,
                page.Regions.Count(region => region?.Expectation is RefusedRegionExpectationV1));
            if (page.Regions.Count >= 2 && page.Sheet?.Nodes is { Count: >= 2 } sheetNodes)
            {
                Dictionary<string, string> definitions = sheetNodes
                    .Where(node => node is
                    {
                        Role: CorpusSheetRoleV1.Definition,
                        DefinedSymbol: not null,
                    })
                    .GroupBy(node => node.DefinedSymbol!, StringComparer.Ordinal)
                    .Where(group => group.Count() == 1)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Single().RegionKey,
                        StringComparer.Ordinal);
                bool hasCrossRegionDependency = sheetNodes.Any(node =>
                    (node.FreeVariables ?? []).Any(variable =>
                        definitions.TryGetValue(variable, out string? definitionRegion)
                        && !string.Equals(definitionRegion, node.RegionKey, StringComparison.Ordinal)));
                if (hasCrossRegionDependency)
                {
                    Add(CorpusCoverageFeatureV1.MultiLineDependency);
                }
            }
        }

        foreach (ExpressionCaseV1 @case in cases)
        {
            if (@case.Capture?.Source == CorpusCaptureSourceV1.UserRealPen)
            {
                Add(CorpusCoverageFeatureV1.RealPenCapture);
            }
            foreach (CorpusStepV1 step in @case.Steps ?? [])
            {
                switch (step)
                {
                    case RecognizeStepV1 recognize:
                        AddPage(recognize.Expected);
                        break;
                    case EraseStepV1:
                        Add(CorpusCoverageFeatureV1.Erase);
                        break;
                    case RewriteStepV1:
                        Add(CorpusCoverageFeatureV1.Rewrite);
                        break;
                    case UndoStepV1:
                        Add(CorpusCoverageFeatureV1.Undo);
                        break;
                    case RedoStepV1:
                        Add(CorpusCoverageFeatureV1.Redo);
                        break;
                    case StampStepV1:
                        Add(CorpusCoverageFeatureV1.Stamp);
                        break;
                    case TaffyProbeStepV1:
                        Add(CorpusCoverageFeatureV1.Taffy);
                        break;
                    case SaveStepV1 { Mode: CorpusSaveModeV1.Autosave }:
                        Add(CorpusCoverageFeatureV1.Autosave);
                        break;
                    case ReopenStepV1 reopen:
                        if (reopen.Expected is not null)
                        {
                            Add(CorpusCoverageFeatureV1.SaveReopen);
                            AddPage(reopen.Expected);
                        }
                        break;
                    case RecoverStepV1 recover:
                        Add(CorpusCoverageFeatureV1.Recovery);
                        AddPage(recover.Expected);
                        break;
                    case GraphStepV1:
                        Add(CorpusCoverageFeatureV1.GraphInput);
                        break;
                }
            }
        }
        return counts;
    }

    private static int CountLatencyBudgetViolations(IReadOnlyList<CorpusMetricSummaryV1> metrics)
    {
        var budgets = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [MetricOperation.RecognitionProcessing.ToString()] = 250,
            [MetricOperation.RecognitionGrammar.ToString()] = 25,
            [MetricOperation.SheetRecompute.ToString()] = 100,
            [MetricOperation.TaffyProcessing.ToString()] = 100,
        };
        Dictionary<string, CorpusMetricSummaryV1> byOperation = metrics.ToDictionary(
            metric => metric.Operation,
            StringComparer.Ordinal);
        return budgets.Count(pair => !byOperation.TryGetValue(pair.Key, out CorpusMetricSummaryV1? summary)
            || summary.CompletedMillisecondsP95 is null
            || summary.CompletedMillisecondsP95.Value > pair.Value);
    }

    private static void AddRequiredMetrics(CorpusStepV1 step, IDictionary<MetricOperation, int> required)
    {
        void Require(MetricOperation operation)
        {
            required.TryGetValue(operation, out int current);
            required[operation] = current + 1;
        }
        void RequireRecognition(ExpectedPageV1 expected)
        {
            Require(MetricOperation.RecognitionProcessing);
            Require(MetricOperation.RecognitionPartition);
            // Empty pages have no symbol groups, so the real pipeline correctly performs no classifier
            // or grammar work. Non-empty pages must still prove both stages on every authoritative pass.
            if (expected.Regions.Count > 0)
            {
                Require(MetricOperation.RecognitionClassification);
                Require(MetricOperation.RecognitionGrammar);
            }
            if (expected.Sheet is not null)
            {
                Require(MetricOperation.SheetRecompute);
            }
        }

        switch (step)
        {
            case RecognizeStepV1 recognize:
                RequireRecognition(recognize.Expected);
                break;
            case SaveStepV1 { Mode: CorpusSaveModeV1.Explicit }:
                Require(MetricOperation.ExplicitSave);
                break;
            case SaveStepV1:
                Require(MetricOperation.Autosave);
                break;
            case CloseFlushStepV1:
                Require(MetricOperation.CloseFlush);
                break;
            case ReopenStepV1 reopen:
                Require(MetricOperation.RecoveryRead);
                if (reopen.Expected is not null)
                {
                    RequireRecognition(reopen.Expected);
                }
                break;
            case RecoverStepV1 recover:
                Require(MetricOperation.RecoveryRead);
                RequireRecognition(recover.Expected);
                break;
            case TaffyProbeStepV1:
                Require(MetricOperation.TaffyProcessing);
                Require(MetricOperation.TaffyProbe);
                Require(MetricOperation.TaffyGhostSynthesis);
                Require(MetricOperation.TaffyPublication);
                break;
            case GraphStepV1:
                Require(MetricOperation.GraphDetection);
                Require(MetricOperation.GraphSampling);
                break;
        }
    }

    private static bool GraphEquals(GraphStepV1 expected, GraphActualV1 actual)
    {
        if (expected.ExpectedDecision != actual.Decision
            || !string.Equals(expected.ExpectedVariable, actual.Variable, StringComparison.Ordinal)
            || actual.Samples is null
            || actual.Samples.Count > CorpusResourceLimitsV1.MaximumGraphSamples
            || actual.Samples.Any(sample => sample is null
                || !double.IsFinite(sample.X)
                || !double.IsFinite(sample.Y)
                || sample.X < expected.DomainMin
                || sample.X > expected.DomainMax)
            || !actual.Samples.Zip(actual.Samples.Skip(1))
                .All(pair => pair.First.X < pair.Second.X))
        {
            return false;
        }
        if (expected.ExpectedDecision == CorpusGraphDecisionV1.Refuse)
        {
            return actual.Samples.Count == 0;
        }
        if (actual.Samples.Count != expected.SampleCount
            || expected.ExpectedSamples.Count > actual.Samples.Count)
        {
            return false;
        }

        int nextActualIndex = 0;
        for (int anchorIndex = 0; anchorIndex < expected.ExpectedSamples.Count; anchorIndex++)
        {
            ExpectedGraphSampleV1 anchor = expected.ExpectedSamples[anchorIndex];
            int match = -1;
            for (int actualIndex = nextActualIndex; actualIndex < actual.Samples.Count; actualIndex++)
            {
                ActualGraphSampleV1 sample = actual.Samples[actualIndex];
                if (Math.Abs(sample.X - anchor.X) <= anchor.Tolerance
                    && Math.Abs(sample.Y - anchor.Y) <= anchor.Tolerance)
                {
                    match = actualIndex;
                    break;
                }
            }
            if (match < 0)
            {
                return false;
            }
            nextActualIndex = match + 1;
        }
        return true;
    }

    private static bool StepActualContractValid(StepActualV1? actual) =>
        ActualResultContractV1.IsStepValid(actual);

    private static bool IsBoundedText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= CorpusResourceLimitsV1.MaximumTextLength;

    private static bool IsBoundedId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64;

    private static CorpusMetricSummaryV1 ToReportSummary(MetricOperationSummary summary) => new(
        summary.Operation.ToString(),
        summary.SampleCount,
        summary.CompletedCount,
        summary.CancelledCount,
        summary.RefusedCount,
        summary.FailedCount,
        summary.CompletedDurationP50?.TotalMilliseconds,
        summary.CompletedDurationP95?.TotalMilliseconds);

    private static bool ValidPipelineFingerprint(string? value) =>
        value is not null
        && !string.Equals(value, "unavailable", StringComparison.Ordinal)
        && value.Length is >= 1 and <= 128
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.');

    private static bool ValidSha256(string? value) =>
        value is not null
        && value.Length == 64
        && value.All(Uri.IsHexDigit)
        && value.Any(character => character != '0');

    private static string SafePipelineFingerprint(string? value) =>
        ValidPipelineFingerprint(value)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value!))).ToLowerInvariant()
            : new string('0', 64);

    private static string SafeSha256(string? value) =>
        ValidSha256(value) ? value!.ToLowerInvariant() : new string('0', 64);

    private static TimeSpan ResolveTimeout(TimeSpan? requested, TimeSpan maximum, string parameterName)
    {
        TimeSpan value = requested ?? maximum;
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Timeout must be in (0, {maximum}].");
        }
        return value;
    }

    private static async Task<T> AwaitBoundedAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ObserveLateFault(task);
            throw;
        }
    }

    private static async Task AwaitBoundedAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ObserveLateFault(task);
            throw;
        }
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<RuntimeIdentity> ReadRuntimeIdentityAsync(
        IExpressionScenarioRuntimeFactory factory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        (string? Pipeline, string? Model, double Threshold) identity = await AwaitBoundedAsync(
            Task.Run(
                () => (factory.PipelineFingerprint, factory.ModelFingerprint, factory.RecognitionThreshold),
                CancellationToken.None),
            timeout,
            cancellationToken).ConfigureAwait(false);

        bool valid = ValidPipelineFingerprint(identity.Pipeline)
            && ValidSha256(identity.Model)
            && double.IsFinite(identity.Threshold)
            && identity.Threshold is > 0 and <= 1;
        double? reportedThreshold = double.IsFinite(identity.Threshold) && identity.Threshold is > 0 and <= 1
            ? identity.Threshold
            : null;
        return new RuntimeIdentity(
            SafePipelineFingerprint(identity.Pipeline),
            SafeSha256(identity.Model),
            valid ? identity.Threshold : 1,
            reportedThreshold,
            valid);
    }

    private sealed record RuntimeIdentity(
        string PipelineFingerprint,
        string ModelFingerprint,
        double ComparisonThreshold,
        double? ReportedThreshold,
        bool IsValid)
    {
        public static RuntimeIdentity Invalid { get; } = new(
            new string('0', 64),
            new string('0', 64),
            1,
            null,
            false);
    }

    private sealed class ExpectedScenarioState
    {
        private readonly HashSet<string> _rawIds;
        private readonly HashSet<string> _synthesizedIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aliasToActual = new(StringComparer.Ordinal);
        private readonly Stack<HashSet<string>> _undo = new();
        private readonly Stack<HashSet<string>> _redo = new();
        private readonly Dictionary<string, StoreState> _stores = new(StringComparer.Ordinal);
        private HashSet<string> _live;

        public ExpectedScenarioState(ExpressionCaseV1 @case)
        {
            _rawIds = @case.Strokes.Select(stroke => stroke.StrokeId).ToHashSet(StringComparer.Ordinal);
            _live = @case.InitialStrokeIds.ToHashSet(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> RegionStrokes { get; private set; } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> RegionHandles { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public ExpectedPageV1 TranslateExpectedPage(ExpectedPageV1 page) => page with
        {
            Regions = page.Regions.Select(region => region with
            {
                StrokeIds = Translate(region.StrokeIds),
                Expectation = region.Expectation is AcceptedRegionExpectationV1 accepted
                    ? accepted with
                    {
                        Tokens = accepted.Tokens.Select(token => token with
                        {
                            SourceStrokeIds = Translate(token.SourceStrokeIds),
                        }).ToArray(),
                    }
                    : region.Expectation,
            }).ToArray(),
        };

        public ScenarioActionV1 CreateAction(CorpusStepV1 step) => step switch
        {
            AddInkStepV1 add => new AddInkActionV1(Translate(add.StrokeIds)),
            EraseStepV1 erase => new EraseActionV1(Translate(erase.StrokeIds)),
            RewriteStepV1 rewrite => new RewriteActionV1(
                Translate(rewrite.RemovedStrokeIds),
                Translate(rewrite.AddedStrokeIds)),
            UndoStepV1 => new UndoActionV1(),
            RedoStepV1 => new RedoActionV1(),
            RecognizeStepV1 => new RecognizeActionV1(),
            StampStepV1 stamp => new StampActionV1(
                RegionHandles[stamp.SourceRegionKey], stamp.GestureDelta, stamp.DropPoint),
            TaffyProbeStepV1 taffy => new TaffyProbeActionV1(
                taffy.HitPointWorld,
                taffy.CumulativeScreenDeltaX,
                taffy.CanvasScale),
            SaveStepV1 save => new SaveActionV1(save.StoreSlot, save.Mode),
            CloseFlushStepV1 close => new CloseFlushActionV1(close.StoreSlot),
            ReopenStepV1 reopen => new ReopenActionV1(reopen.StoreSlot),
            RecoverStepV1 recover => new RecoverActionV1(recover.StoreSlot, recover.Damage),
            GraphStepV1 graph => new GraphActionV1(
                RegionHandles[graph.SourceRegionKey], graph.DomainMin, graph.DomainMax, graph.SampleCount),
            _ => throw new InvalidOperationException("Unsupported corpus step."),
        };

        public void ObserveRegions(ExpectedPageV1 expected, ActualPageV1 actual)
        {
            var handles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ExpectedRegionV1 expectedRegion in expected.Regions)
            {
                ActualRegionV1? match = actual.Regions.SingleOrDefault(region =>
                    StrictSetEquals(expectedRegion.StrokeIds, region.StrokeIds));
                if (match is not null)
                {
                    handles[expectedRegion.RegionKey] = match.RuntimeRegionHandle;
                }
            }
            RegionHandles = handles;
        }

        public bool BindStampResult(StampStepV1 expected, StampActualV1 actual)
        {
            string[] expectedRemoved = Translate(expected.ExpectedRemovedStrokeIds);
            bool exact = expected.ExpectedDecision == actual.Decision
                && NullableDoubleEquals(expected.ExpectedScale, actual.AppliedScale)
                && StrictSetEquals(expectedRemoved, actual.RemovedStrokeIds)
                && expected.ExpectedAddedStrokes.Count == actual.AddedStrokes.Count
                && StampGeometryEquals(expected, actual);

            HashSet<string> liveActual = _live.Select(Resolve).ToHashSet(StringComparer.Ordinal);
            string[] actualAddedIds = actual.AddedStrokes.Select(stroke => stroke.StrokeId).ToArray();
            if (actualAddedIds.Any(id => !IsBoundedId(id))
                || actualAddedIds.Distinct(StringComparer.Ordinal).Count() != actualAddedIds.Length
                || actualAddedIds.Any(liveActual.Contains))
            {
                return false;
            }
            for (int index = 0; index < expected.ExpectedAddedStrokes.Count; index++)
            {
                string alias = expected.ExpectedAddedStrokes[index].StrokeId;
                if (_aliasToActual.ContainsKey(alias))
                {
                    return false;
                }
                _aliasToActual[alias] = actualAddedIds[index];
            }
            return exact;
        }

        private static bool StampGeometryEquals(StampStepV1 expected, StampActualV1 actual)
        {
            if (expected.ExpectedDecision == CorpusStampDecisionV1.Refuse)
            {
                return actual.AppliedScale is null
                    && actual.RemovedStrokeIds.Count == 0
                    && actual.AddedStrokes.Count == 0;
            }
            if (actual.AppliedScale is not { } scale
                || !double.IsFinite(scale)
                || scale <= 0
                || actual.SourceStrokes.Count != actual.AddedStrokes.Count
                || actual.SourceStrokes.Count == 0)
            {
                return false;
            }

            CorpusSampleV1[] sourceSamples = actual.SourceStrokes
                .SelectMany(stroke => stroke.Samples)
                .ToArray();
            if (sourceSamples.Length == 0
                || sourceSamples.Any(sample => !double.IsFinite(sample.X) || !double.IsFinite(sample.Y)))
            {
                return false;
            }
            double centreX = (sourceSamples.Min(sample => sample.X) + sourceSamples.Max(sample => sample.X)) / 2;
            double centreY = (sourceSamples.Min(sample => sample.Y) + sourceSamples.Max(sample => sample.Y)) / 2;
            for (int strokeIndex = 0; strokeIndex < actual.AddedStrokes.Count; strokeIndex++)
            {
                CorpusStrokeV1 source = actual.SourceStrokes[strokeIndex];
                CorpusStrokeV1 added = actual.AddedStrokes[strokeIndex];
                CorpusStrokeV1 recorded = expected.ExpectedAddedStrokes[strokeIndex];
                if (source.Samples.Count != added.Samples.Count
                    || recorded.Samples.Count != added.Samples.Count
                    || source.StartOffsetTicks != added.StartOffsetTicks
                    || recorded.StartOffsetTicks != added.StartOffsetTicks)
                {
                    return false;
                }
                for (int sampleIndex = 0; sampleIndex < added.Samples.Count; sampleIndex++)
                {
                    CorpusSampleV1 before = source.Samples[sampleIndex];
                    CorpusSampleV1 after = added.Samples[sampleIndex];
                    CorpusSampleV1 expectedSample = recorded.Samples[sampleIndex];
                    double transformedX = centreX + (before.X - centreX) * scale + expected.GestureDelta.X;
                    double transformedY = centreY + (before.Y - centreY) * scale + expected.GestureDelta.Y;
                    if (!NearlyEqual(transformedX, after.X)
                        || !NearlyEqual(transformedY, after.Y)
                        || !NearlyEqual(expectedSample.X, after.X)
                        || !NearlyEqual(expectedSample.Y, after.Y)
                        || before.ElapsedTicks != after.ElapsedTicks
                        || expectedSample.ElapsedTicks != after.ElapsedTicks
                        || before.Pressure != after.Pressure
                        || expectedSample.Pressure != after.Pressure)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool NullableDoubleEquals(double? expected, double? actual) =>
            expected is null || actual is null
                ? expected is null && actual is null
                : NearlyEqual(expected.Value, actual.Value);

        private static bool NearlyEqual(double left, double right) =>
            double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= 1e-6;

        public void Advance(CorpusStepV1 step)
        {
            switch (step)
            {
                case AddInkStepV1 add:
                    PushMutation();
                    _live.UnionWith(add.StrokeIds);
                    ClearRegions();
                    break;
                case EraseStepV1 erase:
                    PushMutation();
                    _live.ExceptWith(erase.StrokeIds);
                    ClearRegions();
                    break;
                case RewriteStepV1 rewrite:
                    PushMutation();
                    _live.ExceptWith(rewrite.RemovedStrokeIds);
                    _live.UnionWith(rewrite.AddedStrokeIds);
                    ClearRegions();
                    break;
                case UndoStepV1:
                    _redo.Push(Copy(_live));
                    _live = _undo.Pop();
                    ClearRegions();
                    break;
                case RedoStepV1:
                    _undo.Push(Copy(_live));
                    _live = _redo.Pop();
                    ClearRegions();
                    break;
                case RecognizeStepV1 recognize:
                    SetRegions(recognize.Expected);
                    break;
                case StampStepV1 stamp:
                    if (stamp.ExpectedDecision != CorpusStampDecisionV1.Refuse)
                    {
                        PushMutation();
                        _live.ExceptWith(stamp.ExpectedRemovedStrokeIds);
                        string[] addedAliases = stamp.ExpectedAddedStrokes
                            .Select(stroke => stroke.StrokeId)
                            .ToArray();
                        _synthesizedIds.UnionWith(addedAliases);
                        _live.UnionWith(addedAliases);
                        ClearRegions();
                    }
                    break;
                case SaveStepV1 save:
                    WriteStore(save.StoreSlot);
                    break;
                case CloseFlushStepV1 close:
                    WriteStore(close.StoreSlot);
                    break;
                case ReopenStepV1 reopen:
                    HashSet<string>? reopened = ReadStore(_stores[reopen.StoreSlot]).Snapshot;
                    if (reopened is not null)
                    {
                        _live = Copy(reopened);
                        ResetHistory();
                        if (reopen.Expected is not null) SetRegions(reopen.Expected); else ClearRegions();
                    }
                    break;
                case RecoverStepV1 recover:
                    StoreState recoverStore = _stores[recover.StoreSlot];
                    if (recover.Damage != CorpusRecoveryDamageV1.StaleTemporaryCandidate)
                    {
                        recoverStore = recoverStore with
                        {
                            CurrentCondition = recover.Damage == CorpusRecoveryDamageV1.CorruptCurrent
                                ? StoreCurrentCondition.Corrupt
                                : StoreCurrentCondition.Missing,
                        };
                        _stores[recover.StoreSlot] = recoverStore;
                    }
                    _live = Copy(ReadStore(recoverStore).Snapshot!);
                    ResetHistory();
                    SetRegions(recover.Expected);
                    break;
            }
        }

        public bool DocumentStateEquals(ActualDocumentStateV1 actual)
        {
            HashSet<string> expectedLive = _live.Select(Resolve).ToHashSet(StringComparer.Ordinal);
            HashSet<string> expectedUser = _live.Where(_rawIds.Contains).Select(Resolve).ToHashSet(StringComparer.Ordinal);
            HashSet<string> expectedSynthesized = _live.Where(_synthesizedIds.Contains)
                .Select(Resolve)
                .ToHashSet(StringComparer.Ordinal);
            return StrictSetEquals(expectedLive, actual.LiveStrokeIds)
                && StrictSetEquals(expectedUser, actual.UserInkStrokeIds)
                && StrictSetEquals(expectedSynthesized, actual.SynthesizedStrokeIds)
                && !actual.UserInkStrokeIds.Intersect(actual.SynthesizedStrokeIds, StringComparer.Ordinal).Any();
        }

        private void PushMutation()
        {
            _undo.Push(Copy(_live));
            _redo.Clear();
        }

        private void WriteStore(string slot)
        {
            _stores.TryGetValue(slot, out StoreState? existing);
            HashSet<string>? rotatedBackup = existing is
                { CurrentCondition: StoreCurrentCondition.Valid, Current: not null }
                ? existing.Current
                : existing?.Backup;
            _stores[slot] = new StoreState(
                Copy(_live),
                StoreCurrentCondition.Valid,
                rotatedBackup is null ? null : Copy(rotatedBackup));
        }

        private static (CorpusOpenStatusV1 Status, HashSet<string>? Snapshot) ReadStore(StoreState store) =>
            store.CurrentCondition switch
            {
                StoreCurrentCondition.Valid when store.Current is not null =>
                    (CorpusOpenStatusV1.OpenedCurrent, store.Current),
                StoreCurrentCondition.Corrupt when store.Backup is not null =>
                    (CorpusOpenStatusV1.BackupRecoveryCandidate, store.Backup),
                StoreCurrentCondition.Corrupt => (CorpusOpenStatusV1.Invalid, null),
                StoreCurrentCondition.Missing when store.Backup is not null =>
                    (CorpusOpenStatusV1.BackupRecoveryCandidate, store.Backup),
                _ => (CorpusOpenStatusV1.NotFound, null),
            };

        private void ResetHistory()
        {
            _undo.Clear();
            _redo.Clear();
        }

        private void SetRegions(ExpectedPageV1 page)
        {
            RegionStrokes = page.Regions
                .Where(region => region.Expectation is AcceptedRegionExpectationV1)
                .ToDictionary(
                    region => region.RegionKey,
                    region => (IReadOnlyList<string>)Translate(region.StrokeIds),
                    StringComparer.Ordinal);
            RegionHandles = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private void ClearRegions()
        {
            RegionStrokes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            RegionHandles = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private string Resolve(string alias) => _aliasToActual.TryGetValue(alias, out string? actual)
            ? actual
            : alias;

        private string[] Translate(IEnumerable<string> aliases) => aliases.Select(Resolve).ToArray();

        private static HashSet<string> Copy(IEnumerable<string> source) =>
            new(source, StringComparer.Ordinal);

        private static bool StrictSetEquals(IEnumerable<string> expected, IReadOnlyList<string> actual)
        {
            string[] actualItems = actual.ToArray();
            return actualItems.Distinct(StringComparer.Ordinal).Count() == actualItems.Length
                && expected.ToHashSet(StringComparer.Ordinal).SetEquals(actualItems);
        }

        private enum StoreCurrentCondition
        {
            Valid,
            Corrupt,
            Missing,
        }

        private sealed record StoreState(
            HashSet<string>? Current,
            StoreCurrentCondition CurrentCondition,
            HashSet<string>? Backup);
    }
}
