namespace Penumbra.Recognition;

/// <summary>
/// The <em>decision</em> half of the model contract (audit B4): temperature scaling of the logits plus
/// energy-score out-of-distribution rejection, and the two confidence bars the app gates and banks on.
/// A calibrated retrain ships these in <c>meta.json</c> (<c>temperature</c>,
/// <c>reject_energy_threshold</c>, <c>min_confidence</c>, <c>bank_confidence</c>); when a field is absent
/// — every artifact predating this contract — <see cref="Default"/> keeps the pre-calibration behaviour
/// byte-for-byte: <c>T=1</c> is the identity softmax, an infinite reject threshold never rejects, and the
/// bars fall back to the constants the app hardcoded before calibration.
/// </summary>
/// <param name="Temperature">Logit scale <c>T</c> for the calibrated softmax (<c>softmax(logits / T)</c>).</param>
/// <param name="RejectEnergyThreshold">A symbol is rejected (OOD) when its energy exceeds this.</param>
/// <param name="MinConfidence">Reject-gate bar: reads whose weakest symbol falls below this are refused.</param>
/// <param name="BankConfidence">Stricter bar a glyph must clear to enter the handwriting bank.</param>
public sealed record RecognitionCalibration(
    double Temperature,
    double RejectEnergyThreshold,
    double MinConfidence,
    double BankConfidence)
{
    /// <summary>
    /// The pre-calibration reject bar (today's <c>0.55</c>). Canonical home for the constant now that the
    /// app reads the bar from calibration — the App layer no longer owns it.
    /// </summary>
    public const double DefaultMinConfidence = 0.55;

    /// <summary>
    /// Backward-compatible defaults for artifacts whose <c>meta.json</c> predates the B4 fields. Chosen so
    /// an old model behaves exactly as it did before this contract existed.
    /// </summary>
    public static RecognitionCalibration Default { get; } = new(
        Temperature: 1.0,
        RejectEnergyThreshold: double.PositiveInfinity,
        MinConfidence: DefaultMinConfidence,
        BankConfidence: GlyphCapture.BankThreshold);

    /// <summary>
    /// Applies temperature scaling + energy scoring to one symbol's raw logits. Returns the argmax class,
    /// its calibrated softmax confidence (<c>softmax(logits / T)</c> max), the free energy
    /// <c>−T·logsumexp(logits / T)</c>, and whether that energy crosses <see cref="RejectEnergyThreshold"/>
    /// (an OOD reject). At <c>T = 1</c> the confidence is bit-identical to the old plain softmax, so old
    /// artifacts are unaffected. The single (<see cref="OnnxSymbolClassifier.Classify"/>) and batch
    /// (<see cref="OnnxSymbolClassifier.ClassifyBatch"/>) paths both route through here, which is what keeps
    /// them scoring identically.
    /// </summary>
    public (int Index, double Confidence, double Energy, bool Rejected) Apply(IReadOnlyList<float> logits)
    {
        ArgumentNullException.ThrowIfNull(logits);
        if (logits.Count == 0)
        {
            return (0, 0, double.PositiveInfinity, RejectEnergyThreshold < double.PositiveInfinity);
        }

        double t = Temperature > 0 ? Temperature : 1.0;   // guard: meta ships T>0; a bad value can't divide-by-zero

        int best = 0;
        for (int i = 1; i < logits.Count; i++)
        {
            if (logits[i] > logits[best])
            {
                best = i;
            }
        }

        // logsumexp(logits / T), max-shifted for stability. T>0 preserves the argmax, so `best` is also
        // the max of the scaled logits. The shift is done in FLOAT space deliberately: at T=1 the scaled
        // logits are exactly the raw float logits, so `zi - zBest` reproduces the pre-calibration
        // Softmax's float subtraction bit-for-bit — old artifacts score byte-identically. For fitted T
        // the float rounding (~1e-7) is far below the contract's 1e-3 score tolerance.
        float zBest = (float)(logits[best] / t);
        double sumExp = 0;
        for (int i = 0; i < logits.Count; i++)
        {
            sumExp += Math.Exp((float)(logits[i] / t) - zBest);
        }

        double logSumExp = zBest + Math.Log(sumExp);
        double energy = -t * logSumExp;

        // exp(zBest - zBest) is exactly 1, so this is (float)(1 / sumExp) — the exact expression the old
        // Softmax evaluated at the argmax.
        double confidence = (float)(1.0 / sumExp);
        bool rejected = energy > RejectEnergyThreshold;

        return (best, confidence, energy, rejected);
    }
}
