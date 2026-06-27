using Microsoft.Extensions.DependencyInjection;
using Penumbra.App.ViewModels;
using Penumbra.Cas;
using Penumbra.Graphing;
using Penumbra.Ink;
using Penumbra.Recognition;

namespace Penumbra.App.Services;

/// <summary>Registers the Phase 0 application shell and engine seams.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds Penumbra services to the dependency-injection container.</summary>
    public static IServiceCollection AddPenumbraApp(this IServiceCollection services)
    {
        services.AddSingleton<IStrokeSmoother, ChaikinStrokeSmoother>();
        services.AddSingleton<IStrokeSegmenter, OverlapStrokeSegmenter>();
        services.AddSingleton<ISymbolClassifier, OnnxSymbolClassifier>();
        services.AddSingleton<IRecognizer, ExpressionRecognizer>();
        services.AddSingleton<IEvaluator, AngouriMathEvaluator>();
        services.AddSingleton<IGraphDetector, NoOpGraphDetector>();
        services.AddTransient<MainWindowViewModel>();

        return services;
    }
}
