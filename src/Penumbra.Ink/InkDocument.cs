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

    /// <summary>Raised after any mutation (add, clear, undo, redo, load) so the view can repaint.</summary>
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
}
