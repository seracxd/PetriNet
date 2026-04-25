namespace Analysis.Algorithms;

/// <summary>
/// Sugiyama-style layered layout for directed graphs with cycles.
/// The root (initial marking) is always placed on the top layer; every other node
/// sits at its shortest-path distance from the root. Within a layer, node order is
/// chosen by the barycenter heuristic to reduce edge crossings.
/// </summary>
public static class GraphLayoutEngine
{
    public sealed record Result(
        IReadOnlyDictionary<int, int> Layer,
        IReadOnlyDictionary<int, int> Col,
        IReadOnlySet<(int From, int To)> BackEdges);

    private const int BarycenterSweeps = 4;

    public static Result Layout(
        int                        root,
        IReadOnlyCollection<int>   nodeIds,
        IReadOnlyCollection<(int From, int To)> edges)
    {
        if (nodeIds.Count == 0)
            return new(new Dictionary<int, int>(), new Dictionary<int, int>(), new HashSet<(int, int)>());

        var outAdj = nodeIds.ToDictionary(n => n, _ => new List<int>());
        var inAdj  = nodeIds.ToDictionary(n => n, _ => new List<int>());
        foreach (var (f, t) in edges)
        {
            if (!outAdj.ContainsKey(f) || !inAdj.ContainsKey(t)) continue;
            outAdj[f].Add(t);
            inAdj[t].Add(f);
        }

        // ── Layer via BFS from root ───────────────────────────────────────
        var layer = new Dictionary<int, int> { [root] = 0 };
        var q = new Queue<int>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            foreach (int nb in outAdj[cur])
            {
                if (layer.ContainsKey(nb)) continue;
                layer[nb] = layer[cur] + 1;
                q.Enqueue(nb);
            }
        }

        // Nodes unreachable from root: place at layer 0 (shouldn't happen for reachability
        // graphs, but be defensive — coverability can drop nodes when truncated).
        foreach (var n in nodeIds)
            if (!layer.ContainsKey(n)) layer[n] = 0;

        // ── Classify back-edges (target layer ≤ source layer) ─────────────
        var backEdges = new HashSet<(int, int)>();
        foreach (var (f, t) in edges)
        {
            if (!layer.ContainsKey(f) || !layer.ContainsKey(t)) continue;
            if (layer[t] <= layer[f] && f != t) backEdges.Add((f, t));
            else if (f == t) backEdges.Add((f, t)); // self-loop is a back-edge
        }

        // ── Group nodes by layer ──────────────────────────────────────────
        var layers = new Dictionary<int, List<int>>();
        foreach (var (id, lv) in layer)
        {
            if (!layers.TryGetValue(lv, out var lst)) layers[lv] = lst = new();
            lst.Add(id);
        }

        var layerKeys = layers.Keys.OrderBy(k => k).ToList();

        // Initial order: BFS-insertion order is already a reasonable start.
        // Normalize to 0..n-1 per layer.
        var orderInLayer = new Dictionary<int, int>();
        foreach (var lv in layerKeys)
        {
            var lst = layers[lv];
            for (int i = 0; i < lst.Count; i++)
                orderInLayer[lst[i]] = i;
        }

        // ── Barycenter sweeps — use only forward edges for ordering ───────
        // (back-edges would fight the layering and produce oscillating orders)
        // Pre-build per-node forward-predecessor / forward-successor adjacency so the
        // sweep is O(edges) per pass, not O(nodes · edges).
        var fwdIn  = nodeIds.ToDictionary(n => n, _ => new List<int>());
        var fwdOut = nodeIds.ToDictionary(n => n, _ => new List<int>());
        foreach (var e in edges)
        {
            if (backEdges.Contains(e)) continue;
            if (!fwdIn.ContainsKey(e.To) || !fwdOut.ContainsKey(e.From)) continue;
            fwdIn[e.To].Add(e.From);
            fwdOut[e.From].Add(e.To);
        }

        for (int sweep = 0; sweep < BarycenterSweeps; sweep++)
        {
            for (int li = 1; li < layerKeys.Count; li++)
                ReorderByBarycenter(layers[layerKeys[li]], orderInLayer, fwdIn);
            for (int li = layerKeys.Count - 2; li >= 0; li--)
                ReorderByBarycenter(layers[layerKeys[li]], orderInLayer, fwdOut);
        }

        // ── Assign final columns, centered per layer ──────────────────────
        int maxWidth = layers.Values.Max(l => l.Count);
        var col = new Dictionary<int, int>();
        foreach (var lv in layerKeys)
        {
            var lst = layers[lv];
            // Center the layer horizontally so M0 (solo root) sits near the centerline
            int offset = (maxWidth - lst.Count) / 2;
            for (int i = 0; i < lst.Count; i++)
                col[lst[i]] = offset + i;
        }

        return new Result(layer, col, backEdges);
    }

    private static void ReorderByBarycenter(
        List<int>                  layerNodes,
        Dictionary<int, int>       orderInLayer,
        Dictionary<int, List<int>> neighboursOf)
    {
        var bc = new Dictionary<int, double>();
        foreach (int n in layerNodes)
        {
            var nbs = neighboursOf[n];
            if (nbs.Count == 0) { bc[n] = orderInLayer[n]; continue; }
            double sum = 0; int count = 0;
            for (int i = 0; i < nbs.Count; i++)
            {
                if (orderInLayer.TryGetValue(nbs[i], out int o)) { sum += o; count++; }
            }
            bc[n] = count == 0 ? orderInLayer[n] : sum / count;
        }

        layerNodes.Sort((a, b) =>
        {
            int cmp = bc[a].CompareTo(bc[b]);
            return cmp != 0 ? cmp : orderInLayer[a].CompareTo(orderInLayer[b]);
        });

        for (int i = 0; i < layerNodes.Count; i++)
            orderInLayer[layerNodes[i]] = i;
    }
}
