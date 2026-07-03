namespace Penumbra.Ink;

/// <summary>
/// Every tunable for <see cref="HandwritingSynthesizer"/> in one place — no magic numbers in the code.
/// Spacing/anchor knobs are expressed as fractions of <see cref="LineHeight"/> so a single size change
/// scales the whole layout. Defaults are the M2 starting point; expect to tune from dogfooding.
/// </summary>
public sealed record SynthesisOptions
{
    /// <summary>Target cap height for each glyph, in world units (the em the layout fractions scale off).</summary>
    public double LineHeight { get; init; } = 48.0;

    /// <summary>Gap after each glyph before the next, as a fraction of line height.</summary>
    public double LetterSpacing { get; init; } = 0.18;

    /// <summary>Gap between the anchor (the '=') and the first glyph's left edge, as a fraction of line height.</summary>
    public double GapAfterAnchor { get; init; } = 0.6;

    /// <summary>Pen advance for a literal space character, as a fraction of line height (half an em).</summary>
    public double SpaceAdvance { get; init; } = 0.5;

    /// <summary>Nominal writing speed in world units per second, used to synthesize times and air-move gaps.</summary>
    public double PenSpeed { get; init; } = 350.0;

    /// <summary>Uniform-scale jitter magnitude: each glyph scales by 1 ± this fraction (±5% default).</summary>
    public double ScaleJitter { get; init; } = 0.05;

    /// <summary>Rotation jitter magnitude in degrees: each glyph rotates by ± this (±3° default).</summary>
    public double RotationJitterDegrees { get; init; } = 3.0;

    /// <summary>Translation jitter magnitude, as a fraction of line height, applied per axis (±2% default).</summary>
    public double TranslationJitter { get; init; } = 0.02;

    /// <summary>Lower clamp on a pen-up air-move gap between consecutive strokes.</summary>
    public TimeSpan MinAirMove { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>Upper clamp on a pen-up air-move gap between consecutive strokes.</summary>
    public TimeSpan MaxAirMove { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Display-char → glyph-label overrides for chars whose bank/LaTeX label differs from the char itself
    /// (e.g. '×' → "\times"). Any char not listed maps to its own string form. Override to extend.
    /// </summary>
    public IReadOnlyDictionary<char, string> SymbolMap { get; init; } = DefaultSymbolMap;

    private static readonly IReadOnlyDictionary<char, string> DefaultSymbolMap = new Dictionary<char, string>
    {
        ['×'] = "\\times",
        ['÷'] = "\\div",
    };
}
