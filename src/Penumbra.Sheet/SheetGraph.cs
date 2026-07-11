using Penumbra.Cas;
using Penumbra.Core;

namespace Penumbra.Sheet;

/// <summary>
/// The reactive-sheet dependency graph (Seam 2). Nodes are keyed by external region id and hold LaTeX
/// only; the graph asks the CAS two questions through interfaces — <see cref="IExpressionAnalyzer"/>
/// (what does this define / depend on?) and <see cref="IEvaluator"/> (what's its value?) — and never
/// touches an <c>Entity</c> itself. Recompute is pull-based and incremental: an edit dirties one node,
/// dirt flows to its transitive dependents, and only that set is re-evaluated in topological order.
/// </summary>
/// <remarks>
/// Semantics (kickoff decision 4): a dependency <b>cycle</b> gives every member an
/// <see cref="EvaluationKind.Error"/> result with no partial evaluation; <b>duplicate definitions</b> of
/// one symbol keep the topmost-by-region-y winner (first-inserted when regions are absent) and flag the
/// rest as <see cref="SheetNode.IsConflict"/>.
/// <para>
/// The resolve/edge/cycle/order phases are factored into pure functions over a per-node
/// <see cref="EffectiveView"/> that return scratch structures without writing to any node.
/// <see cref="RecomputeDetailed"/> runs them and then commits the outcome to the nodes;
/// <see cref="Probe"/> runs the same phases against a hypothetical view and never commits — this is the
/// single seam that lets the graph answer "what if?" with byte-for-byte recompute semantics.
/// </para>
/// </remarks>
public sealed class SheetGraph
{
    private readonly IEvaluator _evaluator;
    private readonly IExpressionAnalyzer _analyzer;
    private readonly Dictionary<Guid, SheetNode> _nodes = new();
    private long _sequence;

    public SheetGraph(IEvaluator evaluator, IExpressionAnalyzer analyzer)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    /// <summary>All nodes currently in the sheet, in no particular order.</summary>
    public IReadOnlyCollection<SheetNode> Nodes => _nodes.Values;

    /// <summary>The node for a region id, or <c>null</c> if none.</summary>
    public SheetNode? Find(Guid id) => _nodes.GetValueOrDefault(id);

    /// <summary>
    /// Creates or updates the node for <paramref name="id"/>. Re-analyses the LaTeX immediately (so role
    /// and edges are current) and, when the LaTeX actually changed, marks the node dirty — plus any peer
    /// defining the same symbol, whose duplicate-definition winner may now shift.
    /// </summary>
    public SheetNode Upsert(
        Guid id,
        string latex,
        IReadOnlyList<RecognizedToken>? tokens = null,
        InkBounds? region = null)
    {
        latex ??= string.Empty;

        var exists = _nodes.TryGetValue(id, out var node);
        if (!exists || node is null)
        {
            node = new SheetNode(id, _sequence++);
            _nodes[id] = node;
        }

        var analysis = _analyzer.Analyze(latex);
        var oldSymbol = node.DefinedSymbol;
        var latexChanged = !exists || !string.Equals(node.Latex, latex, StringComparison.Ordinal);
        var regionChanged = !exists || node.Region != region;

        node.Latex = latex;
        node.Tokens = tokens ?? Array.Empty<RecognizedToken>();
        node.Region = region;
        node.DefinedSymbol = analysis.DefinedSymbol;
        node.FreeVariables = analysis.FreeVariables;
        node.Role = analysis.DefinedSymbol is not null
            ? NodeRole.Definition
            : analysis.IsQuery ? NodeRole.Query : NodeRole.Statement;

        // Region position is semantic when definitions conflict: the topmost definition wins. Moving
        // unchanged LaTeX above/below a peer can therefore change the owner and every dependent result.
        if (latexChanged || regionChanged)
        {
            node.Dirty = true;
            // The edit can SEVER edges (renaming "x=5" to "y=5" leaves x's users with no owner), and
            // Recompute rebuilds edges before it ripples dirt — so the nodes that depended on this one
            // BEFORE the edit must be seeded dirty here, or they'd keep a stale substituted result.
            foreach (var dependentId in node.Dependents)
            {
                if (_nodes.TryGetValue(dependentId, out var dependent))
                {
                    dependent.Dirty = true;
                }
            }

            // A definition appearing/leaving/moving can change who wins a symbol, so re-check peers.
            MarkSymbolDirty(oldSymbol, exclude: node.Id);
            MarkSymbolDirty(node.DefinedSymbol, exclude: node.Id);
        }

        return node;
    }

