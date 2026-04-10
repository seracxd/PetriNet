namespace Analysis.Engines;

/// <summary>
/// Builds the exact reachability graph for bounded nets.
/// If the graph grows beyond <see cref="MaxStates"/>, analysis stops and the
/// result is treated as unavailable / potentially unbounded.
/// </summary>
public sealed class StateSpaceAnalysis
{
    public const int MaxStates = 500_000;

    public bool HasErrors   { get; private set; }
    public bool IsTruncated { get; private set; }
    public string? ErrorMsg { get; private set; }

    private readonly List<int[]> _states = [];
    private readonly Dictionary<int[], int> _stateIdx = new(TokenArrayComparer.Instance);
    private readonly List<List<(int To, string TransId)>> _edges = [];

    public IReadOnlyList<int[]> States => _states;

    /// <summary>
    /// Returns the adjacency list: for each state index, the list of outgoing edges.
    /// Used by <c>AnalysisResultMapper</c> to build the reachability graph DTO.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(int To, string TransId)>> GetEdges() =>
        _edges.Select(e => (IReadOnlyList<(int, string)>)e).ToList();

    // ── Build ─────────────────────────────────────────────────────────────

    public void Build(Analysis.PetriNetSnapshot net, CancellationToken ct = default, int maxStates = MaxStates)
    {
        _states.Clear();
        _stateIdx.Clear();
        _edges.Clear();
        HasErrors   = false;
        IsTruncated = false;
        ErrorMsg    = null;

        if (!net.Places.Any() || !net.Transitions.Any())
        {
            HasErrors = true;
            ErrorMsg = "Net has no places or transitions.";
            return;
        }

        var initial = net.Places.Select(p => p.Tokens).ToArray();
        EnqueueState(initial);

        var queue = new Queue<int>();
        queue.Enqueue(0);

        var pIdx = net.Places
            .Select((p, i) => (p.Id, i))
            .ToDictionary(x => x.Id, x => x.i);

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                HasErrors = true;
                ErrorMsg = "Analysis cancelled.";
                return;
            }

            int sIdx = queue.Dequeue();
            var marking = _states[sIdx];

