namespace Analysis.Engines;

/// <summary>
/// Finds all elementary cycles in the Petri net using Johnson's algorithm.
/// The graph includes both places and transitions as nodes.
/// </summary>
public sealed class CyclesAnalysis
{
    public bool HasErrors { get; private set; }
    public string? ErrorMsg { get; private set; }

    public IReadOnlyList<PnCycle> Cycles { get; private set; } = [];

    public void Compute(Analysis.PetriNetSnapshot net)
    {
        HasErrors = false; ErrorMsg = null; Cycles = [];

        var nodeIds = net.Places.Select(p => p.Id)
            .Concat(net.Transitions.Select(t => t.Id)).ToList();
        var idx = nodeIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        int n = nodeIds.Count;

        var adj = Enumerable.Range(0, n).Select(_ => new List<int>()).ToList();
        foreach (var arc in net.Arcs)
            if (idx.TryGetValue(arc.SourceId, out int s) && idx.TryGetValue(arc.TargetId, out int t))
                if (!adj[s].Contains(t))
                    adj[s].Add(t);

        var foundCycles = new List<List<string>>();
        var uniqueCycleKeys = new HashSet<string>(StringComparer.Ordinal);
        var blocked = new bool[n];
        var blockMap = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToList();
        var pathStack = new Stack<int>();

        // Iterative Johnson's elementary-circuits search — explicit frame stack
        // so long chains don't blow the CLR stack. Each frame tracks the node,
        // its current edge index, and whether any descendant found a cycle.
        void RunCircuit(int start)
        {
            var frames = new Stack<Frame>();
            frames.Push(new Frame(start, 0, false));
            pathStack.Push(start);
            blocked[start] = true;

            while (frames.Count > 0)
            {
                var top = frames.Peek();
                var neighbours = adj[top.V];

                if (top.EdgeIdx < neighbours.Count)
                {
                    int w = neighbours[top.EdgeIdx++];
                    if (w == start)
                    {
                        var cycle = pathStack.Reverse().Select(i => nodeIds[i]).ToList();
                        var key = CanonicalCycleKey(cycle);
                        if (uniqueCycleKeys.Add(key))
                            foundCycles.Add(cycle);
                        top.Found = true;
                    }
                    else if (!blocked[w])
                    {
                        frames.Push(new Frame(w, 0, false));
                        pathStack.Push(w);
                        blocked[w] = true;
                    }
                    continue;
                }

                // All neighbours processed — post-visit
                if (top.Found) UnblockIterative(top.V);
                else foreach (int w in neighbours) blockMap[w].Add(top.V);

                pathStack.Pop();
                frames.Pop();
                if (frames.Count > 0 && top.Found) frames.Peek().Found = true;
            }
        }

        void UnblockIterative(int start)
        {
            var queue = new Stack<int>();
            queue.Push(start);
            while (queue.Count > 0)
            {
                int v = queue.Pop();
                if (!blocked[v]) continue;
                blocked[v] = false;
                foreach (int w in blockMap[v])
                    if (blocked[w]) queue.Push(w);
                blockMap[v].Clear();
            }
        }

        for (int s = 0; s < n; s++)
        {
            Array.Clear(blocked, 0, n);
            foreach (var b in blockMap) b.Clear();
            RunCircuit(s);
            if (foundCycles.Count > 200) break; // safety cap
        }

        Cycles = foundCycles.Select(c => new PnCycle(c, net)).ToList();
    }

    // Booth's algorithm: find lexicographically smallest rotation of the cycle in O(n).
    // The cycle is compared element-wise (string IDs), not character-wise across the joined string.
    private static string CanonicalCycleKey(IReadOnlyList<string> cycle)
    {
        int n = cycle.Count;
        if (n == 0) return string.Empty;
        if (n == 1) return cycle[0];

        var f = new int[2 * n];
        Array.Fill(f, -1);
        int k = 0;
        for (int j = 1; j < 2 * n; j++)
        {
            int i = f[j - k - 1];
            while (i != -1 && !string.Equals(cycle[j % n], cycle[(k + i + 1) % n], StringComparison.Ordinal))
            {
                int cmp = string.CompareOrdinal(cycle[j % n], cycle[(k + i + 1) % n]);
                if (cmp < 0) k = j - i - 1;
                i = f[i];
            }
            if (i == -1)
            {
                int cmp = string.CompareOrdinal(cycle[j % n], cycle[(k + i + 1) % n]);
                if (cmp != 0)
                {
                    if (cmp < 0) k = j;
                    f[j - k] = -1;
                }
                else f[j - k] = i + 1;
            }
            else f[j - k] = i + 1;
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(cycle[(k + i) % n]);
        }
        return sb.ToString();
    }

    private sealed class Frame
    {
        public int V;
        public int EdgeIdx;
        public bool Found;
        public Frame(int v, int edgeIdx, bool found) { V = v; EdgeIdx = edgeIdx; Found = found; }
    }
}

