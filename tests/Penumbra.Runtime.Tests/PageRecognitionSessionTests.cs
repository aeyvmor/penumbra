using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Recognition;
using Penumbra.Runtime;
using Penumbra.Sheet;

namespace Penumbra.Runtime.Tests;

public sealed class PageRecognitionSessionTests
{
    [Fact]
    public async Task ApplyAndCommit_RejectsUncertainRegionEvictsNodeAndRoundTripsRefusal()
    {
        RegionRecognition accepted = Region("2+2=", y: 0);
        RecognizedToken uncertainToken = accepted.Result.Tokens[0] with { Confidence = 0.1 };
        RegionRecognition refused = accepted with
        {
            Result = accepted.Result with
            {
                Tokens = [uncertainToken],
                Confidence = 0.1,
                MinConfidence = 0.1,
            },
            Dirty = true,
        };
        var recognizer = new QueueRecognizer([accepted], [refused]);
        var session = Create(recognizer);

        PageRecognitionApplication first = session.ApplyAndCommit(
            await session.RecognizeAsync(Strokes(accepted)));
        PageRecognitionApplication second = session.ApplyAndCommit(
            await session.RecognizeAsync(Strokes(accepted)));

        Assert.Single(first.AcceptedRegions);
        Assert.Empty(second.AcceptedRegions);
        Assert.Empty(session.SheetNodes);
        Assert.Contains(uncertainToken.SourceStrokeIds[0], second.UncertainStrokeIds);
        Assert.Equal("2+2=", session.PreviousRegions.Single().Result.Latex);
        Assert.Equal(0.1, session.PreviousRegions.Single().Result.MinConfidence);
        Assert.Empty(recognizer.PreviousArguments[0]);
        Assert.Single(recognizer.PreviousArguments[1]);
    }

    [Fact]
    public async Task Apply_UpsertsCleanMovedDefinitionsSoTopmostOwnerAndDependentChange()
    {
        RegionRecognition firstDefinition = Region("x=1", y: 0);
        RegionRecognition secondDefinition = Region("x=2", y: 100);
        RegionRecognition query = Region("x=", y: 200);
        RegionRecognition movedFirst = Move(firstDefinition, y: 120);
        RegionRecognition movedSecond = Move(secondDefinition, y: 0);
        RegionRecognition cleanQuery = query with { Dirty = false };
        var recognizer = new QueueRecognizer(
            [firstDefinition, secondDefinition, query],
            [movedFirst, movedSecond, cleanQuery]);
        var session = Create(recognizer);

        session.ApplyAndCommit(await session.RecognizeAsync(
            Strokes(firstDefinition, secondDefinition, query)));
        Assert.False(session.SheetNodes.Single(node => node.Id == firstDefinition.Region.Id).IsConflict);
        Assert.True(session.SheetNodes.Single(node => node.Id == secondDefinition.Region.Id).IsConflict);
        Assert.Equal("1", session.SheetNodes.Single(node => node.Id == query.Region.Id).Result?.DisplayText);

        PageRecognitionApplication moved = session.ApplyAndCommit(await session.RecognizeAsync(
            Strokes(firstDefinition, secondDefinition, query)));

        Assert.All(moved.AcceptedRegions, region => Assert.False(region.Dirty));
        Assert.True(session.SheetNodes.Single(node => node.Id == firstDefinition.Region.Id).IsConflict);
        Assert.False(session.SheetNodes.Single(node => node.Id == secondDefinition.Region.Id).IsConflict);
        Assert.Equal("2", session.SheetNodes.Single(node => node.Id == query.Region.Id).Result?.DisplayText);
        Assert.Single(
            moved.RecomputeReport.CausallyAffectedNodes,
            node => node.Id == query.Region.Id);
    }

    [Fact]
    public async Task Commit_IsExplicitAndOlderApplicationCannotAdvanceCacheAfterNewerApply()
    {
        RegionRecognition oldRegion = Region("x=1", y: 0);
        RegionRecognition newRegion = Region(
            "x=2",
            y: 0,
            id: oldRegion.Region.Id,
            strokeId: oldRegion.Region.StrokeIds[0]);
        var recognizer = new QueueRecognizer([oldRegion], [newRegion], [newRegion with { Dirty = false }]);
        var session = Create(recognizer);

        PageRecognitionApplication oldApplication = session.Apply(
            await session.RecognizeAsync(Strokes(oldRegion)));
        Assert.Empty(session.PreviousRegions);

        PageRecognitionCandidate newerCandidate = await session.RecognizeAsync(Strokes(oldRegion));
        Assert.Empty(recognizer.PreviousArguments[1]);
        PageRecognitionApplication newerApplication = session.Apply(newerCandidate);

        Assert.Throws<InvalidOperationException>(() => session.Commit(oldApplication));
        Assert.Empty(session.PreviousRegions);
        session.Commit(newerApplication);
        Assert.Equal("x=2", session.PreviousRegions.Single().Result.Latex);

        await session.RecognizeAsync(Strokes(oldRegion));
        Assert.Equal("x=2", recognizer.PreviousArguments[2].Single().Result.Latex);
    }