    /// <summary>Removes a node. Its dependents (and peers sharing its symbol) are dirtied so the next
    /// recompute re-binds them. Returns false if no such node exists.</summary>
    public bool Remove(Guid id)
    {
        if (!_nodes.TryGetValue(id, out var node))
        {
            return false;
        }

        foreach (var dependentId in node.Dependents)
        {
            if (_nodes.TryGetValue(dependentId, out var dependent))
            {
                dependent.Dirty = true;
            }
        }

        MarkSymbolDirty(node.DefinedSymbol, exclude: id);
        _nodes.Remove(id);
        return true;
    }

    /// <summary>
    /// Pull-based incremental recompute: resolve definition winners, rebuild edges, propagate dirt to
    /// transitive dependents, then re-evaluate the dirty set in topological order — feeding each
    /// definition's value downstream as <see cref="EvaluationRequest.Variables"/>. Returns the nodes
    /// whose <see cref="SheetNode.Result"/> changed.
    /// </summary>
    public IReadOnlyList<SheetNode> Recompute() => RecomputeDetailed().ChangedResultNodes;

    /// <summary>
    /// Recomputes the expanded dirty set and reports both value changes and causal participation.
    /// </summary>
    /// <remarks>
    /// <see cref="RecomputeReport.ChangedResultNodes"/> preserves <see cref="Recompute"/>'s historical
    /// value-equality contract. <see cref="RecomputeReport.CausallyAffectedNodes"/> is broader: it also
    /// contains nodes which were recomputed but produced an equal result. Its deterministic order is
    /// the actual evaluation order, suitable for a causality ripple without replaying unchanged answers.
    /// </remarks>
    public RecomputeReport RecomputeDetailed()
    {
        var nodes = _nodes.Values.ToList();
        var views = nodes.Select(EffectiveView.Committed).ToList();
        var plan = BuildPlan(views);

        // Committing the plan is the ONLY place recompute mutates node structure: the resolve/edge/cycle
        // phases above are pure and wrote nothing, so their outcome is applied to the nodes here.
        foreach (var node in nodes)
        {
            node.IsConflict = plan.Conflicts.Contains(node.Id);
            node.DependsOn.Clear();
            node.DependsOn.UnionWith(plan.DependsOn[node.Id]);
            node.Dependents.Clear();
            node.Dependents.UnionWith(plan.Dependents[node.Id]);
        }

        var dirty = Expand(nodes.Where(n => n.Dirty).Select(n => n.Id), plan.Dependents);

        var changed = new List<SheetNode>();
        var affected = new List<SheetNode>(dirty.Count);

        // Cycle members error out first (no evaluation, no partial results — kickoff decision 4).
        foreach (var node in nodes.OrderBy(n => n.InsertionIndex))
        {
            if (plan.CycleIds.Contains(node.Id) && dirty.Contains(node.Id))
            {
                affected.Add(node);
                Apply(node, CycleError, changed);
            }
        }

        // Everything acyclic evaluates in dependency order so definers resolve before their dependents.
        // Bindings read the live node results, which are already fresh for earlier nodes in this order.
        foreach (var id in plan.Order)
        {
            if (!dirty.Contains(id))
            {
                continue;
            }

            var node = _nodes[id];
            affected.Add(node);
            var result = plan.Conflicts.Contains(id)
                ? ConflictError(plan.ViewById[id].DefinedSymbol)
                : EvaluateView(plan.ViewById[id], plan.Owners, CommittedResult);
            Apply(node, result, changed);
        }

        foreach (var node in nodes)
        {
            node.Dirty = false;
        }

        return new RecomputeReport(changed, affected);
    }