            var fireable = GetFireable(net, pIdx, marking);
            foreach (var t in fireable)
            {
                if (ct.IsCancellationRequested)
                {
                    HasErrors = true;
                    ErrorMsg = "Analysis cancelled.";
                    return;
                }

                var next = Fire(net, pIdx, marking, t.Id);

                if (!_stateIdx.TryGetValue(next, out int nIdx))
                {
                    if (_states.Count >= maxStates)
                    {
                        IsTruncated = true;
                        ErrorMsg    = $"State space exceeded {maxStates} states — net may be unbounded.";
                        continue;  // skip this edge; don't add the new state
                    }

                    nIdx = EnqueueState(next);
                    queue.Enqueue(nIdx);
                }

                _edges[sIdx].Add((nIdx, t.Id));
            }
        }
    }

    // ── Firing semantics ──────────────────────────────────────────────────

    private static bool IsEnabled(
        Analysis.PetriNetSnapshot net,
        Dictionary<string, int> pIdx,
        int[] marking,
        string tId)
    {
        foreach (var arc in net.InputArcs(tId))
        {
            if (!pIdx.TryGetValue(arc.SourceId, out int pi))
                continue;

            if (arc.ArcType == Analysis.PnArcType.Inhibitor)
            {
                if (marking[pi] != 0)
                    return false;
            }
            else
            {
                if (marking[pi] < arc.Weight)
                    return false;
            }
        }

        return true;
    }

    private static IEnumerable<Analysis.PnTransition> GetFireable(
        Analysis.PetriNetSnapshot net,
        Dictionary<string, int> pIdx,
        int[] marking)
    {
        var enabled = net.Transitions.Where(t => IsEnabled(net, pIdx, marking, t.Id)).ToList();
        if (enabled.Count == 0) return enabled;
        int maxPriority = enabled.Max(t => t.Priority);
        if (maxPriority == 0) return enabled;
        return enabled.Where(t => t.Priority == maxPriority);
    }

    private static int[] Fire(
        Analysis.PetriNetSnapshot net,
        Dictionary<string, int> pIdx,
        int[] marking,
        string tId)
    {
        var next = (int[])marking.Clone();

        foreach (var arc in net.InputArcs(tId))
        {
            if (!pIdx.TryGetValue(arc.SourceId, out int pi))
                continue;

            if (arc.ArcType == Analysis.PnArcType.Inhibitor)
                continue;

            if (arc.ArcType == Analysis.PnArcType.Reset)
            {
                next[pi] = 0;
                continue;
            }

            next[pi] -= arc.Weight;
        }

        foreach (var arc in net.OutputArcs(tId))
        {
            if (pIdx.TryGetValue(arc.TargetId, out int pi))
                next[pi] += arc.Weight;
        }

        return next;
    }

    // ── Graph queries ─────────────────────────────────────────────────────

    public bool IsBounded()     => !HasErrors && !IsTruncated;
    public bool IsDeadlockFree() => !HasErrors && !IsTruncated && Enumerable.Range(0, _states.Count).All(i => _edges[i].Count > 0);
    public bool IsReversible()  => !HasErrors && !IsTruncated && FindSCCs().Count == 1;
    public bool IsSafe()        => !HasErrors && !IsTruncated && _states.All(s => s.All(t => t <= 1));

    public bool IsLive(int transCount)
    {
        if (HasErrors || IsTruncated || transCount == 0)
            return false;

        foreach (var scc in FindSCCs())
        {
            if (!IsFinalSCC(scc))
                continue;

            var labels = new HashSet<string>();
            foreach (int n in scc)
            {
                foreach (var (to, tid) in _edges[n])
                {
                    if (scc.Contains(to))
                        labels.Add(tid);
                }
            }

            if (labels.Count != transCount)
                return false;
        }

        return true;
    }

    // ── Tarjan SCC ────────────────────────────────────────────────────────

    public List<HashSet<int>> FindSCCs()
    {
        int n = _states.Count;
        var index   = new int[n];
        var lowlink = new int[n];
        var onStack = new bool[n];
        Array.Fill(index, -1);

        var tarjanStack = new Stack<int>();
        var sccs        = new List<HashSet<int>>();
        int counter     = 0;

        // Iterative Tarjan — each work-item is (node, edgeIteratorIndex)
        // When edgeIndex == -1 the node is being visited for the first time.
        var workStack = new Stack<(int V, int EdgeIdx)>();

        for (int start = 0; start < n; start++)
        {
            if (index[start] != -1) continue;

            workStack.Push((start, -1));

            while (workStack.Count > 0)
            {
                var (v, ei) = workStack.Pop();

                if (ei == -1)
                {
                    // First visit: initialise
                    index[v] = lowlink[v] = counter++;
                    tarjanStack.Push(v);
                    onStack[v] = true;
                    ei = 0;
                }
                else
                {
                    // Returning from a recursive call on the previous neighbour
                    int prev = _edges[v][ei - 1].To;
                    lowlink[v] = Math.Min(lowlink[v], lowlink[prev]);
                }

                // Process remaining neighbours
                bool pushed = false;
                while (ei < _edges[v].Count)
                {
                    int w = _edges[v][ei].To;
                    ei++;
                    if (index[w] == -1)
                    {
                        // Suspend v, recurse into w
                        workStack.Push((v, ei));
                        workStack.Push((w, -1));
                        pushed = true;
                        break;
                    }
                    if (onStack[w])
                        lowlink[v] = Math.Min(lowlink[v], index[w]);
                }

                if (!pushed && lowlink[v] == index[v])
                {
                    var scc = new HashSet<int>();
                    int w;
                    do
                    {
                        w = tarjanStack.Pop();
                        onStack[w] = false;
                        scc.Add(w);
                    }
                    while (w != v);
                    sccs.Add(scc);
                }
            }
        }

        return sccs;
    }

    private bool IsFinalSCC(HashSet<int> scc)
    {
        foreach (int n in scc)
        {
            foreach (var (to, _) in _edges[n])
            {
                if (!scc.Contains(to))
                    return false;
            }
        }

        return true;
    }

    private int EnqueueState(int[] s)
    {
        int idx = _states.Count;
        _states.Add(s);
        _stateIdx[s] = idx;
        _edges.Add([]);
        return idx;
    }
}

/// <summary>Value-equality comparer for int[] token arrays.</summary>
internal sealed class TokenArrayComparer : IEqualityComparer<int[]>
{
    public static readonly TokenArrayComparer Instance = new();

    public bool Equals(int[]? x, int[]? y)
    {
        if (x is null || y is null)
            return x is null && y is null;

        return x.SequenceEqual(y);
    }

    public int GetHashCode(int[] obj)
    {
        var h = new HashCode();
        foreach (var v in obj)
            h.Add(v);
        return h.ToHashCode();
    }
}
