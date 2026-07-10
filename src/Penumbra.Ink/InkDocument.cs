using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// The in-memory, headless editing model the canvas binds to: an ordered list of strokes plus an
/// undo/redo history expressed as reversible edits. Holds no UI or Avalonia types so the editing logic
/// can be unit-tested on its own. Bridges to <see cref="PenumbraDocument"/> for save/load.
/// </summary>
public sealed class InkDocument
{
    private readonly List<Stroke> _strokes = new();
    private readonly Stack<IInkEdit> _undo = new();
    private readonly Stack<IInkEdit> _redo = new();

    /// <summary>Current strokes in draw order.</summary>
    public IReadOnlyList<Stroke> Strokes => _strokes;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after any mutation (add, erase, clear, undo, redo, load) so the view can repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>Appends a finished stroke and records it as an undoable edit.</summary>
    public void AddStroke(Stroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        Apply(new AddStrokeEdit(stroke));
    }

    /// <summary>Removes every stroke as a single undoable edit. No-op (and no history entry) when empty.</summary>
    public void Clear()
    {
        if (_strokes.Count == 0)
        {
            return;
        }

        Apply(new ClearEdit(_strokes.ToList()));
    }

    /// <summary>
    /// Removes the stroke with the given id as a single undoable edit; undo restores it at its original
    /// position in draw order. Erasing an id that isn't present is a harmless no-op that records no history.
    /// </summary>
    public void EraseStroke(Guid id)
        => EraseStrokes(new[] { id });

    /// <summary>
    /// Removes every stroke whose id is in <paramref name="ids"/> as a single undoable edit — the whole
    /// batch undoes/redoes as one step (a scratch-out gesture that clears several strokes at once). Ids not
    /// present are skipped; if none match this is a harmless no-op that records no history. Undo restores
    /// each stroke at its original position in draw order.
    /// </summary>
    public void EraseStrokes(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Resolve ids against the current order up front, before any removal shifts indices. Deduping via a
        // set keeps a repeated id from capturing (and later re-inserting) the same stroke twice.
        HashSet<Guid> requested = ids.ToHashSet();
        var removals = new List<(int Index, Stroke Stroke)>();
        for (int index = 0; index < _strokes.Count; index++)
        {
            if (requested.Contains(_strokes[index].Id))
            {
                removals.Add((index, _strokes[index]));
            }
        }

        if (removals.Count == 0)
        {
            return;
        }

        Apply(new EraseStrokesEdit(removals));
    }

    /// <summary>Reverts the most recent edit, if any.</summary>
    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        IInkEdit edit = _undo.Pop();
        edit.Revert(_strokes);
        _redo.Push(edit);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replays the most recently undone edit, if any.</summary>
    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        IInkEdit edit = _redo.Pop();
        edit.Apply(_strokes);
        _undo.Push(edit);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Snapshots the current strokes into a persistable document.</summary>
    public PenumbraDocument ToDocument() =>
        PenumbraDocumentSerializer.CreateEmpty() with { Strokes = _strokes.ToList() };

    /// <summary>Replaces all content from a loaded document and discards the edit history.</summary>
    public void Load(PenumbraDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _strokes.Clear();
        _strokes.AddRange(document.Strokes);
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(IInkEdit edit)
    {
        edit.Apply(_strokes);
        _undo.Push(edit);
        _redo.Clear(); // a fresh edit invalidates the redo branch
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A reversible mutation of the stroke list.</summary>
    private interface IInkEdit
    {
        void Apply(List<Stroke> strokes);
        void Revert(List<Stroke> strokes);
    }

    private sealed class AddStrokeEdit : IInkEdit
    {
        private readonly Stroke _stroke;

        public AddStrokeEdit(Stroke stroke) => _stroke = stroke;

        public void Apply(List<Stroke> strokes) => strokes.Add(_stroke);

        public void Revert(List<Stroke> strokes) => strokes.Remove(_stroke);
    }

    private sealed class ClearEdit : IInkEdit
    {
        private readonly IReadOnlyList<Stroke> _removed;

        public ClearEdit(IReadOnlyList<Stroke> removed) => _removed = removed;

        public void Apply(List<Stroke> strokes) => strokes.Clear();

        public void Revert(List<Stroke> strokes) => strokes.AddRange(_removed);
    }

    private sealed class EraseStrokesEdit : IInkEdit
    {
        // Each removed stroke paired with the index it held before removal, ordered by ascending index so
        // Revert can re-insert them low-to-high — restoring every stroke to its original slot in draw order.
        private readonly IReadOnlyList<(int Index, Stroke Stroke)> _removed;

        public EraseStrokesEdit(IEnumerable<(int Index, Stroke Stroke)> removed) =>
            _removed = removed.OrderBy(r => r.Index).ToList();

        public void Apply(List<Stroke> strokes)
        {
            foreach ((_, Stroke stroke) in _removed)
            {
                strokes.Remove(stroke);
            }
        }

        public void Revert(List<Stroke> strokes)
        {
            // Ascending order keeps each captured index valid: lower slots are refilled before we reach the
            // higher ones, so every stroke lands back where it started.
            foreach ((int index, Stroke stroke) in _removed)
            {
                strokes.Insert(index, stroke);
            }
        }
    }
}
