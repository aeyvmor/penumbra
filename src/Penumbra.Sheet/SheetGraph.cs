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
    public IReadOnlyList<SheetNode> Recompute()
    {
        var nodes = _nodes.Values.ToList();

        var symbolOwner = ResolveDefinitions(nodes);
        RebuildEdges(nodes, symbolOwner);
        var cycleIds = FindCycleMembers(nodes);
        var order = TopologicalOrder(nodes, cycleIds);
        var dirty = ExpandDirty(nodes);

        var changed = new List<SheetNode>();

        // Cycle members error out first (no evaluation, no partial results — kickoff decision 4).
        foreach (var node in nodes)
        {
            if (cycleIds.Contains(node.Id) && dirty.Contains(node.Id))
            {
                Apply(node, CycleError, changed);
            }
        }

        // Everything acyclic evaluates in dependency order so definers resolve before their dependents.
        foreach (var node in order)
        {
            if (!dirty.Contains(node.Id))
            {
                continue;
            }

            var result = node.IsConflict
                ? ConflictError(node.DefinedSymbol)
                : Evaluate(node, symbolOwner);
            Apply(node, result, changed);
        }

        foreach (var node in nodes)
        {
            node.Dirty = false;
        }

        return changed;
    }

    private static void Apply(SheetNode node, EvaluationResult result, List<SheetNode> changed)
    {
        if (!Equals(node.Result, result))
        {
            node.Result = result;
            changed.Add(node);
        }
    }

    /// <summary>Evaluates one node: a definition's RHS (its value), or a query/statement whole.</summary>
    private EvaluationResult Evaluate(SheetNode node, IReadOnlyDictionary<string, SheetNode> symbolOwner)
    {
        var variables = new Dictionary<string, string>();
        foreach (var symbol in node.FreeVariables)
        {
            // Only bind symbols with a computed value; a missing/cyclic/errored definer leaves the
            // variable free, so the expression stays honestly symbolic rather than silently wrong.
            if (symbolOwner.TryGetValue(symbol, out var owner)
                && owner.Id != node.Id
                && owner.Result is { IsComputed: true } value)
            {
                variables[symbol] = value.Latex;
            }
        }

        var latex = node.Role == NodeRole.Definition ? RightHandSide(node.Latex) : node.Latex;
        return _evaluator.Evaluate(new EvaluationRequest(latex, variables));
    }

    /// <summary>Assigns each defined symbol its winner and flags the losers. Winner: topmost by region
    /// y; with no region, first inserted.</summary>
    private static Dictionary<string, SheetNode> ResolveDefinitions(List<SheetNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsConflict = false;
        }

        var owners = new Dictionary<string, SheetNode>();
        foreach (var group in nodes.Where(n => n.DefinedSymbol is not null)
                     .GroupBy(n => n.DefinedSymbol!))
        {
            var ranked = group
                .OrderBy(n => n.Region?.Y ?? double.PositiveInfinity)
                .ThenBy(n => n.InsertionIndex)
                .ToList();

            owners[group.Key] = ranked[0];
            for (var i = 1; i < ranked.Count; i++)
            {
                ranked[i].IsConflict = true;
            }
        }

        return owners;
    }

    private static void RebuildEdges(List<SheetNode> nodes, IReadOnlyDictionary<string, SheetNode> owners)
    {
        foreach (var node in nodes)
        {
            node.DependsOn.Clear();
            node.Dependents.Clear();
        }

        foreach (var node in nodes)
        {
            foreach (var symbol in node.FreeVariables)
            {
                // A self-edge is kept on purpose: "a = a+1" must surface as a cycle of one.
                if (owners.TryGetValue(symbol, out var owner))
                {
                    node.DependsOn.Add(owner.Id);
                }
            }
        }

        foreach (var node in nodes)
        {
            foreach (var dependencyId in node.DependsOn)
            {
                nodes.First(n => n.Id == dependencyId).Dependents.Add(node.Id);
            }
        }
    }

    /// <summary>
    /// Tarjan's strongly-connected components over the dependency edges. A node is a cycle member iff
    /// its SCC has more than one node, or it depends on itself. This is deliberately not "whatever a
    /// topological sort leaves over" — a node merely <em>downstream</em> of a cycle is not in the cycle
    /// and must still evaluate (it just sees no value for the cyclic symbol).
    /// </summary>
    private static HashSet<Guid> FindCycleMembers(List<SheetNode> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id);
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

            foreach (var depId in byId[id].DependsOn)
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

                if (component.Count > 1 || byId[id].DependsOn.Contains(id))
                {
                    cycleIds.UnionWith(component);
                }
            }
        }

        foreach (var node in nodes)
        {
            if (!index.ContainsKey(node.Id))
            {
                Connect(node.Id);
            }
        }

        return cycleIds;
    }

    /// <summary>Kahn's algorithm over the acyclic remainder: a dependency-first order of every node
    /// outside a cycle, treating edges from cycle members as already satisfied.</summary>
    private List<SheetNode> TopologicalOrder(List<SheetNode> nodes, HashSet<Guid> cycleIds)
    {
        var acyclic = nodes.Where(n => !cycleIds.Contains(n.Id)).ToList();
        var inDegree = acyclic.ToDictionary(
            n => n.Id,
            n => n.DependsOn.Count(depId => !cycleIds.Contains(depId)));
        var ready = new Queue<SheetNode>(
            acyclic.Where(n => inDegree[n.Id] == 0).OrderBy(n => n.InsertionIndex));

        var order = new List<SheetNode>(acyclic.Count);
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);
            foreach (var dependentId in node.Dependents.OrderBy(id => _nodes[id].InsertionIndex))
            {
                if (!cycleIds.Contains(dependentId) && --inDegree[dependentId] == 0)
                {
                    ready.Enqueue(_nodes[dependentId]);
                }
            }
        }

        return order;
    }

    /// <summary>The dirty seed set grown along dependent edges (a change ripples downstream).</summary>
    private static HashSet<Guid> ExpandDirty(List<SheetNode> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var dirty = new HashSet<Guid>();
        var stack = new Stack<Guid>(nodes.Where(n => n.Dirty).Select(n => n.Id));

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!dirty.Add(id))
            {
                continue;
            }

            foreach (var dependentId in byId[id].Dependents)
            {
                stack.Push(dependentId);
            }
        }

        return dirty;
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
}
