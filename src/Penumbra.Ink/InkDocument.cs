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
    // A GUID is a persistence key, not an object identity: hostile files and undo branches can reuse one
    // ID for distinct strokes. Keep their origins separate until a present-ID query resolves ambiguity.
    private readonly Dictionary<Stroke, StrokeOriginKind> _strokeOrigins =
        new(ReferenceEqualityComparer.Instance);
    private readonly Stack<IInkEdit> _undo = new();
    private readonly Stack<IInkEdit> _redo = new();

    /// <summary>Current strokes in draw order.</summary>
    public IReadOnlyList<Stroke> Strokes => _strokes;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after any mutation (add, erase, clear, undo, redo, load) so the view can repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>Appends a finished stroke and records it as an undoable edit.</summary>
    public void AddStroke(Stroke stroke) => AddStroke(stroke, StrokeOriginKind.UserInk);

    /// <summary>Appends a finished stroke with an explicit writable origin as an undoable edit.</summary>
    public void AddStroke(Stroke stroke, StrokeOriginKind origin)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ValidateNewStrokeOrigin(origin);
        RegisterOrigin(stroke, origin);
        Apply(new AddStrokeEdit(stroke));
    }

    /// <summary>
    /// Appends a batch of strokes, in the given order, as a single undoable edit — the whole batch
    /// undoes/redoes as one step (a multi-stroke insertion like a re-inked taffy literal must vanish
    /// with one undo), mirroring the batch contract of <see cref="EraseStrokes"/>. An empty batch is
    /// a harmless no-op that records no history and raises no event; otherwise exactly one
    /// <see cref="Changed"/> fires per call and, like any fresh edit, the redo branch is discarded.
    /// </summary>
    public void AddStrokes(IEnumerable<Stroke> strokes) =>
        AddStrokes(strokes, StrokeOriginKind.UserInk);

    /// <summary>
    /// Appends a batch with one explicit writable origin as a single undoable edit.
    /// </summary>
    public void AddStrokes(IEnumerable<Stroke> strokes, StrokeOriginKind origin)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ValidateNewStrokeOrigin(origin);

        var batch = strokes.ToList();
        if (batch.Count == 0)
        {
            return;
        }

        RegisterOrigins(batch, origin);
        Apply(new AddStrokesEdit(batch));
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

    /// <summary>
    /// Atomically removes every current stroke whose id appears in <paramref name="removedIds"/> and
    /// appends <paramref name="addedStrokes"/> in order as one undoable edit. This is the literal-drop
    /// contract: replacing a handwritten value must not leave the old ink underneath or require two undo
    /// steps. Duplicate document ids are all removed; unknown ids are ignored. When no stroke matches and
    /// the addition is empty, the call is a no-op with no history/event. Otherwise exactly one
    /// <see cref="Changed"/> event fires, undo restores the removed strokes at their exact original indices
    /// and removes the addition, and redo reapplies the same replacement.
    /// </summary>
    public void ReplaceStrokes(IEnumerable<Guid> removedIds, IEnumerable<Stroke> addedStrokes) =>
        ReplaceStrokes(removedIds, addedStrokes, StrokeOriginKind.UserInk);

    /// <summary>
    /// Atomically replaces strokes and assigns one explicit writable origin to every addition.
    /// </summary>
    public void ReplaceStrokes(
        IEnumerable<Guid> removedIds,
        IEnumerable<Stroke> addedStrokes,
        StrokeOriginKind addedOrigin)
    {
        ArgumentNullException.ThrowIfNull(removedIds);
        ArgumentNullException.ThrowIfNull(addedStrokes);
        ValidateNewStrokeOrigin(addedOrigin);

        HashSet<Guid> requested = removedIds.ToHashSet();
        var removals = new List<(int Index, Stroke Stroke)>();
        for (int index = 0; index < _strokes.Count; index++)
        {
            if (requested.Contains(_strokes[index].Id))
            {
                removals.Add((index, _strokes[index]));
            }
        }

        var additions = addedStrokes.ToList();
        if (removals.Count == 0 && additions.Count == 0)
        {
            return;
        }

        RegisterOrigins(additions, addedOrigin);
        Apply(new ReplaceStrokesEdit(removals, additions));
    }

    /// <summary>
    /// Gets the origin of one uniquely identified present stroke. Missing, empty, or duplicate IDs are
    /// conservatively <see cref="StrokeOriginKind.Unknown"/>.
    /// </summary>
    public StrokeOriginKind GetStrokeOrigin(Guid strokeId)
    {
        if (strokeId == Guid.Empty)
        {
            return StrokeOriginKind.Unknown;
        }

        Stroke? match = null;
        foreach (Stroke stroke in _strokes)
        {
            if (stroke.Id != strokeId)
            {
                continue;
            }

            if (match is not null)
            {
                return StrokeOriginKind.Unknown;
            }

            match = stroke;
        }

        return match is null ? StrokeOriginKind.Unknown : GetInstanceOrigin(match);
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
    public PenumbraDocument ToDocument() => PenumbraDocumentSerializer.CreateEmpty() with
    {
        Strokes = _strokes.ToList(),
        StrokeMetadata = _strokes
            .Select(stroke => new PersistedStrokeMetadata(
                stroke.Id,
                ToPersistedOrigin(GetInstanceOrigin(stroke))))
            .ToList(),
    };

    /// <summary>Replaces all content from a loaded document and discards the edit history.</summary>
    public void Load(PenumbraDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        StrokeProvenanceResolution provenance = StrokeProvenanceResolver.Resolve(document);
        Stroke[] strokes = (document.Strokes ?? Array.Empty<Stroke>())
            .Where(stroke => stroke is not null)
            .ToArray();

        _strokes.Clear();
        _strokes.AddRange(strokes);
        _strokeOrigins.Clear();
        foreach (Stroke stroke in strokes)
        {
            _strokeOrigins[stroke] = provenance.GetOrigin(stroke.Id);
        }

        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void ValidateNewStrokeOrigin(StrokeOriginKind origin)
    {
        if (origin is not StrokeOriginKind.UserInk and not StrokeOriginKind.SynthesizedInk)
        {
            throw new ArgumentOutOfRangeException(
                nameof(origin),
                origin,
                "New ink must be identified as user or synthesized ink.");
        }
    }

    private static string ToPersistedOrigin(StrokeOriginKind origin) => origin switch
    {
        StrokeOriginKind.UserInk => StrokeOriginNames.UserInk,
        StrokeOriginKind.SynthesizedInk => StrokeOriginNames.SynthesizedInk,
        StrokeOriginKind.LegacyUnspecified => StrokeOriginNames.LegacyUnspecified,
        _ => string.Empty,
    };

    private static void RemoveByReference(List<Stroke> strokes, Stroke stroke)
    {
        int index = strokes.FindIndex(candidate => ReferenceEquals(candidate, stroke));
        if (index >= 0)
        {
            strokes.RemoveAt(index);
        }
    }

    private void RegisterOrigins(IEnumerable<Stroke> strokes, StrokeOriginKind origin)
    {
        Stroke[] batch = strokes.ToArray();
        foreach (Stroke stroke in batch)
        {
            ArgumentNullException.ThrowIfNull(stroke);
        }

        foreach (Stroke stroke in batch)
        {
            RegisterOrigin(stroke, origin);
        }
    }

    private void RegisterOrigin(Stroke stroke, StrokeOriginKind origin)
    {
        // Reusing the exact same object with two claimed origins cannot be represented honestly. Keep
        // the geometry/edit valid but make that instance conservatively non-bankable.
        if (_strokeOrigins.TryGetValue(stroke, out StrokeOriginKind existing) && existing != origin)
        {
            _strokeOrigins[stroke] = StrokeOriginKind.Unknown;
            return;
        }

        _strokeOrigins[stroke] = origin;
    }

    private StrokeOriginKind GetInstanceOrigin(Stroke stroke) =>
        _strokeOrigins.TryGetValue(stroke, out StrokeOriginKind origin)
            ? origin
            : StrokeOriginKind.Unknown;

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

        public void Revert(List<Stroke> strokes) => RemoveByReference(strokes, _stroke);
    }

    private sealed class AddStrokesEdit : IInkEdit
    {
        private readonly IReadOnlyList<Stroke> _added;

        public AddStrokesEdit(IReadOnlyList<Stroke> added) => _added = added;

        public void Apply(List<Stroke> strokes) => strokes.AddRange(_added);

        // Undo runs strictly LIFO (the same guarantee ClearEdit's Revert leans on), so when Revert
        // fires the batch still sits at the tail exactly where Apply appended it — cut that tail
        // range in one O(batch) removal instead of value-equality scans per stroke.
        public void Revert(List<Stroke> strokes) =>
            strokes.RemoveRange(strokes.Count - _added.Count, _added.Count);
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
                RemoveByReference(strokes, stroke);
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

    private sealed class ReplaceStrokesEdit : IInkEdit
    {
        private readonly IReadOnlyList<(int Index, Stroke Stroke)> _removed;
        private readonly IReadOnlyList<Stroke> _added;

        public ReplaceStrokesEdit(
            IEnumerable<(int Index, Stroke Stroke)> removed,
            IReadOnlyList<Stroke> added)
        {
            _removed = removed.OrderBy(item => item.Index).ToList();
            _added = added;
        }

        public void Apply(List<Stroke> strokes)
        {
            foreach ((_, Stroke stroke) in _removed)
            {
                RemoveByReference(strokes, stroke);
            }

            strokes.AddRange(_added);
        }

        public void Revert(List<Stroke> strokes)
        {
            // Replacement additions are always the tail at LIFO undo time, exactly like AddStrokesEdit.
            if (_added.Count > 0)
            {
                strokes.RemoveRange(strokes.Count - _added.Count, _added.Count);
            }

            foreach ((int index, Stroke stroke) in _removed)
            {
                strokes.Insert(index, stroke);
            }
        }
    }
}
