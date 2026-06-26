using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>Smooths captured online strokes before recognition or rendering.</summary>
public interface IStrokeSmoother
{
    /// <summary>Returns a smoothed copy of the supplied stroke.</summary>
    Stroke Smooth(Stroke stroke);
}
