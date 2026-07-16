using Microsoft.Extensions.DependencyInjection;
using Penumbra.App.Services;
using Penumbra.Core;
using Penumbra.Graphing;
using Penumbra.Recognition;

namespace Penumbra.App.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void ProductionRegistrationsResolveOneRecognizerWithNoOpObservabilityDefaults()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddPenumbraApp()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        IRecognizer legacy = provider.GetRequiredService<IRecognizer>();
        IRegionRecognizer regions = provider.GetRequiredService<IRegionRecognizer>();

        Assert.Same(legacy, regions);
        Assert.IsType<RegionSegmenter>(provider.GetRequiredService<IRegionSegmenter>());
        Assert.Same(NoOpLocalMetricsSink.Instance, provider.GetRequiredService<ILocalMetricsSink>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.IsType<FileSystemPageStore>(provider.GetRequiredService<IPageStore>());

        // Phase 6: the real graphing seams replaced the NoOp placeholder detector.
        Assert.IsType<GraphDetector>(provider.GetRequiredService<IGraphDetector>());
        Assert.IsType<DomainSampler>(provider.GetRequiredService<IDomainSampler>());
    }

    [Fact]
    public void ProductionRecognizerUsesEveryPreRegisteredPipelineDependency()
    {
        Stroke stroke = InkStroke();
        var strokeSegmenter = new RecordingStrokeSegmenter();
        var regionSegmenter = new RecordingRegionSegmenter();
        var classifier = new ConstantClassifier("7");
        var metrics = new BoundedInMemoryMetricsSink(32);
        var time = new CountingTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IStrokeSegmenter>(strokeSegmenter);
        services.AddSingleton<IRegionSegmenter>(regionSegmenter);
        services.AddSingleton<ISymbolClassifier>(classifier);
        services.AddSingleton<ILocalMetricsSink>(metrics);
        services.AddSingleton<TimeProvider>(time);

        using ServiceProvider provider = services
            .AddPenumbraApp()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        IRegionRecognizer regions = provider.GetRequiredService<IRegionRecognizer>();
        IReadOnlyList<RegionRecognition> regionResults = regions.RecognizeRegions(new[] { stroke });
        RecognitionResult pageResult = provider.GetRequiredService<IRecognizer>().Recognize(new[] { stroke });

        Assert.Single(regionResults);
        Assert.Equal("7", regionResults[0].Result.Latex);
        Assert.Equal("7", pageResult.Latex);
        Assert.Equal(1, regionSegmenter.CallCount);
        Assert.Equal(1, strokeSegmenter.CallCount);
        Assert.Equal(2, classifier.CallCount);
        Assert.True(time.TimestampReadCount > 0);
        Assert.Contains(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.RecognitionProcessing);
        Assert.Same(strokeSegmenter, provider.GetRequiredService<IStrokeSegmenter>());
        Assert.Same(regionSegmenter, provider.GetRequiredService<IRegionSegmenter>());
        Assert.Same(classifier, provider.GetRequiredService<ISymbolClassifier>());
        Assert.Same(metrics, provider.GetRequiredService<ILocalMetricsSink>());
        Assert.Same(time, provider.GetRequiredService<TimeProvider>());
    }

    private static Stroke InkStroke() => new(
        Guid.NewGuid(),
        new[]
        {
            new StrokeSample(10, 10, TimeSpan.Zero, .5),
            new StrokeSample(20, 20, TimeSpan.FromMilliseconds(10), .5),
        });

    private sealed class RecordingStrokeSegmenter : IStrokeSegmenter
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<StrokeGroup> Segment(IReadOnlyList<Stroke> strokes)
        {
            CallCount++;
            return strokes.Count == 0
                ? Array.Empty<StrokeGroup>()
                : new[] { Group(strokes) };
        }
    }

    private sealed class RecordingRegionSegmenter : IRegionSegmenter
    {
        public int CallCount { get; private set; }

        public InkSegmentation Segment(IReadOnlyList<Stroke> strokes) => Segment(strokes, previous: null);

        public InkSegmentation Segment(IReadOnlyList<Stroke> strokes, InkSegmentation? previous)
        {
            CallCount++;
            if (strokes.Count == 0)
            {
                return new InkSegmentation(Array.Empty<InkRegion>());
            }

            StrokeGroup group = Group(strokes);
            return new InkSegmentation(new[]
            {
                new InkRegion(
                    Guid.Parse("0f589314-999d-4f20-91ae-9ce05000379c"),
                    strokes.Select(stroke => stroke.Id).ToArray(),
                    group.Bounds,
                    new[] { group }),
            });
        }
    }

    private sealed class ConstantClassifier(string label) : ISymbolClassifier
    {
        public int CallCount { get; private set; }

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            CallCount++;
            return new SymbolPrediction(label, .99);
        }
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public int TimestampReadCount { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            TimestampReadCount++;
            return ++_timestamp;
        }
    }

    private static StrokeGroup Group(IReadOnlyList<Stroke> strokes) =>
        new(strokes, new InkBounds(10, 10, 10, 10));
}
