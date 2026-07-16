using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Runtime;

namespace Penumbra.Runtime.Tests;

public sealed class PageStampTransactionTests
{
    [Fact]
    public void Apply_EmptySpaceAppendsFreshSynthesizedInkAsOneUndoableEdit()
    {
        Guid lineStrokeId = Guid.NewGuid();
        RegionRecognition line = Region(
            Guid.NewGuid(),
            new InkBounds(0, 0, 80, 30),
            [Token("2", lineStrokeId, 0, 0, 20, 30)]);
        InkDocument document = Document(Stroke(lineStrokeId, 0, 0, 10, 10));
        Stroke source = Stroke(Guid.NewGuid(), 100, 20, 110, 30);
        Guid[] before = document.Strokes.Select(stroke => stroke.Id).ToArray();

        PageStampResult result = PageStampTransaction.Apply(
            document,
            [line],
            line.Region.Id,
            [source],
            dx: 40,
            dy: 120,
            dropX: 150,
            dropY: 300);

        Assert.Equal(PageStampDecision.Append, result.Decision);
        Assert.Equal(PageStampRefusal.None, result.Refusal);
        Assert.Equal(1, result.AppliedScale);
        Stroke added = Assert.Single(result.AddedStrokes);
        Assert.NotEqual(source.Id, added.Id);
        Assert.Equal([140d, 150d], added.Samples.Select(sample => sample.X));
        Assert.Equal([140d, 150d], added.Samples.Select(sample => sample.Y));
        Assert.Equal(source.Samples.Select(sample => sample.Time), added.Samples.Select(sample => sample.Time));
        Assert.Equal(source.Samples.Select(sample => sample.Pressure), added.Samples.Select(sample => sample.Pressure));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(added.Id));

        document.Undo();
        Assert.Equal(before, document.Strokes.Select(stroke => stroke.Id));
    }

    [Fact]
    public void Apply_DirectLiteralHitAtomicallyReplacesInkAndMatchesTargetLineHeight()
    {
        Guid yId = Guid.NewGuid();
        Guid equalsId = Guid.NewGuid();
        Guid literalId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        RecognizedToken[] tokens =
        [
            Token("y", yId, 0, 80, 20, 40),
            Token("=", equalsId, 30, 90, 20, 20),
            Token("8", literalId, 60, 80, 20, 40),
        ];
        RegionRecognition target = Region(targetId, new InkBounds(0, 80, 80, 40), tokens);
        Stroke[] original =
        [
            Stroke(yId, 0, 80, 10, 120),
            Stroke(equalsId, 30, 90, 50, 110),
            Stroke(literalId, 60, 80, 80, 120),
        ];
        InkDocument document = Document(original);
        Stroke source = Stroke(Guid.NewGuid(), 100, 0, 110, 10);

        PageStampResult result = PageStampTransaction.Apply(
            document,
            [target],
            Guid.NewGuid(),
            [source],
            dx: -35,
            dy: 95,
            dropX: 70,
            dropY: 100);

        Assert.Equal(PageStampDecision.Replace, result.Decision);
        Assert.Equal(4, result.AppliedScale);
        Assert.Equal([literalId], result.RemovedStrokeIds);
        Assert.DoesNotContain(document.Strokes, stroke => stroke.Id == literalId);
        Stroke replacement = Assert.Single(result.AddedStrokes);
        Assert.Equal([50d, 90d], replacement.Samples.Select(sample => sample.X));
        Assert.Equal([80d, 120d], replacement.Samples.Select(sample => sample.Y));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(replacement.Id));

        document.Undo();
        Assert.Equal(original.Select(stroke => stroke.Id), document.Strokes.Select(stroke => stroke.Id));
        Assert.All(document.Strokes, stroke =>
            Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(stroke.Id)));
    }

    [Fact]
    public void Apply_FarHorizontalDropInLineBandRefusesWithoutMutation()
    {
        Guid lineStrokeId = Guid.NewGuid();
        RegionRecognition line = Region(
            Guid.NewGuid(),
            new InkBounds(0, 0, 100, 20),
            [Token("x", lineStrokeId, 0, 0, 20, 20)]);
        InkDocument document = Document(Stroke(lineStrokeId, 0, 0, 20, 20));
        Stroke source = Stroke(Guid.NewGuid(), 100, 0, 110, 10);
        Guid[] before = document.Strokes.Select(stroke => stroke.Id).ToArray();

        PageStampResult result = PageStampTransaction.Apply(
            document,
            [line],
            line.Region.Id,
            [source],
            dx: 900,
            dy: 0,
            dropX: 1000,
            dropY: 10);

        Assert.Equal(PageStampDecision.Refuse, result.Decision);
        Assert.Equal(PageStampRefusal.UnsafeHorizontalDrop, result.Refusal);
        Assert.Null(result.AppliedScale);
        Assert.Empty(result.AddedStrokes);
        Assert.Empty(result.RemovedStrokeIds);
        Assert.Equal(before, document.Strokes.Select(stroke => stroke.Id));
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void AnswerMaterializer_ReplaysGeometryFromVisibleInputWithoutReusingStrokeIds()
    {
        var synthesizer = new HandwritingSynthesizer([new AnyGlyphSource()]);
        RecognizedToken[] tokens =
        [
            Token("1", Guid.NewGuid(), 0, 0, 20, 30),
            Token("=", Guid.NewGuid(), 30, 5, 20, 10),
        ];

        SynthesizedHandwriting first = Assert.IsType<SynthesizedHandwriting>(
            PageAnswerMaterializer.TrySynthesize("2", tokens, synthesizer));
        SynthesizedHandwriting second = Assert.IsType<SynthesizedHandwriting>(
            PageAnswerMaterializer.TrySynthesize("2", tokens, synthesizer));

        Assert.Equal(
            first.Strokes.SelectMany(stroke => stroke.Samples),
            second.Strokes.SelectMany(stroke => stroke.Samples));
        Assert.NotEqual(first.Strokes.Single().Id, second.Strokes.Single().Id);
    }

    private static InkDocument Document(params Stroke[] strokes)
    {
        var document = new InkDocument();
        PenumbraDocument snapshot = PenumbraDocumentSerializer.CreateEmpty() with
        {
            Strokes = strokes,
            StrokeMetadata = strokes.Select(stroke => new PersistedStrokeMetadata(
                stroke.Id,
                StrokeOriginNames.UserInk)).ToArray(),
        };
        document.Load(snapshot);
        return document;
    }

    private static RegionRecognition Region(
        Guid id,
        InkBounds bounds,
        IReadOnlyList<RecognizedToken> tokens) => new(
        new InkRegion(id, tokens.SelectMany(token => token.SourceStrokeIds).ToArray(), bounds, []),
        new RecognitionResult(
            string.Concat(tokens.Select(token => token.Latex)),
            tokens,
            0.99,
            0.99),
        Dirty: true);

    private static RecognizedToken Token(
        string latex,
        Guid strokeId,
        double x,
        double y,
        double width,
        double height) => new(
        latex,
        [strokeId],
        new InkBounds(x, y, width, height),
        0.99);

    private static Stroke Stroke(
        Guid id,
        double x1,
        double y1,
        double x2,
        double y2) => new(id,
        [
            new StrokeSample(x1, y1, TimeSpan.Zero, 0.4),
            new StrokeSample(x2, y2, TimeSpan.FromMilliseconds(10), 0.6),
        ]);

    private sealed class AnyGlyphSource : IGlyphSource
    {
        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random) =>
        [
            Stroke(Guid.NewGuid(), 0.2, 0.1, 0.8, 0.9),
        ];
    }
}