    /// <summary>
    /// Non-mutating "what if?": returns the results the sheet <em>would</em> show if
    /// <paramref name="nodeId"/>'s LaTeX were replaced by <paramref name="trialLatex"/>, without touching
    /// any node's <see cref="SheetNode.Result"/>, <see cref="SheetNode.IsConflict"/>,
    /// <see cref="SheetNode.Dirty"/> flag, or edges.
    /// </summary>
    /// <param name="nodeId">The node to probe. Must exist, else <see cref="ArgumentException"/>.</param>
    /// <param name="trialLatex">The hypothetical LaTeX for that node (a null is treated as empty).</param>
    /// <returns>
    /// A <see cref="SheetProbeReport"/> whose entries are the probed node plus every node the trial would
    /// change or recompute — the affected set — in <see cref="RecomputeDetailed"/>'s evaluation order
    /// (cycle members by insertion index, then acyclic topological order). Deliberately not a
    /// <see cref="RecomputeReport"/>: that shape implies committed results.
    /// </returns>
    /// <remarks>
    /// Semantics are inherited from the committed path, not re-implemented: the trial is re-analysed
    /// through <see cref="IExpressionAnalyzer"/> and fed through the same pure resolve/edge/cycle/order
    /// phases into scratch structures; each affected node is evaluated into a scratch result map, reading
    /// upstream values scratch-first and falling back to the committed <see cref="SheetNode.Result"/>
    /// under the same computed-value guard. A missing/cyclic/errored upstream leaves a variable free
    /// (honestly symbolic); cycle members yield a cyclic error with no evaluation; conflict losers yield
    /// a conflict error; a definition-role probed node evaluates its right-hand side only. The probe is
    /// idempotent and observationally invisible: pending dirty state is left exactly as found.
    /// </remarks>
    public SheetProbeReport Probe(Guid nodeId, string trialLatex)
    {
        if (!_nodes.TryGetValue(nodeId, out _))
        {
            throw new ArgumentException($"No sheet node has id '{nodeId}'.", nameof(nodeId));
        }

        trialLatex ??= string.Empty;
        var nodes = _nodes.Values.ToList();

        // Every node keeps its committed view except the probed one, which is re-analysed under the trial.
        var committedViews = nodes.Select(EffectiveView.Committed).ToList();
        var analysis = _analyzer.Analyze(trialLatex);
        var trialRole = analysis.DefinedSymbol is not null
            ? NodeRole.Definition
            : analysis.IsQuery ? NodeRole.Query : NodeRole.Statement;
        var trialViews = nodes.Select(n => n.Id == nodeId
            ? new EffectiveView(
                n.Id,
                n.InsertionIndex,
                n.Region?.Y ?? double.PositiveInfinity,
                analysis.DefinedSymbol,
                analysis.FreeVariables,
                trialRole,
                trialLatex)
            : EffectiveView.Committed(n)).ToList();

        // Committed resolution is needed only to spot which nodes the trial re-binds or re-flags.
        var (committedOwners, committedConflicts) = ResolveDefinitions(committedViews);
        var trial = BuildPlan(trialViews);

        // Seed the affected set with the probed node plus every node the trial re-binds (its free vars
        // resolve to a different owner) or re-flags (its conflict status flips). A plain digit swap adds
        // no extra seeds and degenerates to the probed node's downstream cone; a symbol rename reseeds
        // the old dependents that just lost their binding.
        var seeds = new HashSet<Guid> { nodeId };
        foreach (var view in trialViews)
        {
            if (committedConflicts.Contains(view.Id) != trial.Conflicts.Contains(view.Id)
                || BindingDiffers(view.FreeVariables, committedOwners, trial.Owners))
            {
                seeds.Add(view.Id);
            }
        }

        var affected = Expand(seeds, trial.Dependents);

        // Evaluate the affected set into scratch — never touching a node — in recompute's own order:
        // cycle members by insertion index, then the acyclic topological order.
        var scratch = new Dictionary<Guid, EvaluationResult>();
        EvaluationResult? ScratchFirst(Guid id) =>
            scratch.TryGetValue(id, out var r) ? r : _nodes[id].Result;

        var entries = new List<ProbeEntry>(affected.Count);
        foreach (var node in nodes.OrderBy(n => n.InsertionIndex))
        {
            if (trial.CycleIds.Contains(node.Id) && affected.Contains(node.Id))
            {
                scratch[node.Id] = CycleError;
                entries.Add(new ProbeEntry(node, CycleError));
            }
        }

        foreach (var id in trial.Order)
        {
            if (!affected.Contains(id))
            {
                continue;
            }

            var view = trial.ViewById[id];
            var result = trial.Conflicts.Contains(id)
                ? ConflictError(view.DefinedSymbol)
                : EvaluateView(view, trial.Owners, ScratchFirst);
            scratch[id] = result;
            entries.Add(new ProbeEntry(_nodes[id], result));
        }

        return new SheetProbeReport(entries);
    }

