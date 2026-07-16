namespace Penumbra.ExpressionCorpus;

public enum CorpusErrorCode
{
    InvalidFormat,
    UnsupportedSchemaVersion,
    InvalidCorpusVersion,
    InvalidCaseRevision,
    UnsafeLogicalId,
    DuplicateId,
    MissingValue,
    InvalidCaptureMetadata,
    InvalidConsent,
    EmptyStroke,
    NonFiniteNumber,
    NonMonotonicTime,
    InvalidPressure,
    DeadStrokeReference,
    ReusedStrokeId,
    UnownedStroke,
    DuplicateStrokeOwnership,
    UnownedToken,
    DuplicateTokenOwnership,
    InvalidLayoutShape,
    InvalidExpectedOutcome,
    InvalidSheetExpectation,
    InvalidScenarioOrder,
    ManifestMismatch,
    InvalidManifestPath,
    InvalidContentHash,
    CrossPartitionSession,
    CrossPartitionDuplicateInk,
    DuplicateHeldOutInk,
    InvalidHeldOutState,
    MissingHeldOutReplacement,
    ResourceLimitExceeded,
}

public static class CorpusResourceLimitsV1
{
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    public const int MaximumCaseBytes = 64 * 1024 * 1024;
    public const long MaximumCorpusBytes = 256L * 1024 * 1024;
    public const int MaximumCases = 10_000;
    public const int MaximumStrokesPerCase = 4_096;
    public const int MaximumSamplesPerCase = 1_000_000;
    public const long MaximumSamplesPerSuite = 1_000_000;
    public const long MaximumStrokesPerSuite = 100_000;
    public const long MaximumStepsPerSuite = 100_000;
    public const int MaximumStepsPerCase = 4_096;
    public const int MaximumRegionsPerPage = 4_096;
    public const int MaximumTokensPerRegion = 4_096;
    public const int MaximumLayoutNodesPerRegion = 8_192;
    public const int MaximumSheetNodes = 4_096;
    public const int MaximumGraphSamples = 4_096;
    public const int MaximumGraphAnchors = 256;
    public const int MaximumConsentScopes = 8;
    public const int MaximumTextLength = 4_096;
    public const int MaximumValidationErrors = 10_000;
    public const int MaximumMetricCapacity = 1_000_000;
    public const long MaximumStateSnapshotCells = 1_000_000;
    public const long MaximumExpectedObservationCellsPerCase = 1_000_000;
    public const long MaximumExpectedObservationCellsPerSuite = 1_000_000;
}

internal static class CorpusPathRulesV1
{
    private const string CaseSuffix = ".case.json";

    public static bool IsCaseFileName(string? fileName)
    {
        if (fileName is null
            || !fileName.EndsWith(CaseSuffix, StringComparison.Ordinal)
            || fileName.Length is < 11 or > 74)
        {
            return false;
        }

        ReadOnlySpan<char> logicalId = fileName.AsSpan(0, fileName.Length - CaseSuffix.Length);
        if (!IsLowerAsciiLetterOrDigit(logicalId[0]))
        {
            return false;
        }
        foreach (char character in logicalId[1..])
        {
            if (!IsLowerAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}

public sealed record CorpusValidationError(
    CorpusErrorCode Code,
    string Location);
