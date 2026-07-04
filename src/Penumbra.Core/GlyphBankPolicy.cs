namespace Penumbra.Core;

/// <summary>
/// The single source of truth for which symbols the glyph bank may SERVE for handwriting synthesis, shared
/// by both sides so they can never drift: capture never banks ink outside this set, and the synthesis read
/// path never samples a symbol outside it (the bank file may still hold such ink as raw corpus per ADR-0006 —
/// it just never reaches an answer). Letters are deliberately ABSENT: they are served uniformly by the Caveat
/// font until letter capture is trustworthy — the x↔× confusion makes banked "x" ink especially suspect.
/// Ordinal-compared because these are exact LaTeX/label tokens.
/// </summary>
public static class GlyphBankPolicy
{
    /// <summary>Symbols the bank is allowed to serve for synthesis (digits, operators, brackets, '=').</summary>
    public static readonly IReadOnlySet<string> BankableSymbols = new HashSet<string>(StringComparer.Ordinal)
    {
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "+", "-", ".", "/", "(", ")", "=", "\\times", "\\div", "\\pi",
    };

    /// <summary>True when <paramref name="symbol"/> is synthesis-trusted (in <see cref="BankableSymbols"/>).</summary>
    public static bool IsBankable(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return BankableSymbols.Contains(symbol);
    }
}
