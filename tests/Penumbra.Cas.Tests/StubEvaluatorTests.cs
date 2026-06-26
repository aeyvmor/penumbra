using Penumbra.Cas;

namespace Penumbra.Cas.Tests;

public sealed class StubEvaluatorTests
{
    [Fact]
    public void EvaluateReturnsPendingResult()
    {
        var evaluator = new StubEvaluator();
        var request = new EvaluationRequest("2+2", new Dictionary<string, string>());

        var result = evaluator.Evaluate(request);

        Assert.False(result.IsComputed);
        Assert.Equal("2+2", result.Latex);
    }
}
