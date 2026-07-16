using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Penumbra.ExpressionCorpus;

public static partial class CorpusValidator
{
    private const int MaxLogicalIdLength = 64;

    public static IReadOnlyList<CorpusValidationError> ValidateCase(ExpressionCaseV1 @case)
    {
        ArgumentNullException.ThrowIfNull(@case);
        var errors = new List<CorpusValidationError>();
        if (!TryCountExpectedObservationCells(
                @case,
                CorpusResourceLimitsV1.MaximumExpectedObservationCellsPerCase,
                out _))
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, "case.expectedObservations");
            return errors;
        }
        ValidateCase(@case, errors, "case");
        return errors;
    }

    public static IReadOnlyList<CorpusValidationError> ValidateSuite(ExpressionCorpusSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        var errors = new List<CorpusValidationError>();

        CorpusManifestV1? manifest = suite.Manifest;
        if (manifest is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, "manifest");
            return errors;
        }
        if (!string.Equals(manifest.Format, CorpusFormatV1.ManifestFormat, StringComparison.Ordinal))
        {
            Add(errors, CorpusErrorCode.InvalidFormat, "manifest.format");
        }

        if (manifest.SchemaVersion != CorpusFormatV1.SchemaVersion)
        {
            Add(errors, CorpusErrorCode.UnsupportedSchemaVersion, "manifest.schemaVersion");
        }

        if (!IsVersion(manifest.CorpusVersion))
        {
            Add(errors, CorpusErrorCode.InvalidCorpusVersion, "manifest.corpusVersion");
        }
        if (!Sha256Regex().IsMatch(suite.ManifestSha256 ?? string.Empty))
        {
            Add(errors, CorpusErrorCode.InvalidContentHash, "manifestSha256");
        }

        if (suite.Cases is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, "cases");
        }
        IReadOnlyList<ExpressionCaseV1> rawCases = suite.Cases ?? [];
        if (rawCases.Count > CorpusResourceLimitsV1.MaximumCases)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, "cases");
        }
        if (rawCases.Take(CorpusResourceLimitsV1.MaximumCases).Any(item => item is null))
        {
            Add(errors, CorpusErrorCode.MissingValue, "cases");
        }
        ExpressionCaseV1[] cases = rawCases
            .Take(CorpusResourceLimitsV1.MaximumCases)
            .OfType<ExpressionCaseV1>()
            .ToArray();
        if (manifest.Entries is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, "manifest.entries");
        }
        IReadOnlyList<CorpusManifestEntryV1> rawEntries = manifest.Entries ?? [];
        if (rawEntries.Count > CorpusResourceLimitsV1.MaximumCases)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, "manifest.entries");
        }
        if (rawEntries.Take(CorpusResourceLimitsV1.MaximumCases).Any(entry => entry is null))
        {
            Add(errors, CorpusErrorCode.MissingValue, "manifest.entries");
        }
        CorpusManifestEntryV1[] entries = rawEntries
            .Take(CorpusResourceLimitsV1.MaximumCases)
            .OfType<CorpusManifestEntryV1>()
            .ToArray();
        long suiteSamples = 0;
        long suiteStrokes = 0;
        long suiteSteps = 0;
        long suiteExpectedObservationCells = 0;
        bool suiteWorkWithinLimits = true;
        for (int i = 0; i < cases.Length; i++)
        {
            if (ErrorLimitReached(errors))
            {
                break;
            }
            if (!TryConsumeSuiteWork(
                    cases[i],
                    ref suiteSamples,
                    ref suiteStrokes,
                    ref suiteSteps,
                    ref suiteExpectedObservationCells))
            {
                Add(errors, CorpusErrorCode.ResourceLimitExceeded, "cases");
                suiteWorkWithinLimits = false;
                break;
            }
        }

        if (suiteWorkWithinLimits)
        {
            for (int i = 0; i < cases.Length; i++)
            {
                if (ErrorLimitReached(errors))
                {
                    break;
                }
                ValidateCase(cases[i], errors, $"cases[{i}]");
                if (cases[i].Capture is
                    {
                        Source: CorpusCaptureSourceV1.UserRealPen,
                        Consent.PrivateRemoteStorageAllowed: false,
                    })
                {
                    Add(
                        errors,
                        CorpusErrorCode.InvalidConsent,
                        $"cases[{i}].capture.consent.privateRemoteStorageAllowed");
                }
            }
        }

        AddDuplicateErrors(entries.Select(entry => entry.CaseId), errors, "manifest.entries", CorpusErrorCode.DuplicateId);
        AddDuplicateErrors(cases.Select(item => item.CaseId), errors, "cases", CorpusErrorCode.DuplicateId);

        var entriesById = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.CaseId))
            .GroupBy(entry => entry.CaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var casesById = cases
            .Where(item => !string.IsNullOrWhiteSpace(item.CaseId))
            .GroupBy(item => item.CaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (entries.Length != cases.Length
            || entriesById.Keys.Except(casesById.Keys, StringComparer.Ordinal).Any()
            || casesById.Keys.Except(entriesById.Keys, StringComparer.Ordinal).Any())
        {
            Add(errors, CorpusErrorCode.ManifestMismatch, "manifest.entries");
        }

        for (int i = 0; i < entries.Length; i++)
        {
            if (ErrorLimitReached(errors))
            {
                break;
            }
            CorpusManifestEntryV1 entry = entries[i];
            string location = $"manifest.entries[{i}]";
            ValidateDefined(entry.Partition, errors, $"{location}.partition");
            ValidateDefined(entry.Status, errors, $"{location}.status");
            if (entry.ContaminationReason is { } contaminationReason)
            {
                ValidateDefined(contaminationReason, errors, $"{location}.contaminationReason");
            }
            ValidateLogicalId(entry.CaseId, errors, $"{location}.caseId");
            ValidateLogicalId(entry.SessionId, errors, $"{location}.sessionId");
            ValidateManifestPath(entry, errors, $"{location}.relativePath");
            if (!Sha256Regex().IsMatch(entry.Sha256 ?? string.Empty))
            {
                Add(errors, CorpusErrorCode.InvalidContentHash, $"{location}.sha256");
            }

            ExpressionCaseV1? item = null;
            if (entry.CaseId is not null)
            {
                casesById.TryGetValue(entry.CaseId, out item);
            }
            if (item is not null
                && (item.Capture is null
                    || item.Partition != entry.Partition
                    || !string.Equals(item.Capture.SessionId, entry.SessionId, StringComparison.Ordinal)
                    || !string.Equals(item.CorpusVersion, manifest.CorpusVersion, StringComparison.Ordinal)))
            {
                Add(errors, CorpusErrorCode.ManifestMismatch, location);
            }
            else if (item is not null && errors.Count == 0)
            {
                try
                {
                    if (!CorpusJson.TryComputeCanonicalSha256(
                            item,
                            CorpusResourceLimitsV1.MaximumCaseBytes,
                            out string canonicalHash))
                    {
                        Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.sha256");
                    }
                    else if (!string.Equals(canonicalHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(errors, CorpusErrorCode.InvalidContentHash, $"{location}.sha256");
                    }
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException
                    or NotSupportedException)
                {
                    Add(errors, CorpusErrorCode.InvalidContentHash, $"{location}.sha256");
                }
            }

            ValidateManifestState(entry, entriesById, errors, location);
        }

        if (errors.Count == 0)
        {
            if (!CorpusJson.TryComputeCanonicalSha256(
                    manifest,
                    CorpusResourceLimitsV1.MaximumManifestBytes,
                    out string canonicalManifestHash))
            {
                Add(errors, CorpusErrorCode.ResourceLimitExceeded, "manifestSha256");
            }
            else if (!string.Equals(
                    canonicalManifestHash,
                    suite.ManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(errors, CorpusErrorCode.InvalidContentHash, "manifestSha256");
            }
        }

        if (errors.Count == 0)
        {
        foreach (IGrouping<string, ExpressionCaseV1> sessions in cases
                     .Where(item => item.Capture is not null)
                     .GroupBy(
                     item => item.Capture.SessionId,
                     StringComparer.Ordinal))
        {
            if (sessions.Select(item => item.Partition).Distinct().Count() > 1)
            {
                Add(errors, CorpusErrorCode.CrossPartitionSession, "cases.capture.sessionId");
            }
        }

        foreach (IGrouping<string, ExpressionCaseV1> duplicate in cases.GroupBy(NormalizedInkHash, StringComparer.Ordinal))
        {
            if (duplicate.Select(item => item.Partition).Distinct().Count() > 1)
            {
                Add(errors, CorpusErrorCode.CrossPartitionDuplicateInk, "cases.strokes");
            }
        }

        foreach (IGrouping<string, CorpusManifestEntryV1> duplicate in entries.GroupBy(
                     entry => entry.Sha256,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (duplicate.Select(entry => entry.Partition).Distinct().Count() > 1)
            {
                Add(errors, CorpusErrorCode.CrossPartitionDuplicateInk, "manifest.entries.sha256");
            }
        }

        HashSet<string> activeHeldOutCaseIds = entries
            .Where(entry => entry.Partition == CorpusPartitionV1.HeldOut
                && entry.Status == CorpusCaseStatusV1.Frozen)
            .Select(entry => entry.CaseId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (IGrouping<string, ExpressionCaseV1> duplicate in cases
                     .Where(item => activeHeldOutCaseIds.Contains(item.CaseId))
                     .GroupBy(NormalizedInkHash, StringComparer.Ordinal))
        {
            if (duplicate.Count() > 1)
            {
                Add(errors, CorpusErrorCode.DuplicateHeldOutInk, "cases.strokes");
            }
        }

        foreach (CorpusManifestEntryV1 contaminated in entries.Where(
                     entry => entry.Status == CorpusCaseStatusV1.Contaminated))
        {
            if (contaminated.CaseId is not null
                && contaminated.ReplacementCaseId is not null
                && casesById.TryGetValue(contaminated.CaseId, out ExpressionCaseV1? original)
                && casesById.TryGetValue(contaminated.ReplacementCaseId, out ExpressionCaseV1? replacement)
                && string.Equals(NormalizedInkHash(original), NormalizedInkHash(replacement), StringComparison.Ordinal))
            {
                Add(errors, CorpusErrorCode.MissingHeldOutReplacement, "manifest.entries.replacementCaseId");
            }
        }

        foreach (IGrouping<string, CorpusManifestEntryV1> reusedReplacement in entries
                     .Where(entry => entry.Status == CorpusCaseStatusV1.Contaminated
                         && entry.ReplacementCaseId is not null)
                     .GroupBy(entry => entry.ReplacementCaseId!, StringComparer.Ordinal))
        {
            if (reusedReplacement.Count() > 1)
            {
                Add(errors, CorpusErrorCode.MissingHeldOutReplacement, "manifest.entries.replacementCaseId");
            }
        }
        }

        return errors;
    }

    private static void ValidateCase(
        ExpressionCaseV1 @case,
        List<CorpusValidationError> errors,
        string root)
    {
        if (!string.Equals(@case.Format, CorpusFormatV1.CaseFormat, StringComparison.Ordinal))
        {
            Add(errors, CorpusErrorCode.InvalidFormat, $"{root}.format");
        }

        if (@case.SchemaVersion != CorpusFormatV1.SchemaVersion)
        {
            Add(errors, CorpusErrorCode.UnsupportedSchemaVersion, $"{root}.schemaVersion");
        }

        if (!IsVersion(@case.CorpusVersion))
        {
            Add(errors, CorpusErrorCode.InvalidCorpusVersion, $"{root}.corpusVersion");
        }

        if (@case.CaseRevision <= 0)
        {
            Add(errors, CorpusErrorCode.InvalidCaseRevision, $"{root}.caseRevision");
        }

        ValidateLogicalId(@case.CaseId, errors, $"{root}.caseId");
        ValidateDefined(@case.Partition, errors, $"{root}.partition");
        ValidateCapture(@case.Capture, errors, $"{root}.capture");

        if (@case.Strokes is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{root}.strokes");
        }
        IReadOnlyList<CorpusStrokeV1> rawStrokes = @case.Strokes ?? [];
        if (rawStrokes.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{root}.strokes");
        }
        if (rawStrokes.Take(CorpusResourceLimitsV1.MaximumStrokesPerCase).Any(stroke => stroke is null))
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{root}.strokes");
        }
        CorpusStrokeV1[] strokes = rawStrokes
            .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .OfType<CorpusStrokeV1>()
            .ToArray();
        AddDuplicateErrors(strokes.Select(stroke => stroke.StrokeId), errors, $"{root}.strokes", CorpusErrorCode.DuplicateId);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        int remainingSampleBudget = CorpusResourceLimitsV1.MaximumSamplesPerCase;
        for (int strokeIndex = 0; strokeIndex < strokes.Length; strokeIndex++)
        {
            CorpusStrokeV1 stroke = strokes[strokeIndex];
            string location = $"{root}.strokes[{strokeIndex}]";
            ValidateLogicalId(stroke.StrokeId, errors, $"{location}.strokeId");
            declared.Add(stroke.StrokeId);
            if (stroke.StartOffsetTicks is < 0)
            {
                Add(errors, CorpusErrorCode.NonMonotonicTime, $"{location}.startOffsetTicks");
            }

            if (stroke.Samples is null)
            {
                Add(errors, CorpusErrorCode.MissingValue, $"{location}.samples");
            }
            IReadOnlyList<CorpusSampleV1> rawSamples = stroke.Samples ?? [];
            if (rawSamples.Count > remainingSampleBudget)
            {
                Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{root}.strokes.samples");
            }
            CorpusSampleV1[] samples = rawSamples.Take(remainingSampleBudget).ToArray();
            remainingSampleBudget -= samples.Length;
            if (samples.Length == 0)
            {
                Add(errors, CorpusErrorCode.EmptyStroke, $"{location}.samples");
            }

            long previousTicks = -1;
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                CorpusSampleV1 sample = samples[sampleIndex];
                string sampleLocation = $"{location}.samples[{sampleIndex}]";
                if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y))
                {
                    Add(errors, CorpusErrorCode.NonFiniteNumber, sampleLocation);
                }

                if (!double.IsFinite(sample.Pressure) || sample.Pressure is < 0 or > 1)
                {
                    Add(errors, CorpusErrorCode.InvalidPressure, $"{sampleLocation}.pressure");
                }

                if (sample.ElapsedTicks < 0 || sample.ElapsedTicks < previousTicks)
                {
                    Add(errors, CorpusErrorCode.NonMonotonicTime, $"{sampleLocation}.elapsedTicks");
                }

                previousTicks = sample.ElapsedTicks;
                if (ErrorLimitReached(errors))
                {
                    return;
                }
            }
        }

        if (@case.Capture?.Source == CorpusCaptureSourceV1.UserRealPen)
        {
            long previousEndTicks = -1;
            for (int strokeIndex = 0; strokeIndex < strokes.Length; strokeIndex++)
            {
                CorpusStrokeV1 stroke = strokes[strokeIndex];
                if (stroke.StartOffsetTicks is not { } startTicks
                    || startTicks < previousEndTicks
                    || stroke.Samples is null
                    || stroke.Samples.Count == 0
                    || stroke.Samples[^1].ElapsedTicks > long.MaxValue - startTicks)
                {
                    Add(errors, CorpusErrorCode.NonMonotonicTime,
                        $"{root}.strokes[{strokeIndex}].startOffsetTicks");
                    continue;
                }
                previousEndTicks = startTicks + stroke.Samples[^1].ElapsedTicks;
            }
        }

        var strokeBounds = new Dictionary<string, CorpusBoundsV1>(StringComparer.Ordinal);
        foreach (CorpusStrokeV1 stroke in strokes)
        {
            if (stroke.StrokeId is not null
                && TrySampleBounds(stroke.Samples, out CorpusBoundsV1 bounds))
            {
                strokeBounds.TryAdd(stroke.StrokeId, bounds);
            }
        }

        if (@case.InitialStrokeIds is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{root}.initialStrokeIds");
        }
        IReadOnlyList<string> rawInitialIds = @case.InitialStrokeIds ?? [];
        if (rawInitialIds.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{root}.initialStrokeIds");
        }
        string[] initialIds = rawInitialIds
            .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .ToArray();
        AddDuplicateErrors(initialIds, errors, $"{root}.initialStrokeIds", CorpusErrorCode.DuplicateId);
        var live = new HashSet<string>(StringComparer.Ordinal);
        var activated = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in initialIds)
        {
            if (!declared.Contains(id))
            {
                Add(errors, CorpusErrorCode.DeadStrokeReference, $"{root}.initialStrokeIds");
            }
            else
            {
                live.Add(id);
                activated.Add(id);
            }
        }

        var generatedIds = new HashSet<string>(StringComparer.Ordinal);
        var undo = new Stack<HashSet<string>>();
        var redo = new Stack<HashSet<string>>();
        long stateSnapshotCells = 0;
        var savedSlots = new Dictionary<string, StoreState>(StringComparer.Ordinal);
        var lastRegions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var lastExpectedRegions = new Dictionary<string, ExpectedRegionV1>(StringComparer.Ordinal);
        if (@case.Steps is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{root}.steps");
        }
        IReadOnlyList<CorpusStepV1> rawSteps = @case.Steps ?? [];
        if (rawSteps.Count > CorpusResourceLimitsV1.MaximumStepsPerCase)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{root}.steps");
        }
        CorpusStepV1[] steps = rawSteps
            .Take(CorpusResourceLimitsV1.MaximumStepsPerCase)
            .ToArray();
        AddDuplicateErrors(steps.Select(step => step?.StepId ?? string.Empty), errors, $"{root}.steps", CorpusErrorCode.DuplicateId);

        for (int stepIndex = 0; stepIndex < steps.Length; stepIndex++)
        {
            if (ErrorLimitReached(errors))
            {
                return;
            }
            CorpusStepV1? step = steps[stepIndex];
            string location = $"{root}.steps[{stepIndex}]";
            if (step is null)
            {
                Add(errors, CorpusErrorCode.MissingValue, location);
                continue;
            }

            ValidateLogicalId(step.StepId, errors, $"{location}.stepId");
            switch (step)
            {
                case AddInkStepV1 add:
                    if (PushMutation(
                            live, undo, redo, ref stateSnapshotCells, errors, location))
                    {
                        ActivateRaw(add.StrokeIds, declared, live, activated, errors, location);
                        ClearRegions(lastRegions, lastExpectedRegions);
                    }
                    break;
                case EraseStepV1 erase:
                    if (PushMutation(
                            live, undo, redo, ref stateSnapshotCells, errors, location))
                    {
                        RemoveLive(erase.StrokeIds, live, errors, location);
                        ClearRegions(lastRegions, lastExpectedRegions);
                    }
                    break;
                case RewriteStepV1 rewrite:
                    if (PushMutation(
                            live, undo, redo, ref stateSnapshotCells, errors, location))
                    {
                        RemoveLive(rewrite.RemovedStrokeIds, live, errors, $"{location}.removedStrokeIds");
                        ActivateRaw(
                            rewrite.AddedStrokeIds,
                            declared,
                            live,
                            activated,
                            errors,
                            $"{location}.addedStrokeIds");
                        ClearRegions(lastRegions, lastExpectedRegions);
                    }
                    break;
                case UndoStepV1:
                    if (undo.Count == 0)
                    {
                        Add(errors, CorpusErrorCode.InvalidScenarioOrder, location);
                    }
                    else if (TryPushState(
                                 redo,
                                 live,
                                 ref stateSnapshotCells,
                                 errors,
                                 location))
                    {
                        live = undo.Pop();
                        ClearRegions(lastRegions, lastExpectedRegions);
                    }
                    break;
                case RedoStepV1:
                    if (redo.Count == 0)
                    {
                        Add(errors, CorpusErrorCode.InvalidScenarioOrder, location);
                    }
                    else if (TryPushState(
                                 undo,
                                 live,
                                 ref stateSnapshotCells,
                                 errors,
                                 location))
                    {
                        live = redo.Pop();
                        ClearRegions(lastRegions, lastExpectedRegions);
                    }
                    break;
                case RecognizeStepV1 recognize:
                    ValidateExpectedPage(recognize.Expected, live, strokeBounds, errors, $"{location}.expected");
                    if (recognize.Expected is not null)
                    {
                        lastRegions = BuildRegionMap(recognize.Expected);
                        lastExpectedRegions = BuildExpectedRegionMap(recognize.Expected);
                    }
                    break;
                case StampStepV1 stamp:
                    ValidateStamp(
                        stamp,
                        declared,
                        live,
                        generatedIds,
                        strokeBounds,
                        lastRegions,
                        undo,
                        redo,
                        ref stateSnapshotCells,
                        errors,
                        location);
                    ClearRegions(lastRegions, lastExpectedRegions);
                    break;
                case TaffyProbeStepV1 taffy:
                    ValidateTaffy(taffy, live, lastRegions, lastExpectedRegions, errors, location);
                    break;
                case SaveStepV1 save:
                    ValidateDefined(save.Mode, errors, $"{location}.mode");
                    if (ValidateLogicalId(save.StoreSlot, errors, $"{location}.storeSlot"))
                    {
                        RotateStore(
                            savedSlots,
                            save.StoreSlot,
                            live,
                            ref stateSnapshotCells,
                            errors,
                            location);
                    }
                    break;
                case CloseFlushStepV1 close:
                    if (ValidateExistingSlot(close.StoreSlot, savedSlots, errors, location))
                    {
                        RotateStore(
                            savedSlots,
                            close.StoreSlot,
                            live,
                            ref stateSnapshotCells,
                            errors,
                            location);
                    }
                    break;
                case ReopenStepV1 reopen:
                    ValidateDefined(reopen.ExpectedStatus, errors, $"{location}.expectedStatus");
                    if (ValidateExistingSlot(reopen.StoreSlot, savedSlots, errors, location))
                    {
                        StoreState store = savedSlots[reopen.StoreSlot];
                        (CorpusOpenStatusV1 status, HashSet<string>? snapshot) = ReadStore(store);
                        if (reopen.ExpectedStatus != status
                            || (snapshot is null) != (reopen.Expected is null))
                        {
                            Add(errors, CorpusErrorCode.InvalidScenarioOrder, $"{location}.expectedStatus");
                        }
                        else if (snapshot is not null && TryCopyState(
                                     snapshot,
                                     ref stateSnapshotCells,
                                     errors,
                                     location,
                                     out HashSet<string> reopenedState))
                        {
                            live = reopenedState;
                        }
                        undo.Clear();
                        redo.Clear();
                    }
                    if (reopen.Expected is not null)
                    {
                        ValidateExpectedPage(
                            reopen.Expected, live, strokeBounds, errors, $"{location}.expected");
                        lastRegions = BuildRegionMap(reopen.Expected);
                        lastExpectedRegions = BuildExpectedRegionMap(reopen.Expected);
                    }
                    else
                    {
                        ClearRegions(lastRegions, lastExpectedRegions);
                    }
                    break;
                case RecoverStepV1 recover:
                    ValidateDefined(recover.Damage, errors, $"{location}.damage");
                    ValidateDefined(recover.ExpectedStatus, errors, $"{location}.expectedStatus");
                    if (ValidateExistingSlot(recover.StoreSlot, savedSlots, errors, location))
                    {
                        StoreState store = savedSlots[recover.StoreSlot];
                        HashSet<string>? expectedSnapshot;
                        CorpusOpenStatusV1 expectedStatus;
                        if (recover.Damage == CorpusRecoveryDamageV1.StaleTemporaryCandidate)
                        {
                            (expectedStatus, expectedSnapshot) = ReadStore(store);
                        }
                        else
                        {
                            store = store with
                            {
                                CurrentCondition = recover.Damage == CorpusRecoveryDamageV1.CorruptCurrent
                                    ? StoreCurrentCondition.Corrupt
                                    : StoreCurrentCondition.Missing,
                            };
                            savedSlots[recover.StoreSlot] = store;
                            expectedSnapshot = store.Backup;
                            expectedStatus = CorpusOpenStatusV1.BackupRecoveryCandidate;
                        }
                        if (expectedSnapshot is null || recover.ExpectedStatus != expectedStatus)
                        {
                            Add(errors, CorpusErrorCode.InvalidScenarioOrder, $"{location}.expectedStatus");
                        }
                        else if (TryCopyState(
                                     expectedSnapshot,
                                     ref stateSnapshotCells,
                                     errors,
                                     location,
                                     out HashSet<string> recoveredState))
                        {
                            live = recoveredState;
                        }
                        undo.Clear();
                        redo.Clear();
                    }
                    ValidateExpectedPage(recover.Expected, live, strokeBounds, errors, $"{location}.expected");
                    if (recover.Expected is not null)
                    {
                        lastRegions = BuildRegionMap(recover.Expected);
                        lastExpectedRegions = BuildExpectedRegionMap(recover.Expected);
                    }
                    break;
                case GraphStepV1 graph:
                    ValidateGraph(graph, lastRegions, errors, location);
                    break;
            }
        }

        foreach (string unused in declared.Except(activated, StringComparer.Ordinal))
        {
            Add(errors, CorpusErrorCode.UnownedStroke, $"{root}.strokes[{unused}]");
        }
    }

    private static void ValidateCapture(
        CaptureMetadataV1? capture,
        List<CorpusValidationError> errors,
        string location)
    {
        if (capture is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
            return;
        }

        ValidateLogicalId(capture.WriterId, errors, $"{location}.writerId");
        ValidateLogicalId(capture.SessionId, errors, $"{location}.sessionId");
        bool sourceDefined = ValidateDefined(
            capture.Source, errors, $"{location}.source", CorpusErrorCode.InvalidCaptureMetadata);
        ValidateDefined(
            capture.DataClassification,
            errors,
            $"{location}.dataClassification",
            CorpusErrorCode.InvalidCaptureMetadata);
        ValidateDefined(
            capture.DeviceClass, errors, $"{location}.deviceClass", CorpusErrorCode.InvalidCaptureMetadata);
        ValidateDefined(
            capture.PressureMode, errors, $"{location}.pressureMode", CorpusErrorCode.InvalidCaptureMetadata);
        ValidateDefined(
            capture.CaptureApi, errors, $"{location}.captureApi", CorpusErrorCode.InvalidCaptureMetadata);
        if (!IsVersion(capture.CaptureBuild))
        {
            Add(errors, CorpusErrorCode.InvalidCaptureMetadata, $"{location}.captureBuild");
        }

        if (!sourceDefined)
        {
            return;
        }

        if (capture.Source == CorpusCaptureSourceV1.Synthetic)
        {
            if (capture.DataClassification != CorpusDataClassificationV1.PublicSynthetic
                || capture.Consent is not null
                || capture.DeviceClass != CorpusDeviceClassV1.Synthetic
                || capture.PressureMode != CorpusPressureModeV1.Normalized
                || capture.CaptureApi != CorpusCaptureApiV1.HandAuthored)
            {
                Add(errors, CorpusErrorCode.InvalidCaptureMetadata, location);
            }
            return;
        }

        CaptureConsentV1? consent = capture.Consent;
        IReadOnlyList<CorpusConsentScopeV1> consentScopes = consent?.Scopes ?? [];
        if (consentScopes.Count > CorpusResourceLimitsV1.MaximumConsentScopes)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.consent.scopes");
        }
        CorpusConsentScopeV1[] boundedConsentScopes = consentScopes
            .Take(CorpusResourceLimitsV1.MaximumConsentScopes)
            .ToArray();
        if (consent is not null)
        {
            ValidateDefined(consent.Basis, errors, $"{location}.consent.basis", CorpusErrorCode.InvalidConsent);
            ValidateDefined(
                consent.RightsBasis,
                errors,
                $"{location}.consent.rightsBasis",
                CorpusErrorCode.InvalidConsent);
            foreach (CorpusConsentScopeV1 scope in boundedConsentScopes)
            {
                ValidateDefined(scope, errors, $"{location}.consent.scopes", CorpusErrorCode.InvalidConsent);
            }
        }
        bool hasRemoteScope = boundedConsentScopes.Contains(CorpusConsentScopeV1.PrivateRemoteBackup);
        bool validConsent = capture.DataClassification == CorpusDataClassificationV1.PrivateOwnedInk
            && capture.DeviceClass is CorpusDeviceClassV1.ActivePen or CorpusDeviceClassV1.PassiveStylus
            && capture.PressureMode is CorpusPressureModeV1.Unavailable or CorpusPressureModeV1.Normalized
            && capture.CaptureApi is CorpusCaptureApiV1.AvaloniaPointer
                or CorpusCaptureApiV1.ImportedPenDocument
            && consent is
            {
                PolicyVersion: 1,
                Basis: CorpusConsentBasisV1.ExplicitUserCaptureCheckpoint,
                RightsBasis: CorpusRightsBasisV1.UserAuthoredContributorOwned,
                PrivateModelTrainingAllowed: false,
                PublicRedistributionAllowed: false,
            }
            && consent.PrivateRemoteStorageAllowed == hasRemoteScope
            && consent.RecordedAtUtc != default
            && consent.RecordedAtUtc.Offset == TimeSpan.Zero
            && consent.Scopes is not null
            && consent.Scopes.Count <= CorpusResourceLimitsV1.MaximumConsentScopes
            && boundedConsentScopes.Length == boundedConsentScopes.Distinct().Count()
            && boundedConsentScopes.Contains(CorpusConsentScopeV1.PrivateLocalRegression)
            && boundedConsentScopes.Contains(CorpusConsentScopeV1.PrivateGitVersioning)
            && boundedConsentScopes.All(scope => scope is CorpusConsentScopeV1.PrivateLocalRegression
                or CorpusConsentScopeV1.PrivateGitVersioning
                or CorpusConsentScopeV1.PrivateRemoteBackup);
        if (!validConsent)
        {
            Add(errors, CorpusErrorCode.InvalidConsent, location);
        }
    }

    private static void ValidateExpectedPage(
        ExpectedPageV1? page,
        IReadOnlySet<string> live,
        IReadOnlyDictionary<string, CorpusBoundsV1> strokeBounds,
        List<CorpusValidationError> errors,
        string location)
    {
        if (page is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
            return;
        }

        if (page.Regions is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.regions");
        }
        IReadOnlyList<ExpectedRegionV1> rawRegions = page.Regions ?? [];
        if (rawRegions.Count > CorpusResourceLimitsV1.MaximumRegionsPerPage)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.regions");
        }
        if (rawRegions.Take(CorpusResourceLimitsV1.MaximumRegionsPerPage).Any(region => region is null))
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.regions");
        }
        ExpectedRegionV1[] regions = rawRegions
            .Take(CorpusResourceLimitsV1.MaximumRegionsPerPage)
            .OfType<ExpectedRegionV1>()
            .ToArray();
        AddDuplicateErrors(regions.Select(region => region.RegionKey), errors, $"{location}.regions", CorpusErrorCode.DuplicateId);
        var ownedStrokes = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < regions.Length; index++)
        {
            if (ErrorLimitReached(errors))
            {
                return;
            }
            ExpectedRegionV1 region = regions[index];
            string regionLocation = $"{location}.regions[{index}]";
            ValidateLogicalId(region.RegionKey, errors, $"{regionLocation}.regionKey");
            bool finiteBounds = FiniteBounds(region.Bounds)
                && double.IsFinite(region.BoundsTolerance)
                && region.BoundsTolerance >= 0;
            if (!finiteBounds)
            {
                Add(errors, CorpusErrorCode.NonFiniteNumber, $"{regionLocation}.bounds");
            }

            if (region.StrokeIds is null)
            {
                Add(errors, CorpusErrorCode.MissingValue, $"{regionLocation}.strokeIds");
            }
            IReadOnlyList<string> rawRegionStrokes = region.StrokeIds ?? [];
            if (rawRegionStrokes.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase)
            {
                Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{regionLocation}.strokeIds");
            }
            string[] regionStrokes = rawRegionStrokes
                .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
                .ToArray();
            if (regionStrokes.Length == 0)
            {
                Add(errors, CorpusErrorCode.UnownedStroke, $"{regionLocation}.strokeIds");
            }
            AddDuplicateErrors(regionStrokes, errors, $"{regionLocation}.strokeIds", CorpusErrorCode.DuplicateStrokeOwnership);
            foreach (string strokeId in regionStrokes)
            {
                if (!live.Contains(strokeId))
                {
                    Add(errors, CorpusErrorCode.DeadStrokeReference, $"{regionLocation}.strokeIds");
                }
                if (!ownedStrokes.Add(strokeId))
                {
                    Add(errors, CorpusErrorCode.DuplicateStrokeOwnership, $"{regionLocation}.strokeIds");
                }
            }

            if (finiteBounds
                && (!NonVacuousBoundsTolerance(region.Bounds, region.BoundsTolerance)
                    || TryUnionBounds(regionStrokes, strokeBounds, out CorpusBoundsV1 inkBounds)
                        && !BoundsEqual(region.Bounds, inkBounds, region.BoundsTolerance)))
            {
                Add(errors, CorpusErrorCode.InvalidExpectedOutcome, $"{regionLocation}.bounds");
            }

            ValidateExpectedOutcome(region.Expectation, regionStrokes, errors, $"{regionLocation}.expectation");
        }

        foreach (string missing in live.Except(ownedStrokes, StringComparer.Ordinal))
        {
            Add(errors, CorpusErrorCode.UnownedStroke, $"{location}.regions[{missing}]");
        }

        ValidateExpectedSheet(page.Sheet, regions
            .Select(region => region.RegionKey)
            .Where(regionKey => regionKey is not null)
            .ToHashSet(StringComparer.Ordinal), errors,
            $"{location}.sheet");
    }

    private static void ValidateExpectedOutcome(
        ExpectedRegionExpectationV1? expectation,
        IReadOnlyList<string> regionStrokes,
        List<CorpusValidationError> errors,
        string location)
    {
        if (expectation is RefusedRegionExpectationV1 refused)
        {
            ValidateDefined(
                refused.FirstStage, errors, $"{location}.firstStage", CorpusErrorCode.InvalidExpectedOutcome);
            ValidateDefined(
                refused.Reason, errors, $"{location}.reason", CorpusErrorCode.InvalidExpectedOutcome);
            if (!CorpusRefusalSemanticsV1.IsValid(refused.FirstStage, refused.Reason))
            {
                Add(errors, CorpusErrorCode.InvalidExpectedOutcome, location);
            }
            return;
        }

        if (expectation is not AcceptedRegionExpectationV1 accepted)
        {
            Add(errors, CorpusErrorCode.InvalidExpectedOutcome, location);
            return;
        }

        if (!IsBoundedText(accepted.Latex))
        {
            Add(errors, CorpusErrorCode.InvalidExpectedOutcome, $"{location}.latex");
        }

        if (accepted.Tokens is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.tokens");
        }
        IReadOnlyList<ExpectedTokenV1> rawTokens = accepted.Tokens ?? [];
        if (rawTokens.Count > CorpusResourceLimitsV1.MaximumTokensPerRegion)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.tokens");
        }
        if (rawTokens.Take(CorpusResourceLimitsV1.MaximumTokensPerRegion).Any(token => token is null))
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.tokens");
        }
        ExpectedTokenV1[] tokens = rawTokens
            .Take(CorpusResourceLimitsV1.MaximumTokensPerRegion)
            .OfType<ExpectedTokenV1>()
            .ToArray();
        if (tokens.Length == 0)
        {
            Add(errors, CorpusErrorCode.InvalidExpectedOutcome, $"{location}.tokens");
        }
        AddDuplicateErrors(tokens.Select(token => token.TokenId), errors, $"{location}.tokens", CorpusErrorCode.DuplicateId);
        var tokenStrokes = new HashSet<string>(StringComparer.Ordinal);
        var regionSet = regionStrokes.ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < tokens.Length; index++)
        {
            if (ErrorLimitReached(errors))
            {
                return;
            }
            ExpectedTokenV1 token = tokens[index];
            string tokenLocation = $"{location}.tokens[{index}]";
            ValidateLogicalId(token.TokenId, errors, $"{tokenLocation}.tokenId");
            if (!IsBoundedText(token.Latex)
                || token.SourceStrokeIds is null
                || token.SourceStrokeIds.Count == 0)
            {
                Add(errors, CorpusErrorCode.InvalidExpectedOutcome, tokenLocation);
            }
            if (token.SourceStrokeIds is null)
            {
                Add(errors, CorpusErrorCode.MissingValue, $"{tokenLocation}.sourceStrokeIds");
            }
            IReadOnlyList<string> rawTokenStrokes = token.SourceStrokeIds ?? [];
            if (rawTokenStrokes.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase)
            {
                Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{tokenLocation}.sourceStrokeIds");
            }
            foreach (string strokeId in rawTokenStrokes.Take(CorpusResourceLimitsV1.MaximumStrokesPerCase))
            {
                if (!regionSet.Contains(strokeId))
                {
                    Add(errors, CorpusErrorCode.DeadStrokeReference, $"{tokenLocation}.sourceStrokeIds");
                }
                if (!tokenStrokes.Add(strokeId))
                {
                    Add(errors, CorpusErrorCode.DuplicateStrokeOwnership, $"{tokenLocation}.sourceStrokeIds");
                }
            }
        }

        foreach (string missing in regionSet.Except(tokenStrokes, StringComparer.Ordinal))
        {
            Add(errors, CorpusErrorCode.UnownedStroke, $"{location}.tokens[{missing}]");
        }

        ValidateLayout(accepted.Layout, tokens
            .Select(token => token.TokenId)
            .Where(tokenId => tokenId is not null)
            .ToHashSet(StringComparer.Ordinal), errors,
            $"{location}.layout");
        ValidateEvaluation(accepted.Cas, errors, $"{location}.cas");
    }

    private static void ValidateLayout(
        ExpectedLayoutNodeV1? root,
        IReadOnlySet<string> tokenIds,
        List<CorpusValidationError> errors,
        string location)
    {
        if (root is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
            return;
        }

        var owned = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<ExpectedLayoutNodeV1>(ReferenceEqualityComparer.Instance);
        int nodeCount = 0;
        WalkLayout(root, tokenIds, owned, active, ref nodeCount, depth: 0, errors, location);
        foreach (string missing in tokenIds.Except(owned, StringComparer.Ordinal))
        {
            Add(errors, CorpusErrorCode.UnownedToken, $"{location}[{missing}]");
        }
    }

    private static void WalkLayout(
        ExpectedLayoutNodeV1 node,
        IReadOnlySet<string> tokenIds,
        HashSet<string> owned,
        HashSet<ExpectedLayoutNodeV1> active,
        ref int nodeCount,
        int depth,
        List<CorpusValidationError> errors,
        string location)
    {
        if (ErrorLimitReached(errors))
        {
            return;
        }
        if (depth >= CorpusJson.MaximumDepth
            || nodeCount >= CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion
            || !active.Add(node))
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
            return;
        }
        nodeCount++;

        if (node.OwnedTokenIds is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.ownedTokenIds");
        }
        IReadOnlyList<string> rawNodeOwned = node.OwnedTokenIds ?? [];
        if (rawNodeOwned.Count > CorpusResourceLimitsV1.MaximumTokensPerRegion)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.ownedTokenIds");
        }
        string[] nodeOwned = rawNodeOwned
            .Take(CorpusResourceLimitsV1.MaximumTokensPerRegion)
            .ToArray();
        ValidateDefined(node.Kind, errors, $"{location}.kind", CorpusErrorCode.InvalidLayoutShape);
        if (node.Children is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.children");
        }
        IReadOnlyList<ExpectedLayoutEdgeV1> rawChildren = node.Children ?? [];
        if (rawChildren.Count > CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.children");
        }
        if (rawChildren.Take(CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion).Any(child => child is null))
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.children");
        }
        ExpectedLayoutEdgeV1[] children = rawChildren
            .Take(CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion)
            .OfType<ExpectedLayoutEdgeV1>()
            .ToArray();
        foreach (string tokenId in nodeOwned)
        {
            if (!tokenIds.Contains(tokenId))
            {
                Add(errors, CorpusErrorCode.DeadStrokeReference, $"{location}.ownedTokenIds");
            }
            if (!owned.Add(tokenId))
            {
                Add(errors, CorpusErrorCode.DuplicateTokenOwnership, $"{location}.ownedTokenIds");
            }
        }

        if (!HasValidShape(node.Kind, nodeOwned.Length, children))
        {
            Add(errors, CorpusErrorCode.InvalidLayoutShape, location);
        }

        for (int index = 0; index < children.Length; index++)
        {
            ValidateDefined(
                children[index].Role,
                errors,
                $"{location}.children[{index}].role",
                CorpusErrorCode.InvalidLayoutShape);
            if (children[index].Node is null)
            {
                Add(errors, CorpusErrorCode.MissingValue, $"{location}.children[{index}]");
                continue;
            }
            WalkLayout(
                children[index].Node,
                tokenIds,
                owned,
                active,
                ref nodeCount,
                depth + 1,
                errors,
                $"{location}.children[{index}].node");
        }
        active.Remove(node);
    }

    private static bool HasValidShape(
        LayoutKindV1 kind,
        int ownedCount,
        IReadOnlyList<ExpectedLayoutEdgeV1> children)
    {
        int Count(LayoutRoleV1 role) => children.Count(edge => edge.Role == role);
        bool Only(params LayoutRoleV1[] roles) => children.All(edge => roles.Contains(edge.Role));
        return kind switch
        {
            LayoutKindV1.Token => ownedCount == 1 && children.Count == 0,
            LayoutKindV1.Sequence => ownedCount == 0 && children.Count > 0 && Only(LayoutRoleV1.Item),
            LayoutKindV1.Script => ownedCount == 0
                && Count(LayoutRoleV1.Base) == 1
                && Count(LayoutRoleV1.Superscript) <= 1
                && Count(LayoutRoleV1.Subscript) <= 1
                && Count(LayoutRoleV1.Superscript) + Count(LayoutRoleV1.Subscript) >= 1
                && Only(LayoutRoleV1.Base, LayoutRoleV1.Superscript, LayoutRoleV1.Subscript),
            LayoutKindV1.Fraction => ownedCount == 1
                && Count(LayoutRoleV1.Numerator) == 1
                && Count(LayoutRoleV1.Denominator) == 1
                && children.Count == 2,
            LayoutKindV1.Radical => ownedCount == 1
                && Count(LayoutRoleV1.Radicand) == 1
                && Count(LayoutRoleV1.RootIndex) <= 1
                && children.Count is 1 or 2,
            LayoutKindV1.DelimitedGroup => ownedCount == 2
                && Count(LayoutRoleV1.Body) == 1
                && children.Count == 1,
            LayoutKindV1.FunctionCall => ownedCount == 0
                && Count(LayoutRoleV1.Function) == 1
                && Count(LayoutRoleV1.Argument) == 1
                && children.Count == 2,
            LayoutKindV1.ImplicitProduct => ownedCount == 0
                && children.Count >= 2
                && Only(LayoutRoleV1.Factor),
            LayoutKindV1.Relation => ownedCount == 1
                && Count(LayoutRoleV1.Left) == 1
                && Count(LayoutRoleV1.Right) <= 1
                && children.Count is 1 or 2
                && Only(LayoutRoleV1.Left, LayoutRoleV1.Right),
            _ => false,
        };
    }

    private static void ValidateExpectedSheet(
        ExpectedSheetV1? sheet,
        IReadOnlySet<string> regionKeys,
        List<CorpusValidationError> errors,
        string location)
    {
        if (sheet is null)
        {
            return;
        }

        if (sheet.Nodes is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.nodes");
        }
        IReadOnlyList<ExpectedSheetNodeV1> rawNodes = sheet.Nodes ?? [];
        if (rawNodes.Count > CorpusResourceLimitsV1.MaximumSheetNodes)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.nodes");
        }
        if (rawNodes.Take(CorpusResourceLimitsV1.MaximumSheetNodes).Any(node => node is null))
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.nodes");
        }
        ExpectedSheetNodeV1[] nodes = rawNodes
            .Take(CorpusResourceLimitsV1.MaximumSheetNodes)
            .OfType<ExpectedSheetNodeV1>()
            .ToArray();
        AddDuplicateErrors(nodes.Select(node => node.RegionKey), errors, $"{location}.nodes", CorpusErrorCode.InvalidSheetExpectation);
        foreach (ExpectedSheetNodeV1 node in nodes)
        {
            if (ErrorLimitReached(errors))
            {
                return;
            }
            ValidateDefined(node.Role, errors, $"{location}.nodes.role", CorpusErrorCode.InvalidSheetExpectation);
            IReadOnlyList<string> freeVariables = node.FreeVariables ?? [];
            if (freeVariables.Count > CorpusResourceLimitsV1.MaximumTokensPerRegion)
            {
                Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.nodes.freeVariables");
            }
            if (!regionKeys.Contains(node.RegionKey)
                || node.FreeVariables is null
                || node.DefinedSymbol is not null && !IsBoundedText(node.DefinedSymbol)
                || (node.Role == CorpusSheetRoleV1.Definition) != (node.DefinedSymbol is not null)
                || freeVariables.Take(CorpusResourceLimitsV1.MaximumTokensPerRegion)
                    .Any(variable => !IsBoundedText(variable))
                || !freeVariables.Take(CorpusResourceLimitsV1.MaximumTokensPerRegion).SequenceEqual(
                    freeVariables.Take(CorpusResourceLimitsV1.MaximumTokensPerRegion)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)))
            {
                Add(errors, CorpusErrorCode.InvalidSheetExpectation, location);
            }
            ValidateEvaluation(node.Result, errors, $"{location}.nodes.result");
        }

        if (sheet.ChangedRegionKeys is null || sheet.CausallyAffectedRegionKeys is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
        }
        IReadOnlyList<string> changed = sheet.ChangedRegionKeys ?? [];
        IReadOnlyList<string> affected = sheet.CausallyAffectedRegionKeys ?? [];
        if (changed.Count > CorpusResourceLimitsV1.MaximumRegionsPerPage
            || affected.Count > CorpusResourceLimitsV1.MaximumRegionsPerPage)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
        }
        if (changed.Count != changed.Distinct(StringComparer.Ordinal).Count()
            || affected.Count != affected.Distinct(StringComparer.Ordinal).Count())
        {
            Add(errors, CorpusErrorCode.InvalidSheetExpectation, location);
        }
        foreach (string regionKey in changed
                     .Take(CorpusResourceLimitsV1.MaximumRegionsPerPage)
                     .Concat(affected.Take(CorpusResourceLimitsV1.MaximumRegionsPerPage)))
        {
            if (!regionKeys.Contains(regionKey))
            {
                Add(errors, CorpusErrorCode.InvalidSheetExpectation, location);
            }
        }
    }

    private static void ValidateEvaluation(
        ExpectedEvaluationV1? evaluation,
        List<CorpusValidationError> errors,
        string location)
    {
        if (evaluation is not null)
        {
            ValidateDefined(evaluation.Kind, errors, $"{location}.kind", CorpusErrorCode.InvalidExpectedOutcome);
            bool computedKind = evaluation.Kind is CorpusEvaluationKindV1.Number
                or CorpusEvaluationKindV1.Symbolic
                or CorpusEvaluationKindV1.Solution
                or CorpusEvaluationKindV1.Boolean;
            if (!IsBoundedText(evaluation.CanonicalValue)
                || evaluation.IsComputed != computedKind)
            {
                Add(errors, CorpusErrorCode.InvalidExpectedOutcome, location);
            }
        }
    }

    private static Dictionary<string, HashSet<string>> BuildRegionMap(ExpectedPageV1 page) =>
        (page.Regions ?? [])
        .Take(CorpusResourceLimitsV1.MaximumRegionsPerPage)
        .OfType<ExpectedRegionV1>()
        .Where(region => region.Expectation is AcceptedRegionExpectationV1
            && region.RegionKey is not null
            && region.StrokeIds is not null)
        .GroupBy(region => region.RegionKey!, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => group.First().StrokeIds!
                .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static Dictionary<string, ExpectedRegionV1> BuildExpectedRegionMap(ExpectedPageV1 page) =>
        (page.Regions ?? [])
        .Take(CorpusResourceLimitsV1.MaximumRegionsPerPage)
        .OfType<ExpectedRegionV1>()
        .Where(region => region.Expectation is AcceptedRegionExpectationV1
            && region.RegionKey is not null)
        .GroupBy(region => region.RegionKey!, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static void ValidateStamp(
        StampStepV1 stamp,
        IReadOnlySet<string> declared,
        HashSet<string> live,
        HashSet<string> generated,
        Dictionary<string, CorpusBoundsV1> strokeBounds,
        IReadOnlyDictionary<string, HashSet<string>> regions,
        Stack<HashSet<string>> undo,
        Stack<HashSet<string>> redo,
        ref long stateSnapshotCells,
        List<CorpusValidationError> errors,
        string location)
    {
        if (stamp.SourceRegionKey is null
            || !regions.TryGetValue(stamp.SourceRegionKey, out HashSet<string>? source)
            || !source.All(live.Contains))
        {
            Add(errors, CorpusErrorCode.InvalidScenarioOrder, $"{location}.sourceRegionKey");
            return;
        }

        ValidateDefined(stamp.ExpectedDecision, errors, $"{location}.expectedDecision");
        if (!double.IsFinite(stamp.GestureDelta.X)
            || !double.IsFinite(stamp.GestureDelta.Y)
            || !double.IsFinite(stamp.DropPoint.X)
            || !double.IsFinite(stamp.DropPoint.Y))
        {
            Add(errors, CorpusErrorCode.NonFiniteNumber, location);
        }
        bool refused = stamp.ExpectedDecision == CorpusStampDecisionV1.Refuse;
        if ((stamp.ExpectedScale is null) != refused
            || stamp.ExpectedScale is { } scale && (!double.IsFinite(scale) || scale <= 0))
        {
            Add(errors, CorpusErrorCode.InvalidExpectedOutcome, $"{location}.expectedScale");
        }

        if (stamp.ExpectedRemovedStrokeIds is null || stamp.ExpectedAddedStrokes is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
        }
        string[] removedIds = (stamp.ExpectedRemovedStrokeIds ?? [])
            .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .ToArray();
        CorpusStrokeV1[] addedStrokes = (stamp.ExpectedAddedStrokes ?? [])
            .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .OfType<CorpusStrokeV1>()
            .ToArray();
        if ((stamp.ExpectedRemovedStrokeIds?.Count ?? 0) > CorpusResourceLimitsV1.MaximumStrokesPerCase
            || (stamp.ExpectedAddedStrokes?.Count ?? 0) > CorpusResourceLimitsV1.MaximumStrokesPerCase)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
        }
        AddDuplicateErrors(removedIds, errors, $"{location}.expectedRemovedStrokeIds", CorpusErrorCode.DuplicateStrokeOwnership);
        AddDuplicateErrors(addedStrokes.Select(stroke => stroke.StrokeId), errors,
            $"{location}.expectedAddedStrokes", CorpusErrorCode.DuplicateId);
        if (removedIds.Any(id => !live.Contains(id)))
        {
            Add(errors, CorpusErrorCode.DeadStrokeReference, $"{location}.expectedRemovedStrokeIds");
        }
        bool decisionShapeValid = stamp.ExpectedDecision switch
        {
            CorpusStampDecisionV1.Append => removedIds.Length == 0 && addedStrokes.Length > 0,
            CorpusStampDecisionV1.Replace => removedIds.Length > 0 && addedStrokes.Length > 0,
            CorpusStampDecisionV1.Refuse => removedIds.Length == 0 && addedStrokes.Length == 0,
            _ => false,
        };
        if (!decisionShapeValid)
        {
            Add(errors, CorpusErrorCode.InvalidExpectedOutcome, location);
        }
        if (refused)
        {
            return;
        }

        if ((long)generated.Count + addedStrokes.Length > CorpusResourceLimitsV1.MaximumStrokesPerCase
            || (long)live.Count - removedIds.Length + addedStrokes.Length
                > CorpusResourceLimitsV1.MaximumStrokesPerCase)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.expectedAddedStrokes");
            return;
        }
        if (!PushMutation(
                live,
                undo,
                redo,
                ref stateSnapshotCells,
                errors,
                location))
        {
            return;
        }
        live.ExceptWith(removedIds);
        int remainingSamples = CorpusResourceLimitsV1.MaximumSamplesPerCase;
        for (int strokeIndex = 0; strokeIndex < addedStrokes.Length; strokeIndex++)
        {
            CorpusStrokeV1 added = addedStrokes[strokeIndex];
            string strokeLocation = $"{location}.expectedAddedStrokes[{strokeIndex}]";
            ValidateLogicalId(added.StrokeId, errors, $"{strokeLocation}.strokeId");
            if (declared.Contains(added.StrokeId) || !generated.Add(added.StrokeId) || live.Contains(added.StrokeId))
            {
                Add(errors, CorpusErrorCode.ReusedStrokeId, $"{strokeLocation}.strokeId");
            }
            IReadOnlyList<CorpusSampleV1> samples = added.Samples ?? [];
            if (added.Samples is null || samples.Count == 0)
            {
                Add(errors, CorpusErrorCode.EmptyStroke, $"{strokeLocation}.samples");
            }
            if (samples.Count > remainingSamples)
            {
                Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.expectedAddedStrokes.samples");
            }
            long previousTicks = -1;
            foreach (CorpusSampleV1 sample in samples.Take(remainingSamples))
            {
                if (!double.IsFinite(sample.X)
                    || !double.IsFinite(sample.Y)
                    || !double.IsFinite(sample.Pressure)
                    || sample.Pressure is < 0 or > 1
                    || sample.ElapsedTicks < previousTicks)
                {
                    Add(errors, CorpusErrorCode.InvalidExpectedOutcome, $"{strokeLocation}.samples");
                    break;
                }
                previousTicks = sample.ElapsedTicks;
            }
            remainingSamples -= Math.Min(remainingSamples, samples.Count);
            if (added.StrokeId is not null)
            {
                live.Add(added.StrokeId);
                if (TrySampleBounds(samples, out CorpusBoundsV1 bounds))
                {
                    strokeBounds[added.StrokeId] = bounds;
                }
            }
        }
    }

    private static void ValidateTaffy(
        TaffyProbeStepV1 taffy,
        IReadOnlySet<string> live,
        IReadOnlyDictionary<string, HashSet<string>> regions,
        IReadOnlyDictionary<string, ExpectedRegionV1> expectedRegions,
        List<CorpusValidationError> errors,
        string location)
    {
        IReadOnlyList<string> sourceStrokeIds = taffy.SourceStrokeIds ?? [];
        IReadOnlyList<LayoutPathSegmentV1> layoutPath = taffy.LayoutPath ?? [];
        if (taffy.SourceStrokeIds is null || taffy.LayoutPath is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
        }
        if (sourceStrokeIds.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase
            || layoutPath.Count >= CorpusJson.MaximumDepth)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
        }
        bool finiteGesture = double.IsFinite(taffy.HitPointWorld.X)
            && double.IsFinite(taffy.HitPointWorld.Y)
            && double.IsFinite(taffy.CumulativeScreenDeltaX)
            && double.IsFinite(taffy.CanvasScale)
            && taffy.CanvasScale >= Penumbra.Runtime.PageTaffyController.MinimumCanvasScale
            && taffy.CanvasScale <= Penumbra.Runtime.PageTaffyController.MaximumCanvasScale;
        if (taffy.RegionKey is null
            || !regions.TryGetValue(taffy.RegionKey, out HashSet<string>? region)
            || taffy.SourceStrokeIds is null
            || !sourceStrokeIds.Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
                .All(id => live.Contains(id) && region.Contains(id))
            || taffy.LayoutPath is null
            || layoutPath.Take(CorpusJson.MaximumDepth)
                .Any(segment => segment is null || segment.Index < 0)
            || !finiteGesture
            || !IsBoundedText(taffy.TrialLatex))
        {
            Add(errors, CorpusErrorCode.InvalidScenarioOrder, location);
        }
        foreach (LayoutPathSegmentV1 segment in layoutPath
                     .Take(CorpusJson.MaximumDepth)
                     .OfType<LayoutPathSegmentV1>())
        {
            ValidateDefined(segment.Role, errors, $"{location}.layoutPath.role");
        }

        if (taffy.RegionKey is not null
            && expectedRegions.TryGetValue(taffy.RegionKey, out ExpectedRegionV1? expectedRegion)
            && expectedRegion.Expectation is AcceptedRegionExpectationV1 accepted
            && accepted.Layout is not null
            && TryResolveLayoutPath(
                accepted.Layout,
                layoutPath.Take(CorpusJson.MaximumDepth).ToArray(),
                out ExpectedLayoutNodeV1? selected)
            && selected is not null)
        {
            HashSet<string> selectedTokenIds = DescendantTokenIds(selected);
            HashSet<string> selectedStrokeIds = (accepted.Tokens ?? [])
                .OfType<ExpectedTokenV1>()
                .Where(token => selectedTokenIds.Contains(token.TokenId))
                .SelectMany(token => token.SourceStrokeIds ?? [])
                .ToHashSet(StringComparer.Ordinal);
            if (!StrictSetEquals(
                    selectedStrokeIds,
                    sourceStrokeIds.Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)))
            {
                Add(errors, CorpusErrorCode.InvalidScenarioOrder, $"{location}.sourceStrokeIds");
            }
        }
        else
        {
            Add(errors, CorpusErrorCode.InvalidScenarioOrder, $"{location}.layoutPath");
        }
        if (taffy.ExpectedSheet is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.expectedSheet");
        }
        else
        {
            ValidateExpectedSheet(
                taffy.ExpectedSheet,
                regions.Keys.ToHashSet(StringComparer.Ordinal),
                errors,
                $"{location}.expectedSheet");
        }
    }

    private static void ValidateGraph(
        GraphStepV1 graph,
        IReadOnlyDictionary<string, HashSet<string>> regions,
        List<CorpusValidationError> errors,
        string location)
    {
        ValidateDefined(graph.ExpectedDecision, errors, $"{location}.expectedDecision");
        IReadOnlyList<ExpectedGraphSampleV1> rawSamples = graph.ExpectedSamples ?? [];
        if (graph.ExpectedSamples is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, $"{location}.expectedSamples");
        }
        if (rawSamples.Count > CorpusResourceLimitsV1.MaximumGraphAnchors)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, $"{location}.expectedSamples");
        }
        bool hasNullSample = rawSamples
            .Take(CorpusResourceLimitsV1.MaximumGraphAnchors)
            .Any(sample => sample is null);
        ExpectedGraphSampleV1[] samples = rawSamples
            .Take(CorpusResourceLimitsV1.MaximumGraphAnchors)
            .OfType<ExpectedGraphSampleV1>()
            .ToArray();
        double domainSpan = graph.DomainMax - graph.DomainMin;
        bool anchorsStrictlyIncreasing = samples
            .Zip(samples.Skip(1))
            .All(pair => pair.First.X < pair.Second.X);
        bool invalid = graph.SourceRegionKey is null
            || !regions.ContainsKey(graph.SourceRegionKey)
            || !double.IsFinite(graph.DomainMin)
            || !double.IsFinite(graph.DomainMax)
            || !double.IsFinite(domainSpan)
            || graph.DomainMin >= graph.DomainMax
            || graph.SampleCount is < 2 or > CorpusResourceLimitsV1.MaximumGraphSamples
            || graph.ExpectedSamples is null
            || hasNullSample
            || !anchorsStrictlyIncreasing
            || samples.Any(sample =>
                !double.IsFinite(sample.X) || !double.IsFinite(sample.Y)
                || !double.IsFinite(sample.Tolerance) || sample.Tolerance < 0
                || sample.Tolerance > Math.Max(
                    1e-6,
                    Math.Max(
                        domainSpan / Math.Max(graph.SampleCount, 1) * 0.25,
                        Math.Abs(sample.Y) * 0.05))
                || sample.X < graph.DomainMin || sample.X > graph.DomainMax)
            || (graph.ExpectedDecision == CorpusGraphDecisionV1.Refuse
                && (graph.ExpectedVariable is not null || samples.Length != 0))
            || (graph.ExpectedDecision == CorpusGraphDecisionV1.Graph
                && (string.IsNullOrWhiteSpace(graph.ExpectedVariable) || samples.Length == 0));
        if (invalid)
        {
            Add(errors, CorpusErrorCode.InvalidScenarioOrder, location);
        }
        if (graph.ExpectedDecision == CorpusGraphDecisionV1.Graph)
        {
            if (!IsBoundedText(graph.ExpectedVariable))
            {
                Add(errors, CorpusErrorCode.InvalidScenarioOrder, $"{location}.expectedVariable");
            }
        }
    }

    private static void ActivateRaw(
        IReadOnlyList<string>? ids,
        IReadOnlySet<string> declared,
        HashSet<string> live,
        HashSet<string> activated,
        List<CorpusValidationError> errors,
        string location)
    {
        if (ids is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
            return;
        }
        if (ids.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
        }
        foreach (string id in ids.Take(CorpusResourceLimitsV1.MaximumStrokesPerCase))
        {
            if (!declared.Contains(id))
            {
                Add(errors, CorpusErrorCode.DeadStrokeReference, location);
            }
            else if (!activated.Add(id) || !live.Add(id))
            {
                Add(errors, CorpusErrorCode.ReusedStrokeId, location);
            }
        }
    }

    private static void RemoveLive(
        IReadOnlyList<string>? ids,
        HashSet<string> live,
        List<CorpusValidationError> errors,
        string location)
    {
        if (ids is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
            return;
        }
        if (ids.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
        }
        foreach (string id in ids.Take(CorpusResourceLimitsV1.MaximumStrokesPerCase))
        {
            if (!live.Remove(id))
            {
                Add(errors, CorpusErrorCode.DeadStrokeReference, location);
            }
        }
    }

    private static bool PushMutation(
        HashSet<string> live,
        Stack<HashSet<string>> undo,
        Stack<HashSet<string>> redo,
        ref long stateSnapshotCells,
        List<CorpusValidationError> errors,
        string location)
    {
        if (!TryPushState(undo, live, ref stateSnapshotCells, errors, location))
        {
            return false;
        }
        redo.Clear();
        return true;
    }

    private static bool TryPushState(
        Stack<HashSet<string>> destination,
        IReadOnlySet<string> source,
        ref long stateSnapshotCells,
        List<CorpusValidationError> errors,
        string location)
    {
        if (!TryCopyState(
                source,
                ref stateSnapshotCells,
                errors,
                location,
                out HashSet<string> snapshot))
        {
            return false;
        }
        destination.Push(snapshot);
        return true;
    }

    private static bool TryCopyState(
        IReadOnlySet<string> source,
        ref long stateSnapshotCells,
        List<CorpusValidationError> errors,
        string location,
        out HashSet<string> copy)
    {
        if (stateSnapshotCells + source.Count > CorpusResourceLimitsV1.MaximumStateSnapshotCells)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
            copy = new HashSet<string>(StringComparer.Ordinal);
            return false;
        }
        stateSnapshotCells += source.Count;
        copy = new HashSet<string>(source, StringComparer.Ordinal);
        return true;
    }

    private static bool ValidateExistingSlot(
        string? slot,
        IReadOnlyDictionary<string, StoreState> savedSlots,
        List<CorpusValidationError> errors,
        string location)
    {
        bool validSlot = ValidateLogicalId(slot, errors, $"{location}.storeSlot");
        if (slot is null
            || !validSlot
            || !savedSlots.ContainsKey(slot))
        {
            Add(errors, CorpusErrorCode.InvalidScenarioOrder, $"{location}.storeSlot");
            return false;
        }
        return true;
    }

    private static bool RotateStore(
        IDictionary<string, StoreState> stores,
        string slot,
        IReadOnlySet<string> live,
        ref long stateSnapshotCells,
        List<CorpusValidationError> errors,
        string location)
    {
        stores.TryGetValue(slot, out StoreState? existing);
        HashSet<string>? rotatedBackup = existing is
            { CurrentCondition: StoreCurrentCondition.Valid, Current: not null }
            ? existing.Current
            : existing?.Backup;
        long requiredCells = live.Count + (rotatedBackup?.Count ?? 0);
        if (stateSnapshotCells + requiredCells > CorpusResourceLimitsV1.MaximumStateSnapshotCells)
        {
            Add(errors, CorpusErrorCode.ResourceLimitExceeded, location);
            return false;
        }
        stateSnapshotCells += requiredCells;
        stores[slot] = new StoreState(
            new HashSet<string>(live, StringComparer.Ordinal),
            StoreCurrentCondition.Valid,
            rotatedBackup is null ? null : new HashSet<string>(rotatedBackup, StringComparer.Ordinal));
        return true;
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

    private static void ClearRegions(
        IDictionary<string, HashSet<string>> regions,
        IDictionary<string, ExpectedRegionV1> expectedRegions)
    {
        regions.Clear();
        expectedRegions.Clear();
    }

    private static bool TryResolveLayoutPath(
        ExpectedLayoutNodeV1 root,
        IReadOnlyList<LayoutPathSegmentV1> path,
        out ExpectedLayoutNodeV1? selected)
    {
        selected = root;
        foreach (LayoutPathSegmentV1 segment in path.Take(CorpusJson.MaximumDepth))
        {
            if (segment is null || selected is null || selected.Children is null)
            {
                selected = null;
                return false;
            }
            ExpectedLayoutEdgeV1[] candidates = selected.Children
                .Take(CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion)
                .OfType<ExpectedLayoutEdgeV1>()
                .Where(edge => edge.Role == segment.Role)
                .ToArray();
            if (segment.Index < 0 || segment.Index >= candidates.Length || candidates[segment.Index].Node is null)
            {
                selected = null;
                return false;
            }
            selected = candidates[segment.Index].Node;
        }
        return selected is not null;
    }

    private static HashSet<string> DescendantTokenIds(ExpectedLayoutNodeV1 node)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<ExpectedLayoutNodeV1>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ExpectedLayoutNodeV1>();
        pending.Push(node);
        while (pending.Count > 0
               && visited.Count < CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion)
        {
            ExpectedLayoutNodeV1 current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            ids.UnionWith((current.OwnedTokenIds ?? [])
                .Take(CorpusResourceLimitsV1.MaximumTokensPerRegion));
            foreach (ExpectedLayoutEdgeV1 edge in (current.Children ?? [])
                         .Take(CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion)
                         .OfType<ExpectedLayoutEdgeV1>())
            {
                if (edge.Node is not null)
                {
                    pending.Push(edge.Node);
                }
            }
        }
        return ids;
    }

    private static void ValidateManifestPath(
        CorpusManifestEntryV1 entry,
        List<CorpusValidationError> errors,
        string location)
    {
        string path = entry.RelativePath ?? string.Empty;
        string[] parts = path.Split('/', StringSplitOptions.None);
        string expectedDirectory = entry.Partition == CorpusPartitionV1.Development ? "development" : "held-out";
        bool valid = parts.Length == 2
            && string.Equals(parts[0], expectedDirectory, StringComparison.Ordinal)
            && CorpusPathRulesV1.IsCaseFileName(parts[1])
            && !path.Contains('\\')
            && !Path.IsPathRooted(path)
            && !parts.Any(part => part is "." or "..")
            && parts.All(part => part.Length > 0);
        if (!valid)
        {
            Add(errors, CorpusErrorCode.InvalidManifestPath, location);
        }
    }

    private static void ValidateManifestState(
        CorpusManifestEntryV1 entry,
        IReadOnlyDictionary<string, CorpusManifestEntryV1> entries,
        List<CorpusValidationError> errors,
        string location)
    {
        if (entry.Partition == CorpusPartitionV1.Development)
        {
            if (entry.Status != CorpusCaseStatusV1.Development
                || entry.ContaminationReason is not null
                || entry.ReplacementCaseId is not null)
            {
                Add(errors, CorpusErrorCode.InvalidHeldOutState, location);
            }
            return;
        }

        if (entry.Status == CorpusCaseStatusV1.Frozen)
        {
            if (entry.ContaminationReason is not null || entry.ReplacementCaseId is not null)
            {
                Add(errors, CorpusErrorCode.InvalidHeldOutState, location);
            }
            return;
        }

        if (entry.Status != CorpusCaseStatusV1.Contaminated
            || entry.ContaminationReason is null
            || string.IsNullOrWhiteSpace(entry.ReplacementCaseId)
            || string.Equals(entry.CaseId, entry.ReplacementCaseId, StringComparison.Ordinal)
            || !entries.TryGetValue(entry.ReplacementCaseId, out CorpusManifestEntryV1? replacement)
            || replacement.Partition != CorpusPartitionV1.HeldOut
            || replacement.Status != CorpusCaseStatusV1.Frozen
            || string.Equals(entry.SessionId, replacement.SessionId, StringComparison.Ordinal)
            || string.Equals(entry.Sha256, replacement.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Add(errors, CorpusErrorCode.MissingHeldOutReplacement, location);
        }
    }

    private static string NormalizedInkHash(ExpressionCaseV1 @case)
    {
        CorpusSampleV1[] all = (@case.Strokes ?? [])
            .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .OfType<CorpusStrokeV1>()
            .SelectMany(stroke => (stroke.Samples ?? [])
                .Take(CorpusResourceLimitsV1.MaximumSamplesPerCase))
            .Take(CorpusResourceLimitsV1.MaximumSamplesPerCase)
            .ToArray();
        if (all.Length == 0 || all.Any(sample => !double.IsFinite(sample.X) || !double.IsFinite(sample.Y)))
        {
            return $"invalid:{@case.CaseId}";
        }

        double minX = all.Min(sample => sample.X);
        double minY = all.Min(sample => sample.Y);
        double width = all.Max(sample => sample.X) - minX;
        double height = all.Max(sample => sample.Y) - minY;
        double scale = Math.Max(Math.Max(width, height), 1e-12);
        var strokeSignatures = new List<string>();
        foreach (CorpusStrokeV1 stroke in (@case.Strokes ?? [])
                     .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
                     .OfType<CorpusStrokeV1>())
        {
            var canonicalStroke = new StringBuilder();
            canonicalStroke.Append('[').Append(stroke.Samples?.Count ?? 0).Append(':');
            foreach (CorpusSampleV1 sample in (stroke.Samples ?? [])
                         .Take(CorpusResourceLimitsV1.MaximumSamplesPerCase))
            {
                canonicalStroke.Append(((sample.X - minX) / scale).ToString("F6", CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(((sample.Y - minY) / scale).ToString("F6", CultureInfo.InvariantCulture))
                    .Append(';');
            }
            canonicalStroke.Append(']');
            strokeSignatures.Add(canonicalStroke.ToString());
        }
        strokeSignatures.Sort(StringComparer.Ordinal);
        string canonical = string.Concat(strokeSignatures);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool ValidateLogicalId(
        string? value,
        List<CorpusValidationError> errors,
        string location)
    {
        if (value is null)
        {
            Add(errors, CorpusErrorCode.MissingValue, location);
            return false;
        }
        bool valid = value.Length <= MaxLogicalIdLength && LogicalIdRegex().IsMatch(value);
        if (!valid)
        {
            Add(errors, CorpusErrorCode.UnsafeLogicalId, location);
        }
        return valid;
    }

    private static void AddDuplicateErrors(
        IEnumerable<string> values,
        List<CorpusValidationError> errors,
        string location,
        CorpusErrorCode code)
    {
        if (values.GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            Add(errors, code, location);
        }
    }

    private static bool FiniteBounds(CorpusBoundsV1 bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
        && bounds.Width >= 0 && bounds.Height >= 0;

    private static bool NonVacuousBoundsTolerance(CorpusBoundsV1 bounds, double tolerance) =>
        tolerance <= Math.Max(0.25, Math.Max(bounds.Width, bounds.Height) * 0.05);

    private static bool BoundsEqual(CorpusBoundsV1 left, CorpusBoundsV1 right, double tolerance) =>
        Math.Abs(left.X - right.X) <= tolerance
        && Math.Abs(left.Y - right.Y) <= tolerance
        && Math.Abs(left.Width - right.Width) <= tolerance
        && Math.Abs(left.Height - right.Height) <= tolerance;

    private static bool TrySampleBounds(
        IReadOnlyList<CorpusSampleV1>? samples,
        out CorpusBoundsV1 bounds)
    {
        bounds = default;
        if (samples is null || samples.Count == 0)
        {
            return false;
        }
        CorpusSampleV1 first = samples[0];
        if (!double.IsFinite(first.X) || !double.IsFinite(first.Y))
        {
            return false;
        }
        double minX = first.X;
        double minY = first.Y;
        double maxX = first.X;
        double maxY = first.Y;
        foreach (CorpusSampleV1 sample in samples
                     .Skip(1)
                     .Take(CorpusResourceLimitsV1.MaximumSamplesPerCase - 1))
        {
            if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y))
            {
                return false;
            }
            minX = Math.Min(minX, sample.X);
            minY = Math.Min(minY, sample.Y);
            maxX = Math.Max(maxX, sample.X);
            maxY = Math.Max(maxY, sample.Y);
        }
        bounds = new CorpusBoundsV1(minX, minY, maxX - minX, maxY - minY);
        return FiniteBounds(bounds);
    }

    private static bool TryUnionBounds(
        IEnumerable<string> strokeIds,
        IReadOnlyDictionary<string, CorpusBoundsV1> strokeBounds,
        out CorpusBoundsV1 bounds)
    {
        bounds = default;
        bool found = false;
        double minX = 0;
        double minY = 0;
        double maxX = 0;
        double maxY = 0;
        foreach (string strokeId in strokeIds.Take(CorpusResourceLimitsV1.MaximumStrokesPerCase))
        {
            if (strokeId is null || !strokeBounds.TryGetValue(strokeId, out CorpusBoundsV1 stroke))
            {
                return false;
            }
            double strokeMaxX = stroke.X + stroke.Width;
            double strokeMaxY = stroke.Y + stroke.Height;
            if (!double.IsFinite(strokeMaxX) || !double.IsFinite(strokeMaxY))
            {
                return false;
            }
            if (!found)
            {
                minX = stroke.X;
                minY = stroke.Y;
                maxX = strokeMaxX;
                maxY = strokeMaxY;
                found = true;
            }
            else
            {
                minX = Math.Min(minX, stroke.X);
                minY = Math.Min(minY, stroke.Y);
                maxX = Math.Max(maxX, strokeMaxX);
                maxY = Math.Max(maxY, strokeMaxY);
            }
        }
        bounds = new CorpusBoundsV1(minX, minY, maxX - minX, maxY - minY);
        return found && FiniteBounds(bounds);
    }

    private static bool StrictSetEquals(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        string[] expectedItems = expected.ToArray();
        string[] actualItems = actual.ToArray();
        return expectedItems.Distinct(StringComparer.Ordinal).Count() == expectedItems.Length
            && actualItems.Distinct(StringComparer.Ordinal).Count() == actualItems.Length
            && expectedItems.Length == actualItems.Length
            && expectedItems.ToHashSet(StringComparer.Ordinal).SetEquals(actualItems);
    }

    private static bool IsVersion(string? value) => value is not null && VersionRegex().IsMatch(value);

    private static bool IsBoundedText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= CorpusResourceLimitsV1.MaximumTextLength;

    private static bool TryCountExpectedObservationCells(
        ExpressionCaseV1 @case,
        long maximum,
        out long cells)
    {
        cells = 0;
        foreach (CorpusStepV1 step in (@case.Steps ?? [])
                     .Take(CorpusResourceLimitsV1.MaximumStepsPerCase)
                     .OfType<CorpusStepV1>())
        {
            bool withinBudget = step switch
            {
                RecognizeStepV1 recognize =>
                    TryConsumeExpectedPage(recognize.Expected, ref cells, maximum),
                ReopenStepV1 reopen =>
                    TryConsumeExpectedPage(reopen.Expected, ref cells, maximum),
                RecoverStepV1 recover =>
                    TryConsumeExpectedPage(recover.Expected, ref cells, maximum),
                TaffyProbeStepV1 taffy =>
                    TryConsumeExpectedSheet(taffy.ExpectedSheet, ref cells, maximum),
                GraphStepV1 graph =>
                    TryConsumeExpectedGraph(graph, ref cells, maximum),
                _ => true,
            };
            if (!withinBudget)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryConsumeExpectedPage(
        ExpectedPageV1? page,
        ref long cells,
        long maximum)
    {
        if (!TryAddBudget(ref cells, 1, maximum))
        {
            return false;
        }
        if (page is null)
        {
            return true;
        }

        IReadOnlyList<ExpectedRegionV1> regions = page.Regions ?? [];
        if (!TryAddBudget(ref cells, regions.Count, maximum))
        {
            return false;
        }
        foreach (ExpectedRegionV1 region in regions
                     .Take(CorpusResourceLimitsV1.MaximumRegionsPerPage)
                     .OfType<ExpectedRegionV1>())
        {
            if (!TryAddBudget(ref cells, region.StrokeIds?.Count ?? 0, maximum)
                || !TryAddBudget(ref cells, 1, maximum))
            {
                return false;
            }
            if (region.Expectation is AcceptedRegionExpectationV1 accepted)
            {
                IReadOnlyList<ExpectedTokenV1> tokens = accepted.Tokens ?? [];
                if (!TryAddBudget(ref cells, tokens.Count, maximum))
                {
                    return false;
                }
                foreach (ExpectedTokenV1 token in tokens
                             .Take(CorpusResourceLimitsV1.MaximumTokensPerRegion)
                             .OfType<ExpectedTokenV1>())
                {
                    if (!TryAddBudget(ref cells, token.SourceStrokeIds?.Count ?? 0, maximum))
                    {
                        return false;
                    }
                }
                if (!TryConsumeExpectedLayout(accepted.Layout, depth: 0, ref cells, maximum)
                    || accepted.Cas is not null && !TryAddBudget(ref cells, 1, maximum))
                {
                    return false;
                }
            }
        }
        return TryConsumeExpectedSheet(page.Sheet, ref cells, maximum);
    }

    private static bool TryConsumeExpectedLayout(
        ExpectedLayoutNodeV1? node,
        int depth,
        ref long cells,
        long maximum)
    {
        if (node is null || depth >= CorpusJson.MaximumDepth)
        {
            return true;
        }
        IReadOnlyList<ExpectedLayoutEdgeV1> children = node.Children ?? [];
        if (!TryAddBudget(ref cells, 1, maximum)
            || !TryAddBudget(ref cells, node.OwnedTokenIds?.Count ?? 0, maximum)
            || !TryAddBudget(ref cells, children.Count, maximum))
        {
            return false;
        }
        foreach (ExpectedLayoutEdgeV1 child in children
                     .Take(CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion)
                     .OfType<ExpectedLayoutEdgeV1>())
        {
            if (!TryConsumeExpectedLayout(child.Node, depth + 1, ref cells, maximum))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryConsumeExpectedSheet(
        ExpectedSheetV1? sheet,
        ref long cells,
        long maximum)
    {
        if (sheet is null)
        {
            return true;
        }
        IReadOnlyList<ExpectedSheetNodeV1> nodes = sheet.Nodes ?? [];
        if (!TryAddBudget(ref cells, 1, maximum)
            || !TryAddBudget(ref cells, nodes.Count, maximum)
            || !TryAddBudget(ref cells, sheet.ChangedRegionKeys?.Count ?? 0, maximum)
            || !TryAddBudget(ref cells, sheet.CausallyAffectedRegionKeys?.Count ?? 0, maximum))
        {
            return false;
        }
        foreach (ExpectedSheetNodeV1 node in nodes
                     .Take(CorpusResourceLimitsV1.MaximumSheetNodes)
                     .OfType<ExpectedSheetNodeV1>())
        {
            if (!TryAddBudget(ref cells, node.FreeVariables?.Count ?? 0, maximum)
                || node.Result is not null && !TryAddBudget(ref cells, 1, maximum))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryConsumeExpectedGraph(
        GraphStepV1 graph,
        ref long cells,
        long maximum) =>
        TryAddBudget(ref cells, 1, maximum)
        && TryAddBudget(ref cells, graph.ExpectedSamples?.Count ?? 0, maximum);

    private static bool TryConsumeSuiteWork(
        ExpressionCaseV1 @case,
        ref long samples,
        ref long strokes,
        ref long steps,
        ref long expectedObservationCells)
    {
        int strokeCount = @case.Strokes?.Count ?? 0;
        int stepCount = @case.Steps?.Count ?? 0;
        if (!TryAddBudget(ref strokes, strokeCount, CorpusResourceLimitsV1.MaximumStrokesPerSuite)
            || !TryAddBudget(ref steps, stepCount, CorpusResourceLimitsV1.MaximumStepsPerSuite))
        {
            return false;
        }
        foreach (CorpusStrokeV1 stroke in (@case.Strokes ?? [])
                     .Take(CorpusResourceLimitsV1.MaximumStrokesPerCase)
                     .OfType<CorpusStrokeV1>())
        {
            if (!TryAddBudget(
                    ref samples,
                    stroke.Samples?.Count ?? 0,
                    CorpusResourceLimitsV1.MaximumSamplesPerSuite))
            {
                return false;
            }
        }
        return TryCountExpectedObservationCells(
                @case,
                CorpusResourceLimitsV1.MaximumExpectedObservationCellsPerCase,
                out long caseExpectedObservationCells)
            && TryAddBudget(
                ref expectedObservationCells,
                caseExpectedObservationCells,
                CorpusResourceLimitsV1.MaximumExpectedObservationCellsPerSuite);
    }

    private static bool TryAddBudget(ref long current, long amount, long maximum)
    {
        if (amount < 0 || current > maximum - amount)
        {
            return false;
        }
        current += amount;
        return true;
    }

    private static bool ErrorLimitReached(IReadOnlyCollection<CorpusValidationError> errors) =>
        errors.Count >= CorpusResourceLimitsV1.MaximumValidationErrors;

    private static bool ValidateDefined<TEnum>(
        TEnum value,
        List<CorpusValidationError> errors,
        string location,
        CorpusErrorCode code = CorpusErrorCode.InvalidFormat)
        where TEnum : struct, Enum
    {
        bool valid = Enum.IsDefined(value);
        if (!valid)
        {
            Add(errors, code, location);
        }
        return valid;
    }

    private static void Add(
        ICollection<CorpusValidationError> errors,
        CorpusErrorCode code,
        string location)
    {
        if (errors.Count < CorpusResourceLimitsV1.MaximumValidationErrors)
        {
            errors.Add(new CorpusValidationError(code, location));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

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
