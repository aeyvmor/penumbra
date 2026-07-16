using System.Globalization;
using Penumbra.ExpressionCorpus;

return await CorpusCli.RunAsync(args).ConfigureAwait(false);

public static class CorpusCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        try
        {
            return await RunCoreAsync(args, output).ConfigureAwait(false);
        }
        catch (CorpusLoadException exception)
        {
            output.WriteLine($"error={ToKey(exception.Code)}");
            output.WriteLine("overall=FAIL");
            return 2;
        }
        catch (Exception)
        {
            WriteError(output, "internal_failure");
            return 2;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] is not ("validate" or "run"))
        {
            WriteError(output, "invalid_arguments");
            return 2;
        }

        if (!TryParseOptions(args.AsSpan(1), out string root, out CorpusPartitionV1 partition))
        {
            WriteError(output, "invalid_arguments");
            return 2;
        }

        ExpressionCorpusSuite suite = ExpressionCorpusLoader.Load(root);

        IReadOnlyList<CorpusValidationError> validation = CorpusValidator.ValidateSuite(suite);
        WriteValidationSummary(output, suite, partition, validation);
        if (validation.Count > 0)
        {
            return 2;
        }

        if (args[0] == "run")
        {
            using var factory = new ProductExpressionScenarioRuntimeFactory();
            CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
                suite,
                factory,
                new CorpusRunOptions(partition),
                CancellationToken.None).ConfigureAwait(false);
            output.WriteLine(CorpusReportJson.Serialize(report));
            bool blocked = report.UnavailableCapabilities.Values.Any(count => count > 0);
            output.WriteLine($"overall={(blocked ? "BLOCKED" : report.ProfilePassed ? "PASS" : "FAIL")}");
            return blocked ? 3 : report.ProfilePassed ? 0 : 1;
        }

        output.WriteLine("overall=PASS");
        return 0;
    }

    private static bool TryParseOptions(
        ReadOnlySpan<string> args,
        out string root,
        out CorpusPartitionV1 partition)
    {
        root = Path.Combine("corpus", "expressions");
        partition = CorpusPartitionV1.Development;
        bool rootSeen = false;
        bool partitionSeen = false;
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }
            string name = args[index];
            string value = args[index + 1];
            switch (name)
            {
                case "--root" when !rootSeen && !string.IsNullOrWhiteSpace(value):
                    root = value;
                    rootSeen = true;
                    break;
                case "--partition" when !partitionSeen && TryParsePartition(value, out partition):
                    partitionSeen = true;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool TryParsePartition(string value, out CorpusPartitionV1 partition)
    {
        if (string.Equals(value, "development", StringComparison.Ordinal))
        {
            partition = CorpusPartitionV1.Development;
            return true;
        }
        if (string.Equals(value, "held-out", StringComparison.Ordinal))
        {
            partition = CorpusPartitionV1.HeldOut;
            return true;
        }
        partition = default;
        return false;
    }

    private static void WriteValidationSummary(
        TextWriter output,
        ExpressionCorpusSuite suite,
        CorpusPartitionV1 partition,
        IReadOnlyList<CorpusValidationError> validation)
    {
        output.WriteLine($"schema_version={suite.Manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"corpus_fingerprint={suite.ManifestSha256}");
        output.WriteLine($"partition={ToKey(partition)}");
        output.WriteLine($"case_count={suite.Cases.Count(item => item.Partition == partition).ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"validation_error_count={validation.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (IGrouping<CorpusErrorCode, CorpusValidationError> group in validation
                     .GroupBy(error => error.Code)
                     .OrderBy(group => group.Key))
        {
            output.WriteLine($"validation_error_{ToKey(group.Key)}={group.Count().ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void WriteError(TextWriter output, string code)
    {
        output.WriteLine($"error={code}");
        output.WriteLine("overall=FAIL");
    }

    private static string ToKey<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));
}
