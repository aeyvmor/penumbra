namespace Penumbra.Sheet;

/// <summary>
/// The outcome of a non-mutating what-if query (<see cref="SheetGraph.Probe"/>): the results the sheet
/// <em>would</em> show if one node's LaTeX were replaced by a trial, without committing anything.
/// </summary>
/// <remarks>
/// Deliberately NOT a <see cref="RecomputeReport"/>: that type's shape signals committed results and the
/// owned-answer-ink replacement that follows a real recompute, whereas a probe touches no node.
/// </remarks>
/// <param name="Entries">
/// The probed node plus every node whose result the trial would change or recompute — the "affected
/// set" — in the same deterministic evaluation order <see cref="SheetGraph.RecomputeDetailed"/> uses:
/// cycle members by insertion index first, then the acyclic topological order. The probed node always
/// appears; a probe that changes nothing downstream yields a single entry.
/// </param>
public sealed record SheetProbeReport(IReadOnlyList<ProbeEntry> Entries);
