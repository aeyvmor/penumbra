using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

/// <summary>
/// The compound <see cref="InkDocument.AddStrokes(IEnumerable{Stroke})"/> edit: a multi-stroke insertion
/// (a re-inked taffy literal, a synthesized answer) lands in order and undoes/redoes as ONE step,
/// mirroring the batch-erase contract.
/// </summary>
public sealed class InkDocumentAddStrokesTests
{
    private static Stroke NewStroke() => new(Guid.NewGuid(), new[]
    {
        new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
        new StrokeSample(1, 1, TimeSpan.FromMilliseconds(10), 0.5),
    });

    [Fact]
    public void AddStrokesAppendsBatchInOrder()
    {
        var doc = new InkDocument();
        Stroke existing = NewStroke();
        doc.AddStroke(existing);
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        Stroke c = NewStroke();

        doc.AddStrokes(new[] { a, b, c });

        Assert.Equal(new[] { existing.Id, a.Id, b.Id, c.Id }, doc.Strokes.Select(s => s.Id));
        Assert.True(doc.CanUndo);
    }

    [Fact]
    public void OneUndoRemovesTheWholeBatch()
    {
        var doc = new InkDocument();
        Stroke existing = NewStroke();
        doc.AddStroke(existing);
        doc.AddStrokes(new[] { NewStroke(), NewStroke(), NewStroke() });

        doc.Undo();

        Assert.Equal(new[] { existing.Id }, doc.Strokes.Select(s => s.Id));
        Assert.True(doc.CanRedo);
    }

    [Fact]
    public void OneRedoRestoresTheWholeBatchInOrder()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        Stroke c = NewStroke();
        doc.AddStrokes(new[] { a, b, c });
        doc.Undo();

        doc.Redo();

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, doc.Strokes.Select(s => s.Id));
    }

    [Fact]
    public void EmptyBatchIsNoOpWithNoHistoryAndNoEvent()
    {
        var doc = new InkDocument();
        doc.AddStroke(NewStroke());
        int events = 0;
        doc.Changed += (_, _) => events++;

        doc.AddStrokes(Array.Empty<Stroke>());

        Assert.Equal(0, events);
        Assert.Single(doc.Strokes);
        // Undo after the no-op must undo the previous real edit, proving no phantom history entry.
        doc.Undo();
        Assert.Empty(doc.Strokes);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void RaisesSingleChangedEventPerCall()
    {
        var doc = new InkDocument();
        int events = 0;
        doc.Changed += (_, _) => events++;

        doc.AddStrokes(new[] { NewStroke(), NewStroke(), NewStroke() });

        Assert.Equal(1, events);
    }

    [Fact]
    public void AddStrokesAfterUndoClearsRedoBranch()
    {
        var doc = new InkDocument();
        doc.AddStroke(NewStroke());
        doc.Undo();
        Assert.True(doc.CanRedo);

        doc.AddStrokes(new[] { NewStroke() }); // a fresh edit invalidates the redo branch

        Assert.False(doc.CanRedo);
        Assert.Single(doc.Strokes);
    }

    [Fact]
    public void NullBatchThrows()
    {
        var doc = new InkDocument();

        Assert.Throws<ArgumentNullException>(() => doc.AddStrokes(null!));
    }
}
