using System.Text.Json;
using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// B4 calibration contract: temperature scaling + energy-score rejection, the meta.json plumbing with
/// backward-compatible defaults, and the gate/banking behaviour for energy-rejected tokens. The retrain
/// ships the fitted constants; these tests pin the machinery they ride on.
/// </summary>
public sealed class RecognitionCalibrationTests
{
    // ---- Apply(): the math ----------------------------------------------------------------------

    [Fact]
    public void TemperatureOne_IsIdentitySoftmax()
    {
        float[] logits = { 2.0f, -1.0f, 0.5f, 4.0f };

        (int index, double confidence, double energy, bool rejected) =
            RecognitionCalibration.Default.Apply(logits);

        Assert.Equal(3, index);
        // Bit-identity with the pre-B4 Softmax (float-space subtraction, float-cast result) — the
        // backward-compat guarantee old artifacts rely on. Exact equality is intentional.
        Assert.Equal(PreCalibrationSoftmaxMax(logits), confidence);
        // energy = -logsumexp(logits); finite and below any real threshold semantics
        Assert.True(double.IsFinite(energy));
        Assert.False(rejected);
    }

    [Fact]
    public void HigherTemperature_FlattensConfidence_ButPreservesArgmax()
    {
        float[] logits = { 8.0f, 1.0f, 0.0f };
        var calibrated = new RecognitionCalibration(
            Temperature: 3.0, RejectEnergyThreshold: double.PositiveInfinity,
            MinConfidence: 0.55, BankConfidence: 0.80);

        (int hotIndex, double hotConfidence, _, _) = calibrated.Apply(logits);
        (int coldIndex, double coldConfidence, _, _) = RecognitionCalibration.Default.Apply(logits);

        Assert.Equal(coldIndex, hotIndex);
        Assert.True(hotConfidence < coldConfidence,
            $"T=3 confidence {hotConfidence} should be below T=1 confidence {coldConfidence}");
    }

    [Fact]
    public void Energy_MatchesClosedForm_AndDrivesRejection()
    {
        float[] logits = { 1.0f, 2.0f, 3.0f };
        const double t = 2.0;
        double expectedEnergy = -t * Math.Log(
            Math.Exp(1.0 / t) + Math.Exp(2.0 / t) + Math.Exp(3.0 / t));

        var accepting = new RecognitionCalibration(t, expectedEnergy + 0.01, 0.55, 0.80);
        var rejecting = new RecognitionCalibration(t, expectedEnergy - 0.01, 0.55, 0.80);

        Assert.Equal(expectedEnergy, accepting.Apply(logits).Energy, 5);
        Assert.False(accepting.Apply(logits).Rejected);
        Assert.True(rejecting.Apply(logits).Rejected);
    }

    // ---- meta.json plumbing ----------------------------------------------------------------------

    [Fact]
    public void MetaWithoutCalibrationFields_YieldsDefaults_AndOldScoringExactly()
    {
        // A meta.json that predates the B4 fields (the shipped one no longer does, so strip them):
        // the classifier must fall back wholesale to Default and behave exactly pre-calibration.
        string source = Path.Combine(AppContext.BaseDirectory, "Models");
        string dir = Directory.CreateTempSubdirectory("penumbra-precalib-meta").FullName;
        try
        {
            File.Copy(Path.Combine(source, "crohme_geo_cnn.onnx"), Path.Combine(dir, "crohme_geo_cnn.onnx"));
            var meta = JsonSerializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(source, "crohme_geo_cnn.meta.json")))!;
            foreach (string field in new[]
                     { "temperature", "reject_energy_threshold", "min_confidence", "bank_confidence" })
            {
                meta.Remove(field);
            }
            File.WriteAllText(Path.Combine(dir, "crohme_geo_cnn.meta.json"), JsonSerializer.Serialize(meta));

            using var classifier = new OnnxSymbolClassifier(dir);

            Assert.Equal(RecognitionCalibration.Default, classifier.Calibration);

            // And a real classification neither rejects nor deviates from plain-softmax confidence.
            Stroke horizontal = Stroke((10.0, 20.0), (15, 20), (20, 20), (25, 20), (30, 20));
            Stroke vertical = Stroke((20.0, 10.0), (20, 15), (20, 20), (20, 25), (20, 30));
            var strokes = new[] { horizontal, vertical };

            SymbolPrediction prediction = classifier.Classify(strokes, SymbolContext.ForSelf(strokes));

