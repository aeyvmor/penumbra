using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// The cross-language witness for the consolidated-retrain contract (audit S1/A3): the fixture v2
/// carries RAW STROKES plus every intermediate the Python exporter computed, and this test runs the
/// FULL C# chain — <see cref="SymbolPreprocessor.RenderImage"/> + <see cref="SymbolPreprocessor.ComputeFeatures"/>
/// + ONNX Runtime + <see cref="RecognitionCalibration.Apply"/> — from those strokes. "Parity OK" here
/// means preprocessing parity, not just engine parity. The fixture is regenerated atomically by
/// <c>ml/export/export_crohme_geo_onnx.py</c>; it cannot exist without its generator.
/// </summary>
public sealed class OnnxSymbolClassifierTests
{
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "Models");

    [Fact]
    public void ParityFixture_ContainsOnlyProvenanceSafeSyntheticGeometry()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity_fixture.json"));
        using JsonDocument fixture = JsonDocument.Parse(json);
        JsonElement root = fixture.RootElement;

        Assert.StartsWith("synthetic parametric geometry", root.GetProperty("provenance").GetString());
        Assert.All(root.GetProperty("samples").EnumerateArray(), sample =>
        {
            string label = sample.GetProperty("label").GetString()!;
            Assert.True(
                label.StartsWith("synthetic_", StringComparison.Ordinal)
                || label.StartsWith("__degenerate_", StringComparison.Ordinal)
                || label.StartsWith("__junk_synthetic_", StringComparison.Ordinal),
                $"fixture sample '{label}' has no synthetic provenance marker");
        });
    }

    [Fact]
    public void FullChain_StrokesToCalibratedScores_MatchesPythonFixture()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity_fixture.json"));
        using JsonDocument fixture = JsonDocument.Parse(json);
        JsonElement root = fixture.RootElement;
        Assert.Equal(2, root.GetProperty("version").GetInt32());

        string[] classes = root.GetProperty("classes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        float mean = (float)root.GetProperty("mean").GetDouble();
        float std = (float)root.GetProperty("std").GetDouble();
        float[] featMean = root.GetProperty("feat_mean").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        float[] featStd = root.GetProperty("feat_std").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var calibration = new RecognitionCalibration(
            root.GetProperty("temperature").GetDouble(),
            root.GetProperty("reject_energy_threshold").GetDouble(),
            root.GetProperty("min_confidence").GetDouble(),
            root.GetProperty("bank_confidence").GetDouble());

        // Contract consistency: fixture classes == meta.json classes == the model's output width
        // (asserted per-sample against the score vector below).
        using JsonDocument meta = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ModelDir, "crohme_geo_cnn.meta.json")));
        string[] metaClasses = meta.RootElement.GetProperty("classes")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(metaClasses, classes);

        using var session = new InferenceSession(Path.Combine(ModelDir, "crohme_geo_cnn.onnx"));

        int checkedSamples = 0;
        var coveredOutputs = new HashSet<int>();
        foreach (JsonElement sample in root.GetProperty("samples").EnumerateArray())
        {
            string label = sample.GetProperty("label").GetString()!;
            var strokes = sample.GetProperty("strokes").EnumerateArray()
                .Select(s => new Stroke(Guid.NewGuid(), s.EnumerateArray()
                    .Select(p => new StrokeSample(p[0].GetDouble(), p[1].GetDouble(), TimeSpan.Zero, 0.5))
                    .ToList()))
                .ToList();
            JsonElement ctx = sample.GetProperty("context");
            var context = new SymbolContext(
                ctx.GetProperty("ref_h").GetDouble(),
                ctx.GetProperty("expr_ymin").GetDouble(),
                ctx.GetProperty("expr_h").GetDouble());

            // 1) bitmap: C# renders + standardizes; the fixture ships RAW coverage, standardized here.
            float[] image = SymbolPreprocessor.RenderImage(strokes, mean, std);
            double[] rawBitmap = sample.GetProperty("bitmap").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            Assert.Equal(rawBitmap.Length, image.Length);
            for (int i = 0; i < image.Length; i++)
            {
                double expected = (rawBitmap[i] - mean) / std;
                Assert.True(Math.Abs(image[i] - expected) < 1e-4,
                    $"{label}: bitmap[{i}] C#={image[i]} Python={expected}");
            }

            // 2) features (standardized).
            float[] features = SymbolPreprocessor.ComputeFeatures(strokes, context, featMean, featStd);
            double[] expectedFeatures = sample.GetProperty("features_std").EnumerateArray()
                .Select(e => e.GetDouble()).ToArray();
            for (int i = 0; i < features.Length; i++)
            {
                Assert.True(Math.Abs(features[i] - expectedFeatures[i]) < 1e-4,
                    $"{label}: feature[{i}] C#={features[i]} Python={expectedFeatures[i]}");
            }

            // 3) logits through ORT on the C#-preprocessed tensors.
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(image, new[] { 1, 1, 32, 32 })),
                NamedOnnxValue.CreateFromTensor("features", new DenseTensor<float>(features, new[] { 1, features.Length })),
            };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);
            float[] scores = results.First().AsEnumerable<float>().ToArray();

            double[] expectedScores = sample.GetProperty("scores").EnumerateArray()
                .Select(e => e.GetDouble()).ToArray();
            Assert.Equal(classes.Length, scores.Length);
            Assert.Equal(expectedScores.Length, scores.Length);
            for (int i = 0; i < scores.Length; i++)
            {
                Assert.True(Math.Abs(scores[i] - expectedScores[i]) < 1e-3,
                    $"{label}: score[{i}] C#={scores[i]} Python={expectedScores[i]}");
            }

            // 4) argmax — only asserted when the fixture's top-2 margin exceeds the score tolerance,
            // else a genuine near-tie (degenerate/junk ink) could flip on 1e-4 noise and mean nothing.
            int expectedIndex = sample.GetProperty("expected_index").GetInt32();
            coveredOutputs.Add(expectedIndex);
            double margin = expectedScores.Max()
                - expectedScores.Where((_, i) => i != Array.IndexOf(expectedScores, expectedScores.Max())).Max();
            if (margin > 1e-2)
            {
                Assert.Equal(expectedIndex, Array.IndexOf(scores, scores.Max()));
            }

            // 5) calibration: confidence + energy through the same code path production uses.
            (_, double confidence, double energy, _) = calibration.Apply(scores);
            Assert.True(Math.Abs(confidence - sample.GetProperty("confidence").GetDouble()) < 1e-3,
                $"{label}: calibrated confidence C#={confidence} Python={sample.GetProperty("confidence").GetDouble()}");
            Assert.True(Math.Abs(energy - sample.GetProperty("energy").GetDouble()) < 1e-3,
                $"{label}: energy C#={energy} Python={sample.GetProperty("energy").GetDouble()}");

            checkedSamples++;
        }

        Assert.True(checkedSamples >= 150, $"fixture unexpectedly small: {checkedSamples} samples");
        Assert.Equal(classes.Length, coveredOutputs.Count);
    }

    [Fact]
    public void FixtureAndMetaCalibration_AgreeWithLoadedClassifier()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity_fixture.json"));
        using JsonDocument fixture = JsonDocument.Parse(json);
        using var classifier = new OnnxSymbolClassifier(ModelDir);

        Assert.Equal(
            new RecognitionCalibration(
                fixture.RootElement.GetProperty("temperature").GetDouble(),
                fixture.RootElement.GetProperty("reject_energy_threshold").GetDouble(),
                fixture.RootElement.GetProperty("min_confidence").GetDouble(),
                fixture.RootElement.GetProperty("bank_confidence").GetDouble()),
            classifier.Calibration);
    }

    [Fact]
    public void Classifies_An_Isolated_Plus()
    {
        using var classifier = new OnnxSymbolClassifier(ModelDir);

        Stroke horizontal = Stroke(new[] { (10.0, 20.0), (15, 20), (20, 20), (25, 20), (30, 20) });
        Stroke vertical = Stroke(new[] { (20.0, 10.0), (20, 15), (20, 20), (20, 25), (20, 30) });
        var strokes = new[] { horizontal, vertical };

        SymbolPrediction prediction = classifier.Classify(strokes, SymbolContext.ForSelf(strokes));

        Assert.Equal("+", prediction.Label);
        Assert.True(prediction.Confidence > 0.5, $"confidence was {prediction.Confidence}");
    }

    /// <summary>
    /// Phase 5.5 slice 4 (Section A) regression: <see cref="SymbolPrediction.Alternatives"/> is filled from
    /// an INDEPENDENT <see cref="RecognitionCalibration.RankedConfidences"/> call, never touching the
    /// <see cref="RecognitionCalibration.Apply"/> code path that decides Label/Confidence/Energy/Rejected.
    /// This proves it end-to-end on the same parity fixture <see cref="FullChain_StrokesToCalibratedScores_MatchesPythonFixture"/>
    /// already validates: top-1 must still match the Python-computed confidence/energy exactly, and the
    /// alternatives list's own winner must agree with the top-1 field precisely (not just approximately).
    /// </summary>
    [Fact]
    public void Alternatives_DoNotChangeTop1_AndTopEntryMatchesPrediction()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity_fixture.json"));
        using JsonDocument fixture = JsonDocument.Parse(json);
        JsonElement root = fixture.RootElement;
        using var classifier = new OnnxSymbolClassifier(ModelDir);

        int checkedSamples = 0;
        foreach (JsonElement sample in root.GetProperty("samples").EnumerateArray())
        {
            string label = sample.GetProperty("label").GetString()!;
            var strokes = sample.GetProperty("strokes").EnumerateArray()
                .Select(s => new Stroke(Guid.NewGuid(), s.EnumerateArray()
                    .Select(p => new StrokeSample(p[0].GetDouble(), p[1].GetDouble(), TimeSpan.Zero, 0.5))
                    .ToList()))
                .ToList();
            JsonElement ctx = sample.GetProperty("context");
            var context = new SymbolContext(
                ctx.GetProperty("ref_h").GetDouble(),
                ctx.GetProperty("expr_ymin").GetDouble(),
                ctx.GetProperty("expr_h").GetDouble());

            double expectedConfidence = sample.GetProperty("confidence").GetDouble();
            double expectedEnergy = sample.GetProperty("energy").GetDouble();

            SymbolPrediction prediction = classifier.Classify(strokes, context);

            Assert.True(Math.Abs(prediction.Confidence - expectedConfidence) < 1e-3,
                $"{label}: confidence C#={prediction.Confidence} Python={expectedConfidence}");
            Assert.True(Math.Abs(prediction.Energy - expectedEnergy) < 1e-3,
                $"{label}: energy C#={prediction.Energy} Python={expectedEnergy}");

            Assert.NotNull(prediction.Alternatives);
            Assert.NotEmpty(prediction.Alternatives!);
            Assert.True(prediction.Alternatives!.Count <= 5);
            Assert.Equal(prediction.Label, prediction.Alternatives[0].Label);
            Assert.Equal(prediction.Confidence, prediction.Alternatives[0].Confidence, 9);

            for (int i = 1; i < prediction.Alternatives.Count; i++)
            {
                Assert.True(
                    prediction.Alternatives[i - 1].Confidence >= prediction.Alternatives[i].Confidence,
                    $"{label}: alternatives not ranked descending at index {i}");
            }

            checkedSamples++;
        }

        Assert.True(checkedSamples >= 150, $"fixture unexpectedly small: {checkedSamples} samples");
    }

    /// <summary>
    /// Test fakes across the codebase construct <c>SymbolPrediction</c> without ever setting
    /// <c>Alternatives</c>. Confirms the field defaults null so every pre-existing construction site keeps
    /// compiling and behaving unchanged.
    /// </summary>
    [Fact]
    public void SymbolPrediction_Alternatives_DefaultsToNull()
    {
        var prediction = new SymbolPrediction("x", 0.9);

        Assert.Null(prediction.Alternatives);
    }

    [Fact]
    public void Empty_Strokes_Classify_To_Empty()
    {
        using var classifier = new OnnxSymbolClassifier(ModelDir);

        SymbolPrediction prediction = classifier.Classify(Array.Empty<Stroke>(), default);

        Assert.Equal(string.Empty, prediction.Label);
    }

    private static Stroke Stroke(IEnumerable<(double X, double Y)> points) =>
        new(Guid.NewGuid(), points.Select(p => new StrokeSample(p.X, p.Y, TimeSpan.Zero, 0.5)).ToList());
}
