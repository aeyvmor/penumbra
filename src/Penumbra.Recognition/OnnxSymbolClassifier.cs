using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// R1 per-symbol classifier: renders one symbol's strokes to the normalized 32x32 bitmap, computes
/// its geometry features in the supplied sibling context, and runs the CROHME geometry-aware ONNX
/// model. This is the model half split out of the former <c>OnnxSymbolRecognizer</c>;
/// <see cref="ExpressionRecognizer"/> drives segmentation around it.
/// </summary>
public sealed class OnnxSymbolClassifier : ISymbolClassifier, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _classes;
    private readonly float _mean;
    private readonly float _std;
    private readonly float[] _featMean;
    private readonly float[] _featStd;

    public OnnxSymbolClassifier(string? modelDirectory = null)
    {
        modelDirectory ??= Path.Combine(AppContext.BaseDirectory, "Models");
        string onnxPath = Path.Combine(modelDirectory, "crohme_geo_cnn.onnx");
        string metaPath = Path.Combine(modelDirectory, "crohme_geo_cnn.meta.json");

        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        JsonElement root = meta.RootElement;
        _classes = root.GetProperty("classes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        _mean = (float)root.GetProperty("mean").GetDouble();
        _std = (float)root.GetProperty("std").GetDouble();
        _featMean = root.GetProperty("feat_mean").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        _featStd = root.GetProperty("feat_std").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();

        _session = new InferenceSession(onnxPath);
    }

    /// <inheritdoc />
    public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (strokes.Count == 0 || strokes.All(s => s.Samples.Count == 0))
        {
            return new SymbolPrediction(string.Empty, 0);
        }

        float[] image = SymbolPreprocessor.RenderImage(strokes, _mean, _std);
        float[] features = SymbolPreprocessor.ComputeFeatures(strokes, context, _featMean, _featStd);

        var imageTensor = new DenseTensor<float>(image, new[] { 1, 1, SymbolPreprocessor.ImageSize, SymbolPreprocessor.ImageSize });
        var featureTensor = new DenseTensor<float>(features, new[] { 1, features.Length });

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("image", imageTensor),
            NamedOnnxValue.CreateFromTensor("features", featureTensor),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
        float[] scores = results.First().AsEnumerable<float>().ToArray();

        (int best, float confidence) = Softmax(scores);
        return new SymbolPrediction(_classes[best], confidence);
    }

    private static (int Index, float Probability) Softmax(float[] scores)
    {
        float max = scores.Max();
        double sum = 0;
        int best = 0;
        for (int i = 0; i < scores.Length; i++)
        {
            sum += Math.Exp(scores[i] - max);
            if (scores[i] > scores[best])
            {
                best = i;
            }
        }

        float prob = (float)(Math.Exp(scores[best] - max) / sum);
        return (best, prob);
    }

    public void Dispose() => _session.Dispose();
}
