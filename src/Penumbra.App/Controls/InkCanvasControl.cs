using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.App.Controls;

/// <summary>
/// The drawing surface. Captures pointer samples <c>(x, y, t, pressure)</c> into strokes, renders them
/// with pressure/velocity-driven width, and supports an infinite pannable, zoomable canvas. Strokes are
/// kept in world coordinates; a single scale+translate maps world to screen for both drawing and input.
/// Left button draws or erases according to the toolbar tool; pen eraser/inversion always erases;
/// right/middle button pans; the wheel zooms about the cursor.
/// </summary>
public sealed class InkCanvasControl : Control
{
    public static readonly StyledProperty<InkDocument?> DocumentProperty =
        AvaloniaProperty.Register<InkCanvasControl, InkDocument?>(nameof(Document));

    public static readonly StyledProperty<AnswerLayer?> AnswerLayerProperty =
        AvaloniaProperty.Register<InkCanvasControl, AnswerLayer?>(nameof(AnswerLayer));

    public static readonly StyledProperty<CausalityRipple?> CausalityRippleProperty =
        AvaloniaProperty.Register<InkCanvasControl, CausalityRipple?>(nameof(CausalityRipple));

    public static readonly StyledProperty<bool> IsEraseModeProperty =
        AvaloniaProperty.Register<InkCanvasControl, bool>(nameof(IsEraseMode));

    // 4.5c: strokes the recognizer can't stand behind — rendered desaturated + shivering.
    public static readonly StyledProperty<IReadOnlySet<Guid>?> UncertainStrokeIdsProperty =
        AvaloniaProperty.Register<InkCanvasControl, IReadOnlySet<Guid>?>(nameof(UncertainStrokeIds));

    // 4.5d: strokes highlighted as the displayed answer's provenance (amber glow underlay).
    public static readonly StyledProperty<IReadOnlySet<Guid>?> ProvenanceStrokeIdsProperty =
        AvaloniaProperty.Register<InkCanvasControl, IReadOnlySet<Guid>?>(nameof(ProvenanceStrokeIds));

    /// <summary>4.5b: the user just started drawing a stroke (pen/mouse down) — live reads go stale now.</summary>
    public event EventHandler? DrawingStarted;

    /// <summary>4.5d: the user tapped the played answer (the tap is consumed, not inked).</summary>
    public event EventHandler<AnswerTappedEventArgs>? AnswerTapped;

    // Wall-clock → animation-time multiplier. Captured pen pace can feel sluggish; bump this to speed up the
    // "write-itself" playback. One named knob so tuning later is a single-line change.
    private const double PlaybackSpeed = 1.9;

    // Repaint cadence while an answer animates (~60 fps).
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    // 4.5d tap geometry, in SCREEN pixels so the gesture feels the same at any zoom: a tap's samples all
    // stay within the slop, and the tap counts only if it lands within the tolerance of answer ink.
    private const double TapSlopScreenPx = 6;
    private const double TapToleranceScreenPx = 12;
    private const double EraserToleranceScreenPx = 14;
    private static readonly TimeSpan RippleStepDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RippleHoldDuration = TimeSpan.FromMilliseconds(260);

    // 4.5c glitch: subtle shiver, in world units. Slow and small on purpose — "unsettled", not "alarm".
    private const double GlitchAmplitude = 1.4;

