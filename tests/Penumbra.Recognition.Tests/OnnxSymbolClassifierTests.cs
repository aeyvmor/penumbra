using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Step-4 de-risking, retained through the 5c split: prove the C# ONNX path matches Python, and the
/// per-symbol classifier reads an isolated glyph.
/// </summary>
public sealed class OnnxSymbolClassifierTests
{
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "Models");

    [Fact]
    public void OnnxRuntime_Matches_Python_Scores_On_Same_Tensors()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity_fixture.json"));
        using JsonDocument fixture = JsonDocument.Parse(json);
        JsonElement samples = fixture.RootElement.GetProperty("samples");

        using var session = new InferenceSession(Path.Combine(ModelDir, "crohme_geo_cnn.onnx"));

        foreach (JsonElement sample in samples.EnumerateArray())
        {
            float[] image = sample.GetProperty("image").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            float[] features = sample.GetProperty("features").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            float[] expectedScores = sample.GetProperty("scores").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            int expectedIndex = sample.GetProperty("expected_index").GetInt32();

            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(image, new[] { 1, 1, 32, 32 })),
                NamedOnnxValue.CreateFromTensor("features", new DenseTensor<float>(features, new[] { 1, 5 })),
            };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);
            float[] scores = results.First().AsEnumerable<float>().ToArray();

            Assert.Equal(expectedScores.Length, scores.Length);
            for (int i = 0; i < scores.Length; i++)
            {
                Assert.True(Math.Abs(scores[i] - expectedScores[i]) < 1e-3,
                    $"score[{i}] C#={scores[i]} Python={expectedScores[i]}");
            }

            int argmax = Array.IndexOf(scores, scores.Max());
            Assert.Equal(expectedIndex, argmax);
        }
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
