using System.Globalization;

namespace Penumbra.Recognition;

/// <summary>
/// Maps a horizontal taffy-drag distance to a snapped trial value string. Pure and headless: the
/// App layer feeds it the grabbed literal's text and the drag's screen-space Δx; this owns only the
/// "how far is how much" arithmetic, so the feel (step distance, decimal stepping) is unit-testable
/// without a pointer.
/// </summary>
public static class TaffyValueMapper
{
    /// <summary>
    /// Returns <paramref name="originalValueText"/> stepped by the number of whole
    /// <paramref name="pixelsPerStep"/> units in <paramref name="screenDx"/>. Steps round toward
    /// zero — a value change requires a FULL step's travel in either direction, so a jittery press
    /// never flickers the value. Integer originals step by 1; an original containing <c>'.'</c>
    /// with k fractional digits steps by 10^-k (the last written place) and the result keeps exactly
    /// k fractional digits, invariant culture. The output is normalized — leading zeros drop
    /// (<c>"007"</c> steps to <c>"8"</c>, never <c>"008"</c>) and zero never carries a sign. It MAY
    /// be negative; parenthesizing a negative for the LaTeX line is
    /// <see cref="LiteralRuns.Splice"/>'s job, not the mapper's.
    /// </summary>
    public static string Map(string originalValueText, double screenDx, double pixelsPerStep = 14.0)
    {
        ArgumentNullException.ThrowIfNull(originalValueText);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelsPerStep);

        int steps = (int)(screenDx / pixelsPerStep);

        int dot = originalValueText.IndexOf('.');
        int fractionalDigits = dot < 0 ? 0 : originalValueText.Length - dot - 1;

        decimal value = decimal.Parse(originalValueText, CultureInfo.InvariantCulture);

        // 10^-k built directly as a decimal scale, so decimal steps never pick up binary-float noise
        // ("2.50" + 0.01 must be exactly 2.51, not 2.5100000000000002).
        decimal stepSize = new(1, 0, 0, false, checked((byte)fractionalDigits));
        decimal result = value + steps * stepSize;

        // Reassigning a numeric zero strips any sign representation: "-0" must never leak out.
        if (result == 0m)
        {
            result = 0m;
        }

        return result.ToString("F" + fractionalDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