    private static readonly IImmutableSolidColorBrush PaperBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xFB));
    private static readonly IImmutableSolidColorBrush InkBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x18, 0x20, 0x2A));

    // Desaturated ink for uncertain strokes: clearly "less real" than ink without becoming invisible.
    private static readonly IImmutableSolidColorBrush UncertainBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xB5));

    // Amber underlay for provenance-highlighted strokes.
    private static readonly IImmutableSolidColorBrush GlowBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B));
    private static readonly IImmutableSolidColorBrush RippleBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0x38, 0xB2, 0xAC));

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StrokeWidthModel _widthModel = new();

    // Owner-keyed answer playback lives above document ink and is never folded into it. One shared repaint
    // timer services every concurrently playing entry; finished entries remain at their final frame.
    private readonly Dictionary<Guid, AnswerPlayback> _answers = new();
    private DispatcherTimer? _answerTimer;
    private CausalityRipple? _ripple;
    private TimeSpan _rippleStart;
    private DispatcherTimer? _rippleTimer;

    // Ticks repaints while any stroke is glitching (4.5c); null when the uncertain set is empty.
    private DispatcherTimer? _glitchTimer;

    // The document stores raw pen data (for recognizer/glyph-bank parity). Smoothing is display-only and
    // happens here at render time, cached per stroke id so we don't re-smooth every frame.
    private readonly SmoothedStrokeCache _smoothCache = new(new ChaikinStrokeSmoother());

    // World→screen view transform: screen = world * scale + offset.
    private double _scale = 1.0;
    private double _offsetX;
    private double _offsetY;

    // Active stroke being drawn (world coordinates), or null when not drawing.
    private List<StrokeSample>? _activeSamples;
    private TimeSpan _strokeStart;
    private HashSet<Guid>? _eraseHits;

    // Panning state.
    private bool _isPanning;
    private double _lastPanX;
    private double _lastPanY;

    public InkCanvasControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public InkDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>All owner-keyed answers to render above document ink.</summary>
    public AnswerLayer? AnswerLayer
    {
        get => GetValue(AnswerLayerProperty);
        set => SetValue(AnswerLayerProperty, value);
    }

    public CausalityRipple? CausalityRipple
    {
        get => GetValue(CausalityRippleProperty);
        set => SetValue(CausalityRippleProperty, value);
    }

    public bool IsEraseMode
    {
        get => GetValue(IsEraseModeProperty);
        set => SetValue(IsEraseModeProperty, value);
    }

    /// <summary>Strokes rendered as glitch-ink (4.5c), or null/empty for none.</summary>
    public IReadOnlySet<Guid>? UncertainStrokeIds
    {
        get => GetValue(UncertainStrokeIdsProperty);
        set => SetValue(UncertainStrokeIdsProperty, value);
    }

    /// <summary>Strokes highlighted as answer provenance (4.5d), or null/empty for none.</summary>
    public IReadOnlySet<Guid>? ProvenanceStrokeIds
    {
        get => GetValue(ProvenanceStrokeIdsProperty);
        set => SetValue(ProvenanceStrokeIdsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DocumentProperty)
        {
            if (change.OldValue is InkDocument oldDoc)
            {
                oldDoc.Changed -= OnDocumentChanged;
            }

            if (change.NewValue is InkDocument newDoc)
            {
                newDoc.Changed += OnDocumentChanged;
            }

            InvalidateVisual();
        }
        else if (change.Property == AnswerLayerProperty)
        {
            OnAnswerLayerChanged(change.NewValue as AnswerLayer);
        }
        else if (change.Property == CausalityRippleProperty)
        {
            OnCausalityRippleChanged(change.NewValue as CausalityRipple);
        }
        else if (change.Property == UncertainStrokeIdsProperty)
        {
            OnUncertainStrokeIdsChanged(change.NewValue as IReadOnlySet<Guid>);
        }
        else if (change.Property == ProvenanceStrokeIdsProperty)
        {
            InvalidateVisual();
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => InvalidateVisual();

    // --- Answer animation ------------------------------------------------------------------------

    private void OnAnswerLayerChanged(AnswerLayer? layer)
    {
        HashSet<Guid> incoming = layer?.Answers.Select(answer => answer.OwnerId).ToHashSet() ?? new();
        foreach (Guid removed in _answers.Keys.Where(id => !incoming.Contains(id)).ToArray())
        {
            _answers.Remove(removed);
        }

        foreach (AnswerAnimation answer in layer?.Answers ?? Array.Empty<AnswerAnimation>())
        {
            if (_answers.TryGetValue(answer.OwnerId, out AnswerPlayback? current)
                && current.Animation.Sequence == answer.Sequence)
            {
                continue;
            }

            TimeSpan start = answer.Play
                ? _clock.Elapsed
                : _clock.Elapsed - answer.Handwriting.Timeline.TotalDuration / PlaybackSpeed;
            _answers[answer.OwnerId] = new AnswerPlayback(answer, start);
        }

        EnsureAnswerTimer();
        InvalidateVisual();
    }

    private void EnsureAnswerTimer()
    {
        bool playing = _answers.Values.Any(answer =>
            AnswerElapsed(answer) < answer.Animation.Handwriting.Timeline.TotalDuration);
        if (!playing)
        {
            StopAnswerTimer();
            return;
        }

        if (_answerTimer is null)
        {
            _answerTimer = new DispatcherTimer { Interval = FrameInterval };
            _answerTimer.Tick += OnAnswerTick;
            _answerTimer.Start();
        }
    }

    private void OnAnswerTick(object? sender, EventArgs e)
    {
        InvalidateVisual();

        // Once the whole timeline has played, stop ticking but KEEP _answer so Render holds the final frame
        // (SampleAt clamps past TotalDuration), leaving the finished answer on the page.
        if (_answers.Values.All(answer =>
            AnswerElapsed(answer) >= answer.Animation.Handwriting.Timeline.TotalDuration))
        {
            StopAnswerTimer();
        }
    }

    private TimeSpan AnswerElapsed(AnswerPlayback answer) => (_clock.Elapsed - answer.Start) * PlaybackSpeed;

    private void StopAnswerTimer()
    {
        if (_answerTimer is null)
        {
            return;
        }

        _answerTimer.Stop();
        _answerTimer.Tick -= OnAnswerTick;
        _answerTimer = null;
    }

    private void OnCausalityRippleChanged(CausalityRipple? ripple)
    {
        StopRippleTimer();
        _ripple = ripple;
        if (ripple is not null)
        {
            _rippleStart = _clock.Elapsed;
            _rippleTimer = new DispatcherTimer { Interval = FrameInterval };
            _rippleTimer.Tick += OnRippleTick;
            _rippleTimer.Start();
        }

        InvalidateVisual();
    }

    private void OnRippleTick(object? sender, EventArgs e)
    {
        InvalidateVisual();
        if (_ripple is null
            || _clock.Elapsed - _rippleStart >= RippleStepDuration * _ripple.Steps.Count + RippleHoldDuration)
        {
            StopRippleTimer();
            _ripple = null;
        }
    }

    private void StopRippleTimer()
    {
        if (_rippleTimer is null) return;
        _rippleTimer.Stop();
        _rippleTimer.Tick -= OnRippleTick;
        _rippleTimer = null;
    }

    // --- Glitch-ink (4.5c) -------------------------------------------------------------------------

    // The shiver is time-driven, so it needs a repaint tick — but only while something is uncertain.
    private void OnUncertainStrokeIdsChanged(IReadOnlySet<Guid>? ids)
    {
        if (ids is { Count: > 0 })
        {
            if (_glitchTimer is null)
            {
                _glitchTimer = new DispatcherTimer { Interval = FrameInterval };
                _glitchTimer.Tick += OnGlitchTick;
                _glitchTimer.Start();
            }
        }
        else
        {
            StopGlitchTimer();
        }

        InvalidateVisual();
    }

    private void OnGlitchTick(object? sender, EventArgs e) => InvalidateVisual();

    private void StopGlitchTimer()
    {
        if (_glitchTimer is null)
        {
            return;
        }

        _glitchTimer.Stop();
        _glitchTimer.Tick -= OnGlitchTick;
        _glitchTimer = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopAnswerTimer();   // don't leave timers running against a detached control.
        StopGlitchTimer();
        StopRippleTimer();
    }

    // --- Rendering -------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Opaque fill makes the whole surface paint and stay hit-testable for pointer input.
        context.FillRectangle(PaperBrush, new Rect(Bounds.Size));

        InkDocument? doc = Document;
        Matrix view = Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offsetX, _offsetY);
        using (context.PushTransform(view))
        {
            if (doc is not null)
            {
                IReadOnlySet<Guid>? uncertain = UncertainStrokeIds;
                IReadOnlySet<Guid>? provenance = ProvenanceStrokeIds;

                // 4.5d: glow underlay pass first, so no highlighted stroke's glow sits on a neighbour's ink.
                if (provenance is { Count: > 0 })
                {
                    foreach (Stroke stroke in doc.Strokes)
                    {
                        if (provenance.Contains(stroke.Id))
                        {
                            DrawStroke(context, _smoothCache.GetSmoothed(stroke), GlowBrush, widthPad: 7);
                        }
                    }
                }

                // Ripple is a short dependency-order glow over source ink. It has no persistence and is
                // intentionally independent from the amber provenance selection and answer timelines.
                if (_ripple is not null)
                {
                    double elapsed = (_clock.Elapsed - _rippleStart).TotalMilliseconds;
                    for (int index = 0; index < _ripple.Steps.Count; index++)
                    {
                        double local = elapsed - RippleStepDuration.TotalMilliseconds * index;
                        if (local < 0 || local > RippleHoldDuration.TotalMilliseconds) continue;
                        IReadOnlySet<Guid> ids = _ripple.Steps[index].StrokeIds.ToHashSet();
                        foreach (Stroke stroke in doc.Strokes.Where(stroke => ids.Contains(stroke.Id)))
                        {
                            DrawStroke(context, _smoothCache.GetSmoothed(stroke), RippleBrush, widthPad: 9);
                        }
                    }
                }

                foreach (Stroke stroke in doc.Strokes)
                {
                    // Strokes are stored raw; draw the smoothed form (cached per id).
                    Stroke smoothed = _smoothCache.GetSmoothed(stroke);
                    if (uncertain is not null && uncertain.Contains(stroke.Id))
                    {
                        DrawGlitchStroke(context, smoothed);   // 4.5c: the reject, made visible in-place
                    }
                    else
                    {
                        DrawStroke(context, smoothed);
                    }
                }

                // Bound the cache: forget strokes that are no longer present (cleared/undone).
                _smoothCache.EvictMissing(doc.Strokes.Select(s => s.Id));
            }

            // The in-progress stroke is drawn raw (un-smoothed) for immediate feedback.
            if (_activeSamples is { Count: > 0 })
            {
                DrawStroke(context, new Stroke(Guid.Empty, _activeSamples));
            }

            // The answer layer: replay the timeline up to the current instant, on top of the document ink and
            // in the SAME world transform (the synthesizer anchored these strokes at the '=' world bounds).
            // Reusing DrawStroke gives the answer the identical pressure/velocity width model as real ink.
            foreach (AnswerPlayback answer in _answers.Values.OrderBy(entry => entry.Animation.Sequence))
            {
                foreach (Stroke stroke in answer.Animation.Handwriting.Timeline.SampleAt(AnswerElapsed(answer)))
                {
                    DrawStroke(context, stroke);
                }
            }
        }
    }

    private void DrawStroke(DrawingContext context, Stroke stroke) =>
        DrawStroke(context, stroke, InkBrush, widthPad: 0);

    // One body for ink, glitch-ink, and the provenance glow: same width model, different brush, and an
    // optional pad that widens every segment (the glow is the ink's own shape, inflated).
    private void DrawStroke(DrawingContext context, Stroke stroke, IImmutableSolidColorBrush brush, double widthPad)
    {
        IReadOnlyList<StrokeSample> samples = stroke.Samples;
        if (samples.Count == 0)
        {
            return;
        }

        bool usePressure = HasPressureVariation(stroke);

        if (samples.Count == 1)
        {
            // A tap leaves a dot sized like the start of a stroke.
            double radius = (_widthModel.FromPressure(samples[0].Pressure) + widthPad) / 2;
            context.DrawEllipse(brush, null, new Point(samples[0].X, samples[0].Y), radius, radius);
            return;
        }

        IReadOnlyList<double> widths = _widthModel.ComputeWidths(stroke, usePressure);
        for (int i = 1; i < samples.Count; i++)
        {
            double width = (widths[i - 1] + widths[i]) / 2 + widthPad;
            var pen = new Pen(brush, width) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            context.DrawLine(pen,
                new Point(samples[i - 1].X, samples[i - 1].Y),
                new Point(samples[i].X, samples[i].Y));
        }
    }

    // 4.5c: an uncertain stroke draws desaturated and shivering — a small time-driven offset, phased
    // per stroke id so neighbouring glitch strokes don't move in lockstep.
    private void DrawGlitchStroke(DrawingContext context, Stroke stroke)
    {
        double t = _clock.Elapsed.TotalSeconds;
        int hash = stroke.Id.GetHashCode();
        double phaseX = (hash & 0xFF) / 255.0 * Math.PI * 2;
        double phaseY = ((hash >> 8) & 0xFF) / 255.0 * Math.PI * 2;
        double dx = GlitchAmplitude * Math.Sin(t * 2 * Math.PI * 2.1 + phaseX);
        double dy = GlitchAmplitude * Math.Cos(t * 2 * Math.PI * 1.6 + phaseY);

        using (context.PushTransform(Matrix.CreateTranslation(dx, dy)))
        {
            DrawStroke(context, stroke, UncertainBrush, widthPad: 0);
        }
    }

    // A real pen reports varying pressure; a mouse reports a constant value, so we fall back to velocity.
    private static bool HasPressureVariation(Stroke stroke)
    {
        IReadOnlyList<StrokeSample> s = stroke.Samples;
        if (s.Count < 2)
        {
            return false;
        }

        double first = s[0].Pressure;
        for (int i = 1; i < s.Count; i++)
        {
            if (Math.Abs(s[i].Pressure - first) > 1e-4)
            {
                return true;
            }
        }

        return false;
    }

    // --- Input -----------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);

        // Some platforms report an inverted/eraser pen tip together with a secondary-button state.
        // Eraser identity wins there; ordinary right/middle input remains the pan gesture.
        bool penEraser = point.Properties.IsEraser || point.Properties.IsInverted;
        if (!penEraser && (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed))
        {
            _isPanning = true;
            _lastPanX = point.Position.X;
            _lastPanY = point.Position.Y;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        bool erase = IsEraseMode || penEraser;
        if (point.Properties.IsLeftButtonPressed || erase)
        {
            _strokeStart = _clock.Elapsed;
            if (erase)
            {
                _eraseHits = new HashSet<Guid>();
                AddEraseHit(point);
            }
            else
            {
                _activeSamples = new List<StrokeSample>();
                AddSample(point);
            }
            e.Pointer.Capture(this);
            e.Handled = true;

            // 4.5b: any pending/in-flight live read no longer describes the page being drawn on.
            DrawingStarted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        PointerPoint point = e.GetCurrentPoint(this);

        if (_isPanning)
        {
            _offsetX += point.Position.X - _lastPanX;
            _offsetY += point.Position.Y - _lastPanY;
            _lastPanX = point.Position.X;
            _lastPanY = point.Position.Y;
            InvalidateVisual();
            return;
        }

        if (_eraseHits is not null)
        {
            foreach (PointerPoint intermediate in e.GetIntermediatePoints(this)) AddEraseHit(intermediate);
            AddEraseHit(point);
            InvalidateVisual();
        }
        else if (_activeSamples is not null)
        {
            // Intermediate points recover the high-frequency samples coalesced into one move event.
            foreach (PointerPoint intermediate in e.GetIntermediatePoints(this))
            {
                AddSample(intermediate);
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            return;
        }

        if (_eraseHits is not null)
        {
            HashSet<Guid> hits = _eraseHits;
            _eraseHits = null;
            e.Pointer.Capture(null);
            // One pointer gesture is one document edit and therefore one undo step. Hit testing only ever
            // receives display-smoothed document strokes; synthesized answer ink is not addressable here.
            Document?.EraseStrokes(hits);
            InvalidateVisual();
        }
        else if (_activeSamples is not null)
        {
            List<StrokeSample> samples = _activeSamples;
            _activeSamples = null;
            e.Pointer.Capture(null);

            if (samples.Count > 0)
            {
                // 4.5d: a tap landing ON the played answer is a provenance query, not ink — consume it.
                // A tap anywhere else (e.g. a decimal point) still draws normally.
                if (AnswerOwnerAtTap(samples) is Guid ownerId)
                {
                    AnswerTapped?.Invoke(this, new AnswerTappedEventArgs(ownerId));
                }
                else
                {
                    // Store the raw stroke. The recognizer and glyph bank consume Document.Strokes, so they
                    // get the pen data as captured; the canvas smooths only for display (see _smoothCache).
                    Document?.AddStroke(new Stroke(Guid.NewGuid(), samples));
                }
            }

            InvalidateVisual();
        }
    }

    // A tap: every sample stays within the slop of the first (screen-space, so zoom-invariant), and the
    // touch point lies on the answer's ink within tolerance. Only meaningful while an answer is shown.
    private Guid? AnswerOwnerAtTap(IReadOnlyList<StrokeSample> samples)
    {
        if (_answers.Count == 0 || samples.Count == 0)
        {
            return null;
        }

        double slopWorld = TapSlopScreenPx / _scale;
        StrokeSample first = samples[0];
        for (int i = 1; i < samples.Count; i++)
        {
            double dx = samples[i].X - first.X;
            double dy = samples[i].Y - first.Y;
            if (dx * dx + dy * dy > slopWorld * slopWorld)
            {
                return null;
            }
        }

        // Latest sequence is visually topmost and therefore owns an overlap tap.
        return _answers.Values
            .OrderByDescending(answer => answer.Animation.Sequence)
            .FirstOrDefault(answer => AnswerHitTester.HitTest(
                answer.Animation.Handwriting.Strokes,
                first.X,
                first.Y,
                TapToleranceScreenPx / _scale))
            ?.Animation.OwnerId;
    }

    // Pixels of pan per unit of wheel/scroll delta. Tuned so one mouse notch nudges the canvas a
    // comfortable amount; precision trackpads send many small deltas and end up smooth.
    private const double PanPerWheelUnit = 60;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Several gestures all arrive as "wheel" events; route them by how they're delivered:
        //  - trackpad two-finger scroll -> pan. Precision touchpads send high-resolution fractional
        //    deltas and/or a horizontal component, which a mouse wheel never does.
        //  - mouse wheel and Ctrl+wheel (the latter is how Windows delivers a trackpad pinch) -> zoom.
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool trackpadScroll = !ctrl && (e.Delta.X != 0 || e.Delta.Y != Math.Truncate(e.Delta.Y));

        if (trackpadScroll)
        {
            _offsetX += e.Delta.X * PanPerWheelUnit;
            _offsetY += e.Delta.Y * PanPerWheelUnit;
        }
        else
        {
            Point screen = e.GetCurrentPoint(this).Position;
            double worldX = (screen.X - _offsetX) / _scale;
            double worldY = (screen.Y - _offsetY) / _scale;

            double factor = e.Delta.Y >= 0 ? 1.1 : 1 / 1.1;
            _scale = Math.Clamp(_scale * factor, 0.1, 20);

            // Keep the world point under the cursor pinned in place as we zoom.
            _offsetX = screen.X - worldX * _scale;
            _offsetY = screen.Y - worldY * _scale;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private void AddSample(PointerPoint point)
    {
        double worldX = (point.Position.X - _offsetX) / _scale;
        double worldY = (point.Position.Y - _offsetY) / _scale;
        TimeSpan time = _clock.Elapsed - _strokeStart;
        _activeSamples!.Add(new StrokeSample(worldX, worldY, time, point.Properties.Pressure));
    }

    private void AddEraseHit(PointerPoint point)
    {
        if (Document is not { } document || _eraseHits is null) return;
        double worldX = (point.Position.X - _offsetX) / _scale;
        double worldY = (point.Position.Y - _offsetY) / _scale;
        Stroke[] displayed = document.Strokes.Select(_smoothCache.GetSmoothed).ToArray();
        _eraseHits.UnionWith(StrokeHitTester.HitTest(
            displayed,
            worldX,
            worldY,
            EraserToleranceScreenPx / _scale));
    }

    private sealed record AnswerPlayback(AnswerAnimation Animation, TimeSpan Start);
}
