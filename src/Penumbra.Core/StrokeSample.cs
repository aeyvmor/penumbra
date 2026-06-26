namespace Penumbra.Core;

/// <summary>One time-ordered point in a pen stroke.</summary>
public readonly record struct StrokeSample(double X, double Y, TimeSpan Time, double Pressure);
