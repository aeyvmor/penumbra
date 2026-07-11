using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

/// <summary>Atomic replace semantics used when an answer stamp lands directly on a recognized literal.</summary>
public sealed class InkDocumentReplaceStrokesTests
{
    private static Stroke NewStroke(Guid? id = null) => new(id ?? Guid.NewGuid(), new[]
    {
        new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
        new StrokeSample(1, 1, TimeSpan.FromMilliseconds(10), 0.5),
    });

    [Fact]
    public void ReplaceRemovesRequestedStrokesAndAppendsReplacementInOrder()
    {
        var doc = new InkDocument();
        Stroke keepA = NewStroke();
        Stroke removeA = NewStroke();
        Stroke keepB = NewStroke();
        Stroke removeB = NewStroke();
        doc.AddStrokes(new[] { keepA, removeA, keepB, removeB });
        Stroke addA = NewStroke();
        Stroke addB = NewStroke();

        doc.ReplaceStrokes(new[] { removeA.Id, removeB.Id }, new[] { addA, addB });

        Assert.Equal(new[] { keepA.Id, keepB.Id, addA.Id, addB.Id }, doc.Strokes.Select(s => s.Id));
    }

    [Fact]
    public void OneUndoAndRedoRoundTripExactOriginalOrder()
    {
        var doc = new InkDocument();
        Stroke a = NewStroke();
        Stroke removed = NewStroke();
        Stroke b = NewStroke();
        Stroke replacement = NewStroke();
        doc.AddStrokes(new[] { a, removed, b });

        doc.ReplaceStrokes(new[] { removed.Id }, new[] { replacement });
        doc.Undo();
        Assert.Equal(new[] { a.Id, removed.Id, b.Id }, doc.Strokes.Select(s => s.Id));

        doc.Redo();
        Assert.Equal(new[] { a.Id, b.Id, replacement.Id }, doc.Strokes.Select(s => s.Id));
    }

    [Fact]
    public void DuplicateDocumentIdsAreAllReplacedAndOneEventFires()
    {
        Guid duplicate = Guid.NewGuid();
        var doc = new InkDocument();
        doc.AddStrokes(new[] { NewStroke(duplicate), NewStroke(), NewStroke(duplicate) });
        int events = 0;
        doc.Changed += (_, _) => events++;

        doc.ReplaceStrokes(new[] { duplicate }, new[] { NewStroke() });

        Assert.DoesNotContain(doc.Strokes, stroke => stroke.Id == duplicate);
        Assert.Equal(1, events);
    }

    [Fact]
    public void EmptyOrUnmatchedReplaceIsNoOpWithoutHistoryOrEvent()
    {
        var doc = new InkDocument();
        Stroke existing = NewStroke();
        doc.AddStroke(existing);
        int events = 0;
        doc.Changed += (_, _) => events++;

        doc.ReplaceStrokes(new[] { Guid.NewGuid() }, Array.Empty<Stroke>());

        Assert.Equal(0, events);
        doc.Undo();
        Assert.Empty(doc.Strokes); // no phantom replace history: undo reached the original add
    }
}
