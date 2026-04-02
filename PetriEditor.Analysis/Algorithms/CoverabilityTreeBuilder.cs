namespace Analysis.Algorithms;

/// <summary>
/// Builds the Karp-Miller coverability tree for a Petri net.
///
/// The coverability tree handles potentially unbounded nets by replacing
/// unbounded token counts with ω (<c>int.MaxValue</c> sentinel). It always
/// terminates because the ω-introduction rule ensures no infinite ascending
/// chains can form.
///
/// Algorithm (Karp-Miller, 1969):
///  1. Root = initial marking.
///  2. Process each leaf node <c>n</c> with marking <c>m</c> in BFS order:
///     For each enabled transition <c>t</c>:
///       a. Compute <c>m' = Fire(t, m)</c> (omega tokens propagate).
///       b. Walk the ancestor chain from <c>n</c> to the root.
///          For every ancestor <c>a</c> with marking <c>ma</c>:
///          if <c>ma[i] ≤ m'[i]</c> for all i  AND  <c>ma[j] &lt; m'[j]</c> for some j,
///          set <c>m'[j] = ω</c> for every j where <c>ma[j] &lt; m'[j]</c>.
///       c. If <c>m'</c> equals any node already in the tree →
///          create a duplicate leaf (no further expansion).
///       d. Otherwise add a new node and enqueue it.
///  3. Stop when <see cref="MaxNodes"/> is reached.
/// </summary>
public sealed class CoverabilityTreeBuilder
{
    public const int MaxNodes = 100_000;
    public const int Omega    = int.MaxValue;   // ω sentinel

    public bool   HasErrors    { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<CoverTreeNode> Nodes => _nodes;
    public IReadOnlyList<CoverTreeEdge> Edges => _edges;

    private readonly List<CoverTreeNode> _nodes = [];
    private readonly List<CoverTreeEdge> _edges = [];

    public void Build(
        PetriNetSnapshot  net,
        CancellationToken ct = default)
    {
        _nodes.Clear();
        _edges.Clear();
        HasErrors    = false;
        ErrorMessage = null;

        if (!net.Places.Any() || !net.Transitions.Any())
        {
            HasErrors    = true;
            ErrorMessage = "Net has no places or transitions.";
            return;
        }

        var initial = net.Places.Select(p => p.Tokens).ToArray();
        var root    = new CoverTreeNode(0, initial, IsInitial: true, IsDeadlock: false, IsDuplicate: false, ParentId: -1);
        _nodes.Add(root);

        var pIdx = net.Places
            .Select((p, i) => (p.Id, i))
            .ToDictionary(x => x.Id, x => x.i);

        // BFS queue carries nodeId; marking is read from _nodes[nodeId]
        var queue = new Queue<int>();
        queue.Enqueue(0);

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                HasErrors    = true;
                ErrorMessage = "Analysis cancelled.";
                return;
            }

            int parentId    = queue.Dequeue();
            var parentNode  = _nodes[parentId];
            var marking     = parentNode.Marking;
            bool anyFired   = false;

            foreach (var t in net.Transitions)
            {
                if (!FireUtils.IsEnabled(net, pIdx, marking, t.Id))
                    continue;

                anyFired = true;

                // Step a: fire the transition
                var next = FireUtils.Fire(net, pIdx, marking, t.Id);

                // Step b: walk ancestors and promote to omega where needed
                PropagateOmega(next, parentId);

                if (_nodes.Count >= MaxNodes)
                {
                    HasErrors    = true;
                    ErrorMessage = $"Coverability tree exceeded {MaxNodes} nodes.";
                    return;
                }

                // Step c/d: check for duplicate marking anywhere in existing tree
                bool isDup   = FindDuplicate(next, out _);
                int  newId   = _nodes.Count;
                bool isDeadlock = false;

                _nodes.Add(new CoverTreeNode(newId, next,
                    IsInitial:   false,
                    IsDeadlock:  false,
                    IsDuplicate: isDup,
                    ParentId:    parentId));

                _edges.Add(new CoverTreeEdge(parentId, newId, t.Id, t.Name));

                if (!isDup)
                    queue.Enqueue(newId);
            }

            if (!anyFired)
            {
                var existing = _nodes[parentId];
                _nodes[parentId] = existing with { IsDeadlock = true };
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Walk the ancestor chain of <paramref name="parentId"/> and apply
    /// the omega-introduction rule to <paramref name="next"/>.
    /// </summary>
    private void PropagateOmega(int[] next, int parentId)
    {
        int cur = parentId;
        while (cur >= 0)
        {
            var ancestor = _nodes[cur];
            var am       = ancestor.Marking;

            // Check: ancestor ≤ next (component-wise), with at least one strict increase
            bool dominated = true;
            bool strict    = false;
            for (int i = 0; i < am.Length; i++)
            {
                int ai = am[i] == Omega ? Omega : am[i];
                int ni = next[i] == Omega ? Omega : next[i];

                if (ai > ni) { dominated = false; break; }
                if (ai < ni)  strict = true;
            }

            if (dominated && strict)
            {
                // Set omega wherever ancestor is strictly smaller
                for (int i = 0; i < am.Length; i++)
                {
                    int ai = am[i] == Omega ? Omega : am[i];
                    if (ai < next[i])
                        next[i] = Omega;
                }
            }

            cur = ancestor.ParentId;
        }
    }

    /// <summary>
    /// True if an existing node in the tree has the same marking as <paramref name="marking"/>.
    /// </summary>
    private bool FindDuplicate(int[] marking, out int existingId)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (MarkingsEqual(_nodes[i].Marking, marking))
            {
                existingId = i;
                return true;
            }
        }
        existingId = -1;
        return false;
    }

    private static bool MarkingsEqual(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

public sealed record CoverTreeNode(
    int   Id,
    int[] Marking,      // int.MaxValue = ω
    bool  IsInitial,
    bool  IsDeadlock,
    bool  IsDuplicate,
    int   ParentId);

public sealed record CoverTreeEdge(int From, int To, string TransitionId, string TransitionName);
