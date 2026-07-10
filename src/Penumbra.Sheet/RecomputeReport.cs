namespace Penumbra.Sheet;

/// <summary>The two deliberately distinct outcomes of one incremental recomputation.</summary>
/// <param name="ChangedResultNodes">
/// Nodes whose stored result changed by value. Consumers use this set to replace or remove owned
/// answer ink; a node that recomputed to an equal result is intentionally absent.
/// </param>
/// <param name="CausallyAffectedNodes">
/// Every surviving node in the expanded dirty set, including nodes whose result remained equal.
/// The order is deterministic and follows graph evaluation: cycle members by insertion order, then
/// acyclic nodes dependency-first. Consumers use this broader set for causality feedback, never as
/// a synonym for result changes.
/// </param>
public sealed record RecomputeReport(
    IReadOnlyList<SheetNode> ChangedResultNodes,
    IReadOnlyList<SheetNode> CausallyAffectedNodes);
