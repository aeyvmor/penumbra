using Microsoft.Extensions.DependencyInjection;
using Penumbra.App.ViewModels;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Graphing;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.App.Services;

/// <summary>Registers the Phase 0 application shell and engine seams.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds Penumbra services to the dependency-injection container.</summary>
    public static IServiceCollection AddPenumbraApp(this IServiceCollection services)
    {
        services.AddSingleton<IStrokeSegmenter, OverlapStrokeSegmenter>();
        services.AddSingleton<ISymbolClassifier, OnnxSymbolClassifier>();
        // One recognizer owns both the legacy and region-aware seams. Registering the concrete once is
        // important: two instances could carry different model/calibration state and make manual and live
        // reads disagree even though they appear to use the same implementation.
        services.AddSingleton<ExpressionRecognizer>();
        services.AddSingleton<IRecognizer>(sp => sp.GetRequiredService<ExpressionRecognizer>());
        services.AddSingleton<IRegionRecognizer>(sp => sp.GetRequiredService<ExpressionRecognizer>());
        services.AddSingleton<IEvaluator, AngouriMathEvaluator>();
        services.AddSingleton<IExpressionAnalyzer, AngouriMathExpressionAnalyzer>();
        // A graph is page/VM state, never an application singleton. Every window receives an isolated sheet.
        services.AddTransient<SheetGraph>();
        services.AddSingleton<IGraphDetector, NoOpGraphDetector>();

        // B4: the decision contract (reject/bank bars, temperature + energy applied in-classifier) rides
        // the model artifact — the ViewModel gets whatever the loaded meta.json ships, with
        // RecognitionCalibration.Default covering pre-calibration models and non-ONNX classifiers.
        services.AddSingleton(sp => sp.GetRequiredService<ISymbolClassifier>() is OnnxSymbolClassifier onnx
            ? onnx.Calibration
            : RecognitionCalibration.Default);

        // 3.9d: the glyph bank fills passively during normal use; persisted per-user under AppData.
        services.AddSingleton<IGlyphBank>(_ => new JsonGlyphBank(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Penumbra",
            "glyphbank.json")));

        // Phase 4d/4e: synthesize the answer in the user's own hand. The source chain is the user's bank first,
        // then the Caveat cold-start font fallback — the user's hand always wins, the font only fills gaps. As
        // the bank fills, the font is consulted less: that IS the M2 crossfade. An absent bank/font just yields
        // a shorter chain rather than throwing — the app still runs, animation just degrades.
        services.AddSingleton(sp =>
        {
            var sources = new List<IGlyphSource>();
            if (sp.GetService<IGlyphBank>() is { } bank)
            {
                sources.Add(new BankGlyphSource(bank));
            }

            // Font is copied next to the app (see csproj). If it is missing/corrupt, skip the source — cold-start
            // fallback is off but the app must not crash.
            string fontPath = Path.Combine(AppContext.BaseDirectory, "assets", "Caveat-VariableFont_wght.ttf");
            try
            {
                sources.Add(new CaveatGlyphSource(fontPath));
            }
            catch (Exception)
            {
                // Degrade gracefully: no cold-start glyphs, but the bank chain and the rest of the app still work.
            }

            return new HandwritingSynthesizer(sources);
        });

        services.AddTransient<MainWindowViewModel>();

        return services;
    }
}