public sealed class PnCycle
{
    public IReadOnlyList<string> NodeIds { get; }
    public IReadOnlyList<string> PlaceIds { get; }
    public IReadOnlyList<string> TransitionIds { get; }
    public int TokensInCycle { get; }

    public PnCycle(IEnumerable<string> nodeIds, Analysis.PetriNetSnapshot net)
    {
        NodeIds = nodeIds.ToList();
        PlaceIds = NodeIds.Where(id => net.PlaceById.ContainsKey(id)).ToList();
        TransitionIds = NodeIds.Where(id => net.TransitionById.ContainsKey(id)).ToList();
        TokensInCycle = PlaceIds.Sum(pid => net.PlaceById[pid].Tokens);
    }
}

public static class CyclesAnalysisExtensions
{
    public static int PlaceCoverageCount(this CyclesAnalysis analysis, Analysis.PetriNetSnapshot net)
    {
        var covered = analysis.Cycles.SelectMany(c => c.PlaceIds).Distinct().Count();
        return Math.Min(covered, net.Places.Count);
    }

    public static int TransitionCoverageCount(this CyclesAnalysis analysis, Analysis.PetriNetSnapshot net)
    {
        var covered = analysis.Cycles.SelectMany(c => c.TransitionIds).Distinct().Count();
        return Math.Min(covered, net.Transitions.Count);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Computes minimal traps and minimal co-traps (siphons) by exhaustive
/// subset enumeration. Feasible for nets with up to ~20 places.
///
/// Trap:   post(S) ⊆ pre(S)  — once marked, stays marked
/// Siphon: pre(S) ⊆ post(S)  — once empty, stays empty
/// </summary>
public sealed class TrapCotrapAnalysis
{
    public const int MaxPlaces = 20;

    public bool HasErrors { get; private set; }
    public string? ErrorMsg { get; private set; }

    public IReadOnlyList<PlaceSubset> Traps { get; private set; } = [];
    public IReadOnlyList<PlaceSubset> Cotraps { get; private set; } = [];

    public void Compute(Analysis.PetriNetSnapshot net, CancellationToken ct = default)
    {
        HasErrors = false; ErrorMsg = null;
        Traps = []; Cotraps = [];

        if (!net.Places.Any()) return;

        int n = net.Places.Count;
        if (n > MaxPlaces)
        {
            HasErrors = true;
            ErrorMsg = $"Trap/co-trap enumeration skipped: {n} places exceeds the {MaxPlaces}-place limit.";
            return;
        }

        var placeIds = net.Places.Select(p => p.Id).ToList();
        var traps = new List<PlaceSubset>();
        var cotraps = new List<PlaceSubset>();

        for (int mask = 1; mask < (1 << n); mask++)
        {
            if ((mask & 0x1FFF) == 0) ct.ThrowIfCancellationRequested();

            var subset = new HashSet<string>();
            for (int i = 0; i < n; i++)
                if ((mask & (1 << i)) != 0) subset.Add(placeIds[i]);

            if (IsTrap(subset, net)) traps.Add(new PlaceSubset(subset, net));
            if (IsSiphon(subset, net)) cotraps.Add(new PlaceSubset(subset, net));
        }

        Traps = MinimalSubsets(traps);
        Cotraps = MinimalSubsets(cotraps);
    }

    // ── Structural predicates ─────────────────────────────────────────────

    /// post(S) ⊆ pre(S): every transition that produces into S also consumes from S
    private static bool IsTrap(HashSet<string> S, Analysis.PetriNetSnapshot net)
    {
        foreach (var t in net.Transitions)
        {
            bool producesIntoS = net.OutputArcs(t.Id).Any(a => S.Contains(a.TargetId));
            if (!producesIntoS) continue;
            bool consumesFromS = net.InputArcs(t.Id).Any(a => S.Contains(a.SourceId));
            if (!consumesFromS) return false;
        }
        return true;
    }

    /// pre(S) ⊆ post(S): every transition that consumes from S also produces into S
    private static bool IsSiphon(HashSet<string> S, Analysis.PetriNetSnapshot net)
    {
        foreach (var t in net.Transitions)
        {
            bool consumesFromS = net.InputArcs(t.Id).Any(a => S.Contains(a.SourceId));
            if (!consumesFromS) continue;
            bool producesIntoS = net.OutputArcs(t.Id).Any(a => S.Contains(a.TargetId));
            if (!producesIntoS) return false;
        }
        return true;
    }

    private static List<PlaceSubset> MinimalSubsets(List<PlaceSubset> sets) =>
        sets.Where(s => !sets.Any(other =>
            other != s && other.PlaceIds.IsProperSubsetOf(s.PlaceIds))).ToList();
}

public sealed class PlaceSubset
{
    public IReadOnlySet<string> PlaceIds { get; }
    public bool HasToken { get; }

    public PlaceSubset(IEnumerable<string> placeIds, Analysis.PetriNetSnapshot net)
    {
        PlaceIds = new HashSet<string>(placeIds);
        HasToken = PlaceIds.Any(pid => net.PlaceById.TryGetValue(pid, out var p) && p.Tokens > 0);
    }
}
