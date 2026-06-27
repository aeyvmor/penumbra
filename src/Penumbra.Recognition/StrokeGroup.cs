using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>A set of strokes the segmenter judged to form one symbol, with their combined bounds.</summary>
public sealed record StrokeGroup(IReadOnlyList<Stroke> Strokes, InkBounds Bounds);
