using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class InkDocumentTests
{
    private static Stroke NewStroke() => new(Guid.NewGuid(), new[]
    {
        new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
        new StrokeSample(1, 1, TimeSpan.FromMilliseconds(10), 0.5),
    });

    [Fact]
    public void AddStrokeAppendsAndEnablesUndo()
    {
        var doc = new InkDocument();

        doc.AddStroke(NewStroke());

        Assert.Single(doc.Strokes);
        Assert.True(doc.CanUndo);
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void UndoRemovesLastStrokeAndEnablesRedo()
    {
        var doc = new InkDocument();
        doc.AddStroke(NewStroke());

        doc.Undo();

        Assert.Empty(doc.Strokes);
        Assert.True(doc.CanRedo);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void RedoRestoresUndoneStroke()
    {
        var doc = new InkDocument();
        Stroke stroke = NewStroke();
        doc.AddStroke(stroke);
        doc.Undo();

        doc.Redo();

        Assert.Single(doc.Strokes);
        Assert.Equal(stroke.Id, doc.Strokes[0].Id);
    }

    [Fact]
    public void ClearRemovesAllAndIsUndoable()
    {
        var doc = new InkDocument();
        doc.AddStroke(NewStroke());
        doc.AddStroke(NewStroke());

        doc.Clear();
        Assert.Empty(doc.Strokes);

        doc.Undo();
        Assert.Equal(2, doc.Strokes.Count);
    }

    [Fact]
    public void ClearOnEmptyDocumentRecordsNoHistory()
    {
        var doc = new InkDocument();

        doc.Clear();

        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void NewEditDiscardsRedoBranch()
    {
        var doc = new InkDocument();
        doc.AddStroke(NewStroke());
        doc.Undo();
        Assert.True(doc.CanRedo);

        doc.AddStroke(NewStroke()); // new action invalidates the redo branch

        Assert.False(doc.CanRedo);
        Assert.Single(doc.Strokes);
    }

    [Fact]
    public void UndoAndRedoAreNoOpsWhenHistoryEmpty()
    {
        var doc = new InkDocument();

        doc.Undo();
        doc.Redo();

        Assert.Empty(doc.Strokes);
        Assert.False(doc.CanUndo);
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void ChangedFiresOnMutation()
    {
        var doc = new InkDocument();
        int count = 0;
        doc.Changed += (_, _) => count++;

        doc.AddStroke(NewStroke()); // 1
        doc.Undo();                 // 2
        doc.Redo();                 // 3
        doc.Clear();                // 4

        Assert.Equal(4, count);
    }

    [Fact]
    public void ToDocumentAndLoadRoundTripStrokes()
    {
        var doc = new InkDocument();
        doc.AddStroke(NewStroke());
        doc.AddStroke(NewStroke());

        PenumbraDocument snapshot = doc.ToDocument();
        var reopened = new InkDocument();
        reopened.Load(snapshot);

        Assert.Equal(2, reopened.Strokes.Count);
        Assert.False(reopened.CanUndo); // load resets history
    }
}
