using System.Text;

namespace Penumbra.Ink;

/// <summary>
/// Translates a CAS DisplayText surface (AngouriMath's Stringize form) into the text the handwriting
/// synthesizer should actually draw. Today it only rewrites multiplication: an explicit '*' becomes
/// juxtaposition ("4 * y" → "4y") when a letter sits on either side (the conventional form next to a
/// variable), otherwise a proper times sign ("3 * 4" → "3×4", which the synthesizer maps to \times) so two
/// digits never silently merge into a misleading number. The '*' absorbs the whitespace padding it; all
/// OTHER whitespace (e.g. the word gaps in "x = 2 or x = -2") is preserved exactly.
/// Known M2 limit: no other rewriting — powers ("x ^ 2") and roots ("sqrt(2)") are written literally.
/// </summary>
public static class HandwritingText
{
    /// <summary>Rewrites display multiplication into drawable text; see the type summary for the rule.</summary>
    public static string FromDisplayText(string displayText)
    {
        ArgumentNullException.ThrowIfNull(displayText);
        if (displayText.IndexOf('*') < 0)
        {
            return displayText;   // nothing to rewrite — preserve the surface verbatim
        }

        var sb = new StringBuilder(displayText.Length);
        int i = 0;
        while (i < displayText.Length)
        {
            char c = displayText[i];
            if (c != '*')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Nearest non-space already emitted (before) and nearest non-space ahead (after), absorbing the
            // whitespace that pads the '*'. Juxtaposition is only conventional beside a variable, so it applies
            // iff a LETTER is adjacent; between two digits we keep an explicit '×' instead.
            int end = sb.Length;
            while (end > 0 && char.IsWhiteSpace(sb[end - 1]))
            {
                end--;
            }

            char before = end > 0 ? sb[end - 1] : '\0';

            int j = i + 1;
            while (j < displayText.Length && char.IsWhiteSpace(displayText[j]))
            {
                j++;
            }

            char after = j < displayText.Length ? displayText[j] : '\0';

            sb.Length = end;   // drop the whitespace padding the '*'
            if (!char.IsLetter(before) && !char.IsLetter(after))
            {
                sb.Append('×');
            }

            i = j;   // the 'after' char is appended by the next iteration
        }

        return sb.ToString();
    }
}