    private static void Apply(SheetNode node, EvaluationResult result, List<SheetNode> changed)
    {
        if (!Equals(node.Result, result))
        {
            node.Result = result;
            changed.Add(node);
        }
    }

    /// <summary>The committed evaluation of a node, read live so a node evaluated earlier in this pass is
    /// already fresh. This is the variable-binding source for the committed recompute path.</summary>
    private EvaluationResult? CommittedResult(Guid id) => _nodes[id].Result;

    /// <summary>
    /// Evaluates one node's effective view: a definition's RHS (its value), or a query/statement whole.
    /// Binds each free variable to its owner's value via <paramref name="resultOf"/> — only when that
    /// owner is a distinct node with a computed result; a missing/cyclic/errored owner leaves the
    /// variable free so the expression stays honestly symbolic rather than silently wrong.
    /// </summary>
    private EvaluationResult EvaluateView(
        EffectiveView view,
        IReadOnlyDictionary<string, Guid> owners,
        Func<Guid, EvaluationResult?> resultOf)
    {
        var variables = new Dictionary<string, string>();
        foreach (var symbol in view.FreeVariables)
        {
            if (owners.TryGetValue(symbol, out var ownerId)
                && ownerId != view.Id
                && resultOf(ownerId) is { IsComputed: true } value)
            {
                variables[symbol] = value.Latex;
            }
        }

        var latex = view.Role == NodeRole.Definition ? RightHandSide(view.Latex) : view.Latex;
        return _evaluator.Evaluate(new EvaluationRequest(latex, variables));
    }

    /// <summary>Runs the four pure structural phases over one set of effective views and packages their
    /// scratch outputs. Writes nothing; callers decide whether to commit or merely inspect the plan.</summary>
    private static RecomputePlan BuildPlan(IReadOnlyList<EffectiveView> views)
    {
        var (owners, conflicts) = ResolveDefinitions(views);
        var (dependsOn, dependents) = BuildEdges(views, owners);
        var cycleIds = FindCycleMembers(views, dependsOn);
        var order = TopologicalOrder(views, cycleIds, dependsOn, dependents);
        var viewById = views.ToDictionary(v => v.Id);
        return new RecomputePlan(viewById, owners, conflicts, dependsOn, dependents, cycleIds, order);
    }

