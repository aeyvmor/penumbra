namespace Penumbra.Core;

/// <summary>A single pen-down to pen-up trace.</summary>
public sealed record Stroke(Guid Id, IReadOnlyList<StrokeSample> Samples);
