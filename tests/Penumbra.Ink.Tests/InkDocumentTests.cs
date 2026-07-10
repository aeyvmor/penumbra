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
    public void EraseStrokeRemovesByIdAndIsUndoable()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);

        doc.EraseStroke(a.Id);

        Assert.Single(doc.Strokes);
        Assert.Equal(b.Id, doc.Strokes[0].Id);
        Assert.True(doc.CanUndo);
    }

    [Fact]
    public void UndoRestoresErasedStrokeAtOriginalPosition()
    {
        // Decision: undo restores an erased stroke to its original slot in draw order, not the end — order
        // is meaningful (paint order, and later stroke↔token alignment), so erase+undo must be a true no-op.
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        Stroke c = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);
        doc.AddStroke(c);

        doc.EraseStroke(b.Id); // erase the middle stroke
        Assert.Equal(new[] { a.Id, c.Id }, doc.Strokes.Select(s => s.Id));

        doc.Undo();

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, doc.Strokes.Select(s => s.Id));
    }

    [Fact]
    public void RedoErasesStrokeAgain()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);
        doc.EraseStroke(a.Id);
        doc.Undo();

        doc.Redo();

        Assert.Single(doc.Strokes);
        Assert.Equal(b.Id, doc.Strokes[0].Id);
    }

    [Fact]
    public void EraseUnknownIdIsNoOpAndDoesNotPolluteUndoStack()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        doc.AddStroke(a);

        doc.EraseStroke(Guid.NewGuid()); // id not present — must record no history

        Assert.Single(doc.Strokes);
        // Undo after the no-op erase must undo the previous real edit (the AddStroke), not a phantom erase.
        doc.Undo();
        Assert.Empty(doc.Strokes);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void EraseStrokesRemovesBatchAsSingleUndoStep()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        Stroke c = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);
        doc.AddStroke(c);

        doc.EraseStrokes(new[] { a.Id, c.Id }); // scratch-out clears two strokes at once
        Assert.Equal(new[] { b.Id }, doc.Strokes.Select(s => s.Id));

        doc.Undo(); // a single undo restores the whole batch to original positions
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, doc.Strokes.Select(s => s.Id));

        doc.Redo(); // and a single redo re-erases the whole batch
        Assert.Equal(new[] { b.Id }, doc.Strokes.Select(s => s.Id));
    }

    [Fact]
    public void EraseStrokes_RestoresReverseOrderedNoncontiguousBatchInDrawOrder()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        Stroke c = NewStroke();
        Stroke d = NewStroke();
        Stroke e = NewStroke();
        foreach (Stroke stroke in new[] { a, b, c, d, e })
        {
            doc.AddStroke(stroke);
        }

        doc.EraseStrokes(new[] { d.Id, b.Id });
        doc.Undo();

        Assert.Equal(new[] { a.Id, b.Id, c.Id, d.Id, e.Id }, doc.Strokes.Select(s => s.Id));
    }

    [Fact]
    public void EraseStrokes_DuplicateRequestIdRemovesAndRestoresOnce()
    {
        var doc = new InkDocument();
        Stroke stroke = NewStroke();
        doc.AddStroke(stroke);

        doc.EraseStrokes(new[] { stroke.Id, stroke.Id });
        Assert.Empty(doc.Strokes);

        doc.Undo();
        Assert.Single(doc.Strokes);
        Assert.Same(stroke, doc.Strokes[0]);
    }

    [Fact]
    public void EraseStrokes_RemovesEveryDocumentStrokeWithARequestedDuplicateId()
    {
        var doc = new InkDocument();
        Guid sharedId = Guid.NewGuid();
        Stroke first = NewStroke() with { Id = sharedId };
        Stroke middle = NewStroke();
        Stroke second = NewStroke() with { Id = sharedId };
        doc.AddStroke(first);
        doc.AddStroke(middle);
        doc.AddStroke(second);

        doc.EraseStrokes(new[] { sharedId });
        Assert.Equal(new[] { middle.Id }, doc.Strokes.Select(s => s.Id));

        doc.Undo();
        Assert.Equal(new[] { first, middle, second }, doc.Strokes);
    }

    [Fact]
    public void EraseStrokesSkipsUnknownIdsButErasesTheRest()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);

        doc.EraseStrokes(new[] { a.Id, Guid.NewGuid() });

        Assert.Equal(new[] { b.Id }, doc.Strokes.Select(s => s.Id));
        doc.Undo();
        Assert.Equal(new[] { a.Id, b.Id }, doc.Strokes.Select(s => s.Id));
    }

    [Fact]
    public void EraseStrokesWithNoMatchingIdsRecordsNoHistory()
    {
        var doc = new InkDocument();
        doc.AddStroke(NewStroke());

        doc.EraseStrokes(new[] { Guid.NewGuid(), Guid.NewGuid() });

        Assert.Single(doc.Strokes);
        doc.Undo(); // must undo the AddStroke, proving the empty batch left no history entry
        Assert.Empty(doc.Strokes);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void EraseDiscardsRedoBranch()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);
        doc.Undo();
        Assert.True(doc.CanRedo);

        doc.EraseStroke(a.Id); // a fresh edit invalidates the redo branch, like any other mutation

        Assert.False(doc.CanRedo);
        Assert.Empty(doc.Strokes);
    }

    [Fact]
    public void EraseFiresChangedOncePerUndoableStep()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);
        int count = 0;
        doc.Changed += (_, _) => count++;

        doc.EraseStroke(a.Id);              // 1
        doc.EraseStrokes(new[] { b.Id });   // 2
        doc.EraseStroke(Guid.NewGuid());    // no-op, fires nothing

        Assert.Equal(2, count);
    }

    [Fact]
    public void LoadClearsEraseRedoBranch()
    {
        var doc = new InkDocument();
        Stroke old = NewStroke();
        doc.AddStroke(old);
        doc.EraseStroke(old.Id);
        doc.Undo();
        Assert.True(doc.CanRedo);

        Stroke loaded = NewStroke();
        doc.Load(PenumbraDocumentSerializer.CreateEmpty() with { Strokes = new[] { loaded } });

        Assert.False(doc.CanUndo);
        Assert.False(doc.CanRedo);
        doc.Redo();
        Assert.Equal(new[] { loaded }, doc.Strokes);
    }

    [Fact]
    public void EraseAndClearComposeThroughUndoRedoInExactOrder()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke b = NewStroke();
        Stroke c = NewStroke();
        doc.AddStroke(a);
        doc.AddStroke(b);
        doc.AddStroke(c);

        doc.EraseStrokes(new[] { c.Id, a.Id });
        doc.Clear();
        doc.Undo();
        Assert.Equal(new[] { b }, doc.Strokes);
        doc.Undo();
        Assert.Equal(new[] { a, b, c }, doc.Strokes);
        doc.Redo();
        Assert.Equal(new[] { b }, doc.Strokes);
        doc.Redo();
        Assert.Empty(doc.Strokes);
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