    /// <summary>True when any of <paramref name="freeVariables"/> resolves to a different owner (or from
    /// no owner to one, or vice versa) between the committed and trial resolutions.</summary>
    private static bool BindingDiffers(
        IReadOnlySet<string> freeVariables,
        IReadOnlyDictionary<string, Guid> committedOwners,
        IReadOnlyDictionary<string, Guid> trialOwners)
    {
        foreach (var symbol in freeVariables)
        {
            var committed = committedOwners.TryGetValue(symbol, out var c) ? c : (Guid?)null;
            var trial = trialOwners.TryGetValue(symbol, out var t) ? t : (Guid?)null;
            if (committed != trial)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Assigns each defined symbol its winner and collects the losers. Winner: topmost by region
    /// y; with no region, first inserted. Pure — flags nothing on the nodes.</summary>
    private static (Dictionary<string, Guid> Owners, HashSet<Guid> Conflicts) ResolveDefinitions(
        IReadOnlyList<EffectiveView> views)
    {
        var owners = new Dictionary<string, Guid>();
        var conflicts = new HashSet<Guid>();

        foreach (var group in views.Where(v => v.DefinedSymbol is not null)
                     .GroupBy(v => v.DefinedSymbol!))
        {
            var ranked = group
                .OrderBy(v => v.RankY)
                .ThenBy(v => v.InsertionIndex)
                .ToList();

            owners[group.Key] = ranked[0].Id;
            for (var i = 1; i < ranked.Count; i++)
            {
                conflicts.Add(ranked[i].Id);
            }
        }

        return (owners, conflicts);
    }

    /// <summary>Builds the dependency adjacency (each node → the owners of its free variables) and its
    /// inverse, as scratch maps keyed by node id. Pure — rewrites no node's edges.</summary>
    private static (Dictionary<Guid, HashSet<Guid>> DependsOn, Dictionary<Guid, HashSet<Guid>> Dependents)
        BuildEdges(IReadOnlyList<EffectiveView> views, IReadOnlyDictionary<string, Guid> owners)
    {
        var dependsOn = views.ToDictionary(v => v.Id, _ => new HashSet<Guid>());
        var dependents = views.ToDictionary(v => v.Id, _ => new HashSet<Guid>());

        foreach (var view in views)
        {
            foreach (var symbol in view.FreeVariables)
            {
                // A self-edge is kept on purpose: "a = a+1" must surface as a cycle of one.
                if (owners.TryGetValue(symbol, out var ownerId))
                {
                    dependsOn[view.Id].Add(ownerId);
                }
            }
        }

        foreach (var view in views)
        {
            foreach (var dependencyId in dependsOn[view.Id])
            {
                dependents[dependencyId].Add(view.Id);
            }
        }

        return (dependsOn, dependents);
    }

    /// <summary>
    /// Tarjan's strongly-connected components over the dependency edges. A node is a cycle member iff
    /// its SCC has more than one node, or it depends on itself. This is deliberately not "whatever a
    /// topological sort leaves over" — a node merely <em>downstream</em> of a cycle is not in the cycle
    /// and must still evaluate (it just sees no value for the cyclic symbol).
    /// </summary>
    private static HashSet<Guid> FindCycleMembers(
        IReadOnlyList<EffectiveView> views,
        IReadOnlyDictionary<Guid, HashSet<Guid>> dependsOn)
    {
        var index = new Dictionary<Guid, int>();
        var lowLink = new Dictionary<Guid, int>();
        var onStack = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        var cycleIds = new HashSet<Guid>();
        var next = 0;

        void Connect(Guid id)
        {
            index[id] = lowLink[id] = next++;
            stack.Push(id);
            onStack.Add(id);

            foreach (var depId in dependsOn[id])
            {
                if (!index.ContainsKey(depId))
                {
                    Connect(depId);
                    lowLink[id] = Math.Min(lowLink[id], lowLink[depId]);
                }
                else if (onStack.Contains(depId))
                {
                    lowLink[id] = Math.Min(lowLink[id], index[depId]);
                }
            }

            if (lowLink[id] == index[id])
            {
                var component = new List<Guid>();
                Guid member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (member != id);

                if (component.Count > 1 || dependsOn[id].Contains(id))
                {
                    cycleIds.UnionWith(component);
                }
            }
        }

        foreach (var view in views)
        {
            if (!index.ContainsKey(view.Id))
            {
                Connect(view.Id);
            }
        }

        return cycleIds;
    }

    /// <summary>Kahn's algorithm over the acyclic remainder: a dependency-first order of every node
    /// outside a cycle, treating edges from cycle members as already satisfied. Returns node ids.</summary>
    private static IReadOnlyList<Guid> TopologicalOrder(
        IReadOnlyList<EffectiveView> views,
        HashSet<Guid> cycleIds,
        IReadOnlyDictionary<Guid, HashSet<Guid>> dependsOn,
        IReadOnlyDictionary<Guid, HashSet<Guid>> dependents)
    {
        var insertionIndex = views.ToDictionary(v => v.Id, v => v.InsertionIndex);
        var acyclic = views.Where(v => !cycleIds.Contains(v.Id)).ToList();
        var inDegree = acyclic.ToDictionary(
            v => v.Id,
            v => dependsOn[v.Id].Count(depId => !cycleIds.Contains(depId)));
        var ready = new Queue<Guid>(
            acyclic.Where(v => inDegree[v.Id] == 0)
                .OrderBy(v => v.InsertionIndex)
                .Select(v => v.Id));

        var order = new List<Guid>(acyclic.Count);
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            order.Add(id);
            foreach (var dependentId in dependents[id].OrderBy(d => insertionIndex[d]))
            {
                if (!cycleIds.Contains(dependentId) && --inDegree[dependentId] == 0)
                {
                    ready.Enqueue(dependentId);
                }
            }
        }

        return order;
    }

    /// <summary>The seed set grown along dependent edges (a change ripples downstream). Membership only —
    /// order is irrelevant, so a set suffices for both the committed dirty set and a probe's affected set.</summary>
    private static HashSet<Guid> Expand(
        IEnumerable<Guid> seeds,
        IReadOnlyDictionary<Guid, HashSet<Guid>> dependents)
    {
        var reached = new HashSet<Guid>();
        var stack = new Stack<Guid>(seeds);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reached.Add(id))
            {
                continue;
            }

            foreach (var dependentId in dependents[id])
            {
                stack.Push(dependentId);
            }
        }