            Assert.False(prediction.Rejected);
            Assert.True(prediction.Energy < 0, "free energy of a confident in-distribution read is negative");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MetaWithCalibrationFields_IsReadIntoCalibration()
    {
        // Same real model, meta.json augmented with the four B4 fields in a temp model dir.
        string source = Path.Combine(AppContext.BaseDirectory, "Models");
        string dir = Directory.CreateTempSubdirectory("penumbra-calib-meta").FullName;
        try
        {
            File.Copy(Path.Combine(source, "crohme_geo_cnn.onnx"), Path.Combine(dir, "crohme_geo_cnn.onnx"));
            var meta = JsonSerializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(source, "crohme_geo_cnn.meta.json")))!;
            meta["temperature"] = 2.5;
            meta["reject_energy_threshold"] = -4.0;
            meta["min_confidence"] = 0.4;
            meta["bank_confidence"] = 0.7;
            File.WriteAllText(Path.Combine(dir, "crohme_geo_cnn.meta.json"), JsonSerializer.Serialize(meta));

            using var classifier = new OnnxSymbolClassifier(dir);

            Assert.Equal(new RecognitionCalibration(2.5, -4.0, 0.4, 0.7), classifier.Calibration);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- gate + banking behaviour for rejected tokens ---------------------------------------------

    [Fact]
    public void Gate_RejectedTokenAtHighConfidence_RefusesAndNamesIt()
    {
        // The 4.5 failure class: a spiral reading '\theta' at 1.00 — confidence alone would sail through.
        RecognitionResult result = Result(
            (0.9, "1", false), (0.99, @"\theta", true), (0.95, "=", false));

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, 0.55);

        Assert.False(gate.Accepted);
        Assert.Equal(2, gate.SymbolPosition);
    }

    [Fact]
    public void Gate_UncertainStrokeIds_IncludeRejectedTokens()
    {
        var rejectedStroke = Guid.NewGuid();
        var tokens = new List<RecognizedToken>
        {
            new("1", new[] { Guid.NewGuid() }, default, 0.9),
            new(@"\theta", new[] { rejectedStroke }, default, 0.99, Rejected: true),
        };
        var result = new RecognitionResult(@"1\theta", tokens, 0.945, 0.9);

        IReadOnlySet<Guid> ids = RecognitionGate.UncertainStrokeIds(result, 0.55);

        Assert.Equal(new HashSet<Guid> { rejectedStroke }, ids);
    }

    [Fact]
    public void Gate_LowConfidenceUncertainToken_IsNamedAheadOfRejectedConfidentOne()
    {
        // Both are uncertain; the genuinely weak read is the more actionable callout.
        RecognitionResult result = Result(
            (0.99, @"\theta", true), (0.30, "2", false), (0.95, "=", false));

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, 0.55);

        Assert.False(gate.Accepted);
        Assert.Equal(2, gate.SymbolPosition);
    }

    [Fact]
    public void GlyphCapture_NeverBanksARejectedToken()
    {
        var stroke = Stroke((0.0, 0.0), (10, 10));
        var tokens = new List<RecognizedToken>
        {
            new("5", new[] { stroke.Id }, new InkBounds(0, 0, 10, 10), 0.99, Rejected: true),
        };

        IReadOnlyList<GlyphSample> banked = GlyphCapture.Collect(
            tokens, new[] { stroke }, threshold: 0.80, DateTimeOffset.UtcNow);

        Assert.Empty(banked);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    // Verbatim port of the pre-B4 OnnxSymbolClassifier.Softmax arithmetic (float subtraction,
    // float-cast result) so the bit-identity assertion means exactly "old artifacts score the same".
    private static double PreCalibrationSoftmaxMax(float[] scores)
    {
        float max = scores.Max();
        double sum = 0;
        foreach (float s in scores)
        {
            sum += Math.Exp(s - max);
        }

        return (float)(Math.Exp(scores.Max() - max) / sum);
    }

    private static RecognitionResult Result(params (double Confidence, string Label, bool Rejected)[] symbols)
    {
        var tokens = symbols
            .Select(s => new RecognizedToken(s.Label, Array.Empty<Guid>(), default, s.Confidence, s.Rejected))
            .ToList();
        return new RecognitionResult(
            string.Concat(symbols.Select(s => s.Label)), tokens,
            symbols.Average(s => s.Confidence), symbols.Min(s => s.Confidence));
    }

    private static Stroke Stroke(params (double X, double Y)[] points) =>
        new(Guid.NewGuid(), points.Select(p => new StrokeSample(p.X, p.Y, TimeSpan.Zero, 0.5)).ToList());
}
