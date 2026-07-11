using Penumbra.Cas;

namespace Penumbra.Sheet;

/// <summary>
/// One row of a <see cref="SheetGraph.Probe"/> result: a node paired with the value it <em>would</em>
/// take under the trial, computed entirely against scratch state.
/// </summary>
/// <param name="Node">
/// The affected node — the probed node itself, or one downstream of the trial's effect. The same live
/// <see cref="SheetNode"/> instance the graph holds; the probe never writes to it.
/// </param>
/// <param name="TrialResult">
/// The evaluation the node would produce under the trial: a value, an <see cref="EvaluationKind.Error"/>
/// for a cyclic or conflicting node, or an honestly-symbolic result when an upstream stays unbound. This
/// is a hypothetical and is deliberately NOT written back to <paramref name="Node"/>.
/// </param>
public sealed record ProbeEntry(SheetNode Node, EvaluationResult TrialResult);