    [Fact]
    public async Task Apply_RefusesLateCandidateAfterNewerCandidateAlreadyMutatedSheet()
    {
        RegionRecognition stale = Region("x=1", y: 0);
        RegionRecognition fresh = Region(
            "x=2",
            y: 0,
            id: stale.Region.Id,
            strokeId: stale.Region.StrokeIds[0]);
        var recognizer = new QueueRecognizer([stale], [fresh]);
        var session = Create(recognizer);
        Stroke[] strokes = Strokes(stale);

        PageRecognitionCandidate staleCandidate = await session.RecognizeAsync(strokes);
        PageRecognitionCandidate freshCandidate = await session.RecognizeAsync(strokes);
        session.ApplyAndCommit(freshCandidate);

        Assert.Throws<InvalidOperationException>(() => session.Apply(staleCandidate));
        Assert.Equal("x=2", session.SheetNodes.Single().Latex);
        Assert.Equal("x=2", session.PreviousRegions.Single().Result.Latex);
    }

    [Fact]
    public async Task ReplaceCacheAndClear_InvalidateOutstandingCandidatesAndResetPage()
    {
        RegionRecognition region = Region("x=1", y: 0);
        var recognizer = new QueueRecognizer([region], [region]);
        var session = Create(recognizer);
        Stroke[] strokes = Strokes(region);

        PageRecognitionCandidate invalidatedByLoad = await session.RecognizeAsync(strokes);
        session.ReplaceCache([region]);
        Assert.Throws<InvalidOperationException>(() => session.Apply(invalidatedByLoad));
        Assert.True(session.PreviousRegions.Single().RequiresAuthoritativeRecognition);

        session.ApplyAndCommit(await session.RecognizeAsync(strokes));
        Assert.Single(session.SheetNodes);
        Assert.False(session.PreviousRegions.Single().RequiresAuthoritativeRecognition);
        session.Clear();

        Assert.Empty(session.SheetNodes);
        Assert.Empty(session.PreviousRegions);
        Assert.Empty(session.AcceptedRegions);
    }

    [Fact]
    public async Task Recognize_RefusesARecognizerThatEchoesPersistedAuthorityMarker()
    {
        RegionRecognition hint = Region("7+7=", y: 0);
        var session = Create(new EchoPreviousRecognizer());
        session.ReplaceCache([hint]);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RecognizeAsync(Strokes(hint)));

        Assert.Contains("authoritative recognition", error.Message, StringComparison.Ordinal);
        Assert.True(session.PreviousRegions.Single().RequiresAuthoritativeRecognition);
        Assert.Empty(session.SheetNodes);
    }

    [Fact]
    public void RuntimeAssembly_IsHeadlessAndDoesNotReferenceAppOrAvalonia()
    {
        string[] references = typeof(PageRecognitionSession).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("Penumbra.App", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    private static PageRecognitionSession Create(IRegionRecognizer recognizer) => new(
        recognizer,
        new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
        RecognitionCalibration.Default.MinConfidence);

    private static RegionRecognition Region(
        string latex,
        double y,
        Guid? id = null,
        Guid? strokeId = null)
    {
        Guid sourceId = strokeId ?? Guid.NewGuid();
        var bounds = new InkBounds(0, y, 40, 20);
        var token = new RecognizedToken(latex, [sourceId], bounds, 0.99);
        var region = new InkRegion(id ?? Guid.NewGuid(), [sourceId], bounds, []);
        return new RegionRecognition(
            region,
            new RecognitionResult(latex, [token], 0.99, 0.99),
            Dirty: true);
    }

    private static RegionRecognition Move(RegionRecognition region, double y) => region with
    {
        Region = region.Region with { Bounds = region.Region.Bounds with { Y = y } },
        Dirty = false,
    };

    private static Stroke[] Strokes(params RegionRecognition[] regions) => regions
        .SelectMany(region => region.Region.StrokeIds)
        .Distinct()
        .Select(id => new Stroke(id,
        [
            new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
            new StrokeSample(10, 10, TimeSpan.FromMilliseconds(10), 0.5),
        ]))
        .ToArray();

    private sealed class QueueRecognizer(params IReadOnlyList<RegionRecognition>[] results)
        : IRegionRecognizer
    {
        private readonly Queue<IReadOnlyList<RegionRecognition>> _results = new(results);

        public List<IReadOnlyList<RegionRecognition>> PreviousArguments { get; } = [];

        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreviousArguments.Add(previous?.ToArray() ?? []);
            return _results.Dequeue();
        }

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RecognizeRegions(strokes, previous, cancellationToken));
    }

    private sealed class EchoPreviousRecognizer : IRegionRecognizer
    {
        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => previous?.ToArray() ?? [];

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RecognizeRegions(strokes, previous, cancellationToken));
    }
}
