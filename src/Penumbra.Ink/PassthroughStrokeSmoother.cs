using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>Phase 0 stroke smoother that preserves input unchanged.</summary>
public sealed class PassthroughStrokeSmoother : IStrokeSmoother
{
    /// <inheritdoc />
    public Stroke Smooth(Stroke stroke)
    {
        return stroke;
    }
}
