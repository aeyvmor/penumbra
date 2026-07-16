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
        Calibration = ReadCalibration(root);

        _session = new InferenceSession(onnxPath);
    }

    /// <summary>
    /// The decision contract shipped in this model's <c>meta.json</c> (audit B4) — temperature, energy
    /// reject threshold, and the app's reject/bank confidence bars. <see cref="RecognitionCalibration.Default"/>
    /// when the meta predates the calibrated-retrain fields, so old artifacts behave exactly as before.
    /// </summary>
    public RecognitionCalibration Calibration { get; }

    /// <summary>
    /// Reads the optional B4 calibration fields, falling back per-field to
    /// <see cref="RecognitionCalibration.Default"/> — a partially calibrated meta (e.g. temperature fitted
    /// but no reject threshold picked) still gets sensible behaviour for the missing pieces.
    /// </summary>
    private static RecognitionCalibration ReadCalibration(JsonElement root) => new(
        Temperature: ReadDoubleOrDefault(root, "temperature", RecognitionCalibration.Default.Temperature),
        RejectEnergyThreshold: ReadDoubleOrDefault(
            root, "reject_energy_threshold", RecognitionCalibration.Default.RejectEnergyThreshold),
        MinConfidence: ReadDoubleOrDefault(root, "min_confidence", RecognitionCalibration.Default.MinConfidence),
        BankConfidence: ReadDoubleOrDefault(root, "bank_confidence", RecognitionCalibration.Default.BankConfidence));

    private static double ReadDoubleOrDefault(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;

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

        return Predict(scores);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One <c>_session.Run</c> for the whole line via the model's dynamic batch axis (Phase 4.5a) —
    /// the per-symbol preprocessing is identical to <see cref="Classify"/>, so batch and single
    /// calls score identically; the parity test pins that.
    /// </remarks>
    public IReadOnlyList<SymbolPrediction> ClassifyBatch(
        IReadOnlyList<IReadOnlyList<Stroke>> symbols, SymbolContext context)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var predictions = new SymbolPrediction[symbols.Count];

        // Preprocess every non-empty symbol; empty ones keep the same "no ink → empty label" contract
        // as Classify without ever reaching the model.
        var batchIndices = new List<int>(symbols.Count);
        for (int i = 0; i < symbols.Count; i++)
        {
            if (symbols[i].Count > 0 && symbols[i].Any(s => s.Samples.Count > 0))
            {
                batchIndices.Add(i);
            }
            else
            {
                predictions[i] = new SymbolPrediction(string.Empty, 0);
            }
        }

        if (batchIndices.Count == 0)
        {
            return predictions;
        }

        int n = batchIndices.Count;
        int imageLen = SymbolPreprocessor.ImageSize * SymbolPreprocessor.ImageSize;
        var images = new float[n * imageLen];
        var features = new float[n * SymbolPreprocessor.FeatureCount];
        for (int b = 0; b < n; b++)
        {
            IReadOnlyList<Stroke> strokes = symbols[batchIndices[b]];
            SymbolPreprocessor.RenderImage(strokes, _mean, _std).CopyTo(images, b * imageLen);
            SymbolPreprocessor.ComputeFeatures(strokes, context, _featMean, _featStd)
                .CopyTo(features, b * SymbolPreprocessor.FeatureCount);
        }

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(
                images, new[] { n, 1, SymbolPreprocessor.ImageSize, SymbolPreprocessor.ImageSize })),
            NamedOnnxValue.CreateFromTensor("features", new DenseTensor<float>(
                features, new[] { n, SymbolPreprocessor.FeatureCount })),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
        float[] scores = results.First().AsEnumerable<float>().ToArray();
        int classCount = scores.Length / n;

        for (int b = 0; b < n; b++)
        {
            var row = new float[classCount];
            Array.Copy(scores, b * classCount, row, 0, classCount);
            predictions[batchIndices[b]] = Predict(row);
        }

        return predictions;
    }

    /// <summary>Ranked alternatives exposed per prediction (winner + up to this many runners-up).</summary>
    private const int TopKAlternatives = 5;

    /// <summary>
    /// One symbol's logits → calibrated prediction. Single and batch paths both land here, so temperature
    /// scaling and energy rejection can never diverge between them (the batch-vs-single parity test pins it).
    /// <see cref="RecognitionCalibration.Apply"/> alone decides <c>Label</c>/<c>Confidence</c>/<c>Energy</c>/
    /// <c>Rejected</c> — <see cref="SymbolPrediction.Alternatives"/> is filled from a separate, independent
    /// <see cref="RecognitionCalibration.RankedConfidences"/> call over the same logits, so adding it can
    /// never perturb the top-1 contract (proved on the parity fixture in
    /// <c>OnnxSymbolClassifierTests.Alternatives_DoNotChangeTop1_AndTopEntryMatchesPrediction</c>).
    /// </summary>
    private SymbolPrediction Predict(float[] logits)
    {
        (int best, double confidence, double energy, bool rejected) = Calibration.Apply(logits);

        IReadOnlyList<(int Index, double Confidence)> ranked = Calibration.RankedConfidences(logits);
        int k = Math.Min(TopKAlternatives, ranked.Count);
        var alternatives = new SymbolAlternative[k];
        for (int i = 0; i < k; i++)
        {
            alternatives[i] = new SymbolAlternative(_classes[ranked[i].Index], ranked[i].Confidence);
        }

        return new SymbolPrediction(_classes[best], confidence, energy, rejected, alternatives);
    }

    public void Dispose() => _session.Dispose();
}
