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
/// Left button draws; right/middle button pans; the wheel zooms about the cursor.
/// </summary>
public sealed class InkCanvasControl : Control
{
    public static readonly StyledProperty<InkDocument?> DocumentProperty =
        AvaloniaProperty.Register<InkCanvasControl, InkDocument?>(nameof(Document));

    public static readonly StyledProperty<AnswerAnimation?> AnswerAnimationProperty =
        AvaloniaProperty.Register<InkCanvasControl, AnswerAnimation?>(nameof(AnswerAnimation));

    // Wall-clock → animation-time multiplier. Captured pen pace can feel sluggish; bump this to speed up the
    // "write-itself" playback. One named knob so tuning later is a single-line change.
    private const double PlaybackSpeed = 1.9;

    // Repaint cadence while an answer animates (~60 fps).
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private static readonly IImmutableSolidColorBrush PaperBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xFB));
    private static readonly IImmutableSolidColorBrush InkBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x18, 0x20, 0x2A));

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StrokeWidthModel _widthModel = new();

    // The currently-playing answer layer (rendered on top of the document ink, never folded into it), the
    // wall-clock instant playback began, and the ticking repaint timer (null when idle or finished).
    private SynthesizedHandwriting? _answer;
    private TimeSpan _answerStart;
    private DispatcherTimer? _answerTimer;

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

    /// <summary>The answer layer to play on top of the ink, or null for none. Set by the view-model on Recognize.</summary>
    public AnswerAnimation? AnswerAnimation
    {
        get => GetValue(AnswerAnimationProperty);
        set => SetValue(AnswerAnimationProperty, value);
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
        else if (change.Property == AnswerAnimationProperty)
        {
            OnAnswerAnimationChanged(change.NewValue as AnswerAnimation);
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => InvalidateVisual();

    // --- Answer animation ------------------------------------------------------------------------

    private void OnAnswerAnimationChanged(AnswerAnimation? animation)
    {
        // Always stop the previous run first so replacing an animation never leaks a running timer.
        StopAnswerTimer();

        _answer = animation?.Handwriting;
        if (_answer is null)
        {
            InvalidateVisual();
            return;
        }

        // Anchor playback to "now"; Render maps elapsed wall-clock (× speed) onto the timeline.
        _answerStart = _clock.Elapsed;
        _answerTimer = new DispatcherTimer { Interval = FrameInterval };
        _answerTimer.Tick += OnAnswerTick;
        _answerTimer.Start();
        InvalidateVisual();
    }

    private void OnAnswerTick(object? sender, EventArgs e)
    {
        InvalidateVisual();

        // Once the whole timeline has played, stop ticking but KEEP _answer so Render holds the final frame
        // (SampleAt clamps past TotalDuration), leaving the finished answer on the page.
        if (_answer is null || AnswerElapsed() >= _answer.Timeline.TotalDuration)
        {
            StopAnswerTimer();
        }
    }

    private TimeSpan AnswerElapsed() => (_clock.Elapsed - _answerStart) * PlaybackSpeed;

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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopAnswerTimer();   // don't leave a timer running against a detached control.
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
                foreach (Stroke stroke in doc.Strokes)
                {
                    // Strokes are stored raw; draw the smoothed form (cached per id).
                    DrawStroke(context, _smoothCache.GetSmoothed(stroke));
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
            if (_answer is not null)
            {
                foreach (Stroke stroke in _answer.Timeline.SampleAt(AnswerElapsed()))
                {
                    DrawStroke(context, stroke);
                }
            }
        }
    }

    private void DrawStroke(DrawingContext context, Stroke stroke)
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
            double radius = _widthModel.FromPressure(samples[0].Pressure) / 2;
            context.DrawEllipse(InkBrush, null, new Point(samples[0].X, samples[0].Y), radius, radius);
            return;
        }

        IReadOnlyList<double> widths = _widthModel.ComputeWidths(stroke, usePressure);
        for (int i = 1; i < samples.Count; i++)
        {
            double width = (widths[i - 1] + widths[i]) / 2;
            var pen = new Pen(InkBrush, width) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            context.DrawLine(pen,
                new Point(samples[i - 1].X, samples[i - 1].Y),
                new Point(samples[i].X, samples[i].Y));
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

        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPanX = point.Position.X;
            _lastPanY = point.Position.Y;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _activeSamples = new List<StrokeSample>();
            _strokeStart = _clock.Elapsed;
            AddSample(point);
            e.Pointer.Capture(this);
            e.Handled = true;
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

        if (_activeSamples is not null)
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

        if (_activeSamples is not null)
        {
            if (_activeSamples.Count > 0)
            {
                // Store the raw stroke. The recognizer and glyph bank consume Document.Strokes, so they
                // get the pen data as captured; the canvas smooths only for display (see _smoothCache).
                Document?.AddStroke(new Stroke(Guid.NewGuid(), _activeSamples));
            }

            _activeSamples = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
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
}
