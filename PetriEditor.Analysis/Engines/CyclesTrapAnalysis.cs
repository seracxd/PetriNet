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
        var stack = new Stack<int>();

        bool Circuit(int v, int s)
        {
            bool found = false;
            stack.Push(v); blocked[v] = true;

            foreach (int w in adj[v])
            {
                if (w == s)
                {
                    var cycle = stack.Reverse().Select(i => nodeIds[i]).ToList();
                    var key = CanonicalCycleKey(cycle);
                    if (uniqueCycleKeys.Add(key))
                        foundCycles.Add(cycle);
                    found = true;
                }
                else if (!blocked[w] && Circuit(w, s)) found = true;
            }

            if (found) Unblock(v);
            else foreach (int w in adj[v]) blockMap[w].Add(v);

            stack.Pop(); return found;
        }

        void Unblock(int v)
        {
            blocked[v] = false;
            foreach (int w in blockMap[v].ToList())
            { blockMap[v].Remove(w); if (blocked[w]) Unblock(w); }
        }

        for (int s = 0; s < n; s++)
        {
            Array.Clear(blocked, 0, n);
            foreach (var b in blockMap) b.Clear();
            Circuit(s, s);
            if (foundCycles.Count > 200) break; // safety cap
        }

        Cycles = foundCycles.Select(c => new PnCycle(c, net)).ToList();
    }

    private static string CanonicalCycleKey(IReadOnlyList<string> cycle)
    {
        if (cycle.Count == 0)
            return string.Empty;

        var best = string.Join("|", cycle);
        for (int shift = 1; shift < cycle.Count; shift++)
        {
            var rotated = string.Join("|", cycle.Skip(shift).Concat(cycle.Take(shift)));
            if (string.CompareOrdinal(rotated, best) < 0)
                best = rotated;
        }

        return best;
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

    public void Compute(Analysis.PetriNetSnapshot net)
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