        return reached;
    }

    private void MarkSymbolDirty(string? symbol, Guid exclude)
    {
        if (symbol is null)
        {
            return;
        }

        foreach (var node in _nodes.Values)
        {
            if (node.Id != exclude && node.DefinedSymbol == symbol)
            {
                node.Dirty = true;
            }
        }
    }

    /// <summary>The part of a definition after its binding <c>=</c> — the value to evaluate.</summary>
    private static string RightHandSide(string latex)
    {
        var braceDepth = 0;
        var parenDepth = 0;
        for (var i = 0; i < latex.Length; i++)
        {
            var c = latex[i];
            switch (c)
            {
                case '{' or '(':
                    if (c == '{') braceDepth++; else parenDepth++;
                    break;
                case '}' or ')':
                    if (c == '}') braceDepth--; else parenDepth--;
                    break;
                case '=' when braceDepth == 0 && parenDepth == 0:
                {
                    var prev = i > 0 ? latex[i - 1] : '\0';
                    var next = i + 1 < latex.Length ? latex[i + 1] : '\0';
                    if (prev is '<' or '>' or '!' or '=' || next == '=')
                    {
                        break;
                    }

                    return latex[(i + 1)..];
                }
            }
        }

        return latex; // no binding "=" found — evaluate as-is (shouldn't happen for a definition)
    }

    private static EvaluationResult CycleError { get; } =
        new(string.Empty, "Cyclic dependency", IsComputed: false, EvaluationKind.Error);

    private static EvaluationResult ConflictError(string? symbol) =>
        new(string.Empty, $"'{symbol}' is already defined", IsComputed: false, EvaluationKind.Error);

    /// <summary>
    /// A read-only snapshot of the per-node facts the structural phases need — its identity, ranking
    /// keys, and analysed shape (defined symbol / free variables / role / latex). Built from a committed
    /// node via <see cref="Committed"/>, or synthesised for a probe's trial node. The phases operate on
    /// these views alone, which is what lets a probe reason about a hypothetical without mutating nodes.
    /// </summary>
    private readonly record struct EffectiveView(
        Guid Id,
        long InsertionIndex,
        double RankY,
        string? DefinedSymbol,
        IReadOnlySet<string> FreeVariables,
        NodeRole Role,
        string Latex)
    {
        /// <summary>The view of a node exactly as it currently stands.</summary>
        public static EffectiveView Committed(SheetNode node) => new(
            node.Id,
            node.InsertionIndex,
            node.Region?.Y ?? double.PositiveInfinity,
            node.DefinedSymbol,
            node.FreeVariables,
            node.Role,
            node.Latex);
    }

    /// <summary>The pure scratch outcome of the structural phases for one set of views: symbol owners,
    /// conflict losers, adjacency both ways, cycle members, and the acyclic evaluation order. Nothing
    /// here is bound to a node — <see cref="RecomputeDetailed"/> commits it; <see cref="Probe"/> reads it.</summary>
    private sealed record RecomputePlan(
        IReadOnlyDictionary<Guid, EffectiveView> ViewById,
        Dictionary<string, Guid> Owners,
        HashSet<Guid> Conflicts,
        Dictionary<Guid, HashSet<Guid>> DependsOn,
        Dictionary<Guid, HashSet<Guid>> Dependents,
        HashSet<Guid> CycleIds,
        IReadOnlyList<Guid> Order);
}
