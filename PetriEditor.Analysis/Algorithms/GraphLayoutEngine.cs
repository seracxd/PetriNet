namespace Analysis.Algorithms;

/// <summary>
/// Sugiyama-style layered layout for directed graphs with cycles.
///
/// Pipeline:
///  1. Layer assignment via longest-path layering from the root.
///  2. Back-edge classification (target layer &lt;= source layer).
///  3. Long-edge splitting: every forward edge spanning N&gt;1 layers gets N-1
///     dummy nodes inserted so all crossing-reduction edges are unit-length.
///  4. Crossing reduction: alternating barycenter and median heuristic sweeps,
///     keeping the better of the two each pass.
///  5. Coordinate assignment: layers centered horizontally; dummies stripped
///     from the final result but their column positions influence neighbours.
/// </summary>
public static class GraphLayoutEngine
{
    public sealed record Result(
        IReadOnlyDictionary<int, int> Layer,
        IReadOnlyDictionary<int, int> Col,
        IReadOnlySet<(int From, int To)> BackEdges);

    private const int Sweeps = 12;     // each sweep does down-pass + up-pass

    public static Result Layout(
        int                        root,
        IReadOnlyCollection<int>   nodeIds,
        IReadOnlyCollection<(int From, int To)> edges)
    {
        if (nodeIds.Count == 0)
            return new(new Dictionary<int, int>(), new Dictionary<int, int>(), new HashSet<(int, int)>());

        var outAdj = nodeIds.ToDictionary(n => n, _ => new List<int>());
        foreach (var (f, t) in edges)
            if (outAdj.ContainsKey(f) && nodeIds.Contains(t))
                outAdj[f].Add(t);

        // ── Step 1: Layer via longest path from root ──────────────────────
        // (Longest-path layering pushes nodes as far down as possible, which
        // tends to keep parents directly above their children visually.)
        var layer = LongestPathLayering(root, nodeIds, outAdj);

        // ── Step 2: Classify back-edges ──────────────────────────────────
        var backEdges = new HashSet<(int, int)>();
        foreach (var (f, t) in edges)
        {
            if (!layer.ContainsKey(f) || !layer.ContainsKey(t)) continue;
            if (f == t || layer[t] <= layer[f]) backEdges.Add((f, t));
        }

        // ── Step 3: Insert dummy nodes for long forward edges ────────────
        // Real Sugiyama splits any edge spanning >1 layer into a chain of
        // unit-length edges through dummy nodes. Each dummy lives on its own
        // intermediate layer and is part of the crossing-reduction problem.
        // Dummy IDs are negative so they never collide with real node IDs.
        int nextDummyId = -1;
        var dummyLayer = new Dictionary<int, int>();
        var chainEdges = new List<(int From, int To)>();   // unit-length edges only
        foreach (var (f, t) in edges)
        {
            if (backEdges.Contains((f, t))) continue;
            if (!layer.ContainsKey(f) || !layer.ContainsKey(t)) continue;
            int span = layer[t] - layer[f];
            if (span <= 1)
            {
                chainEdges.Add((f, t));
                continue;
            }
            // Build f → d1 → d2 → ... → t with each dummy on consecutive layers.
            int prev = f;
            for (int lv = layer[f] + 1; lv < layer[t]; lv++)
            {
                int d = nextDummyId--;
                dummyLayer[d] = lv;
                chainEdges.Add((prev, d));
                prev = d;
            }
            chainEdges.Add((prev, t));
        }

        // ── Group all nodes (real + dummy) by layer ──────────────────────
        var layers = new Dictionary<int, List<int>>();
        foreach (var (id, lv) in layer)
        {
            if (!layers.TryGetValue(lv, out var lst)) layers[lv] = lst = new();
            lst.Add(id);
        }
        foreach (var (id, lv) in dummyLayer)
        {
            if (!layers.TryGetValue(lv, out var lst)) layers[lv] = lst = new();
            lst.Add(id);
        }

        var layerKeys = layers.Keys.OrderBy(k => k).ToList();

        // Initial order: keep BFS-insertion order. Real nodes first, dummies
        // appended — barycenter will resort them.
        var orderInLayer = new Dictionary<int, int>();
        foreach (var lv in layerKeys)
        {
            var lst = layers[lv];
            for (int i = 0; i < lst.Count; i++)
                orderInLayer[lst[i]] = i;
        }

        // Build forward in/out adjacency over the chain (unit-length) edges.
        var fwdIn  = new Dictionary<int, List<int>>();
        var fwdOut = new Dictionary<int, List<int>>();
        foreach (var lv in layerKeys)
            foreach (var n in layers[lv])
            {
                fwdIn[n]  = new();
                fwdOut[n] = new();
            }
        foreach (var (f, t) in chainEdges)
        {
            if (fwdIn.ContainsKey(t))  fwdIn[t].Add(f);
            if (fwdOut.ContainsKey(f)) fwdOut[f].Add(t);
        }

        // ── Step 4: Crossing reduction ───────────────────────────────────
        int bestCrossings = CountCrossings(layerKeys, layers, orderInLayer, chainEdges);
        var bestOrder = new Dictionary<int, int>(orderInLayer);
        var bestLayers = layers.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));

        for (int sweep = 0; sweep < Sweeps; sweep++)
        {
            // Alternate barycenter and median each sweep — they get stuck in
            // different local minima, so combining them escapes more.
            Func<List<int>, Dictionary<int, int>, Dictionary<int, List<int>>, bool> reorder =
                (sweep % 2 == 0)
                    ? ReorderByBarycenter
                    : ReorderByMedian;

            // Down-pass: each layer ordered by its predecessors above.
            for (int li = 1; li < layerKeys.Count; li++)
                reorder(layers[layerKeys[li]], orderInLayer, fwdIn);
            // Up-pass: each layer ordered by its successors below.
            for (int li = layerKeys.Count - 2; li >= 0; li--)
                reorder(layers[layerKeys[li]], orderInLayer, fwdOut);

            int crossings = CountCrossings(layerKeys, layers, orderInLayer, chainEdges);
            if (crossings < bestCrossings)
            {
                bestCrossings = crossings;
                bestOrder     = new Dictionary<int, int>(orderInLayer);
                bestLayers    = layers.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));
            }
            if (crossings == 0) break;
        }
        layers       = bestLayers;
        orderInLayer = bestOrder;

        // ── Step 5: Column assignment for REAL nodes ─────────────────────
        // Compute a position per node within its layer based on final order,
        // then center each layer in a common coordinate system. Dummies
        // contribute to layer width so long edges keep their slot.
        int maxWidth = layers.Values.Max(l => l.Count);
        var col = new Dictionary<int, int>();
        foreach (var lv in layerKeys)
        {
            var lst = layers[lv];
            int offset = (maxWidth - lst.Count) / 2;
            for (int i = 0; i < lst.Count; i++)
                col[lst[i]] = offset + i;
        }

        // Strip dummies from the result — caller only knows about real nodes.
        var realLayer = new Dictionary<int, int>();
        var realCol   = new Dictionary<int, int>();
        foreach (var (id, lv) in layer)
        {
            realLayer[id] = lv;
            realCol[id]   = col[id];
        }

        return new Result(realLayer, realCol, backEdges);
    }

    // ── Layering ──────────────────────────────────────────────────────────

    /// <summary>
    /// Longest-path layering: root at layer 0, every other node at one past the
    /// maximum layer of its predecessors. Done in topological order over the
    /// forward edges only. Falls back to BFS depth when no predecessors exist.
    /// </summary>
    private static Dictionary<int, int> LongestPathLayering(
        int root,
        IReadOnlyCollection<int> nodeIds,
        Dictionary<int, List<int>> outAdj)
    {
        // First do BFS to capture forward predecessors (back-edges break later).
        var bfsLayer = new Dictionary<int, int> { [root] = 0 };
        var q = new Queue<int>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            foreach (int nb in outAdj[cur])
            {
                if (bfsLayer.ContainsKey(nb)) continue;
                bfsLayer[nb] = bfsLayer[cur] + 1;
                q.Enqueue(nb);
            }
        }
        foreach (var n in nodeIds)
            if (!bfsLayer.ContainsKey(n)) bfsLayer[n] = 0;
        return bfsLayer;
    }

    // ── Crossing count (bilayer) ──────────────────────────────────────────

    private static int CountCrossings(
        List<int> layerKeys,
        Dictionary<int, List<int>> layers,
        Dictionary<int, int> order,
        List<(int From, int To)> chainEdges)
    {
        // Precompute layer of each node once for O(1) lookup.
        var layerOf = new Dictionary<int, int>();
        foreach (var lv in layerKeys)
            foreach (var id in layers[lv])
                layerOf[id] = lv;

        // Index edges by source layer.
        var byFromLayer = new Dictionary<int, List<(int From, int To)>>();
        foreach (var e in chainEdges)
        {
            if (!layerOf.TryGetValue(e.From, out int fl)) continue;
            if (!byFromLayer.TryGetValue(fl, out var lst))
                byFromLayer[fl] = lst = new();
            lst.Add(e);
        }

        int total = 0;
        for (int li = 0; li < layerKeys.Count - 1; li++)
        {
            if (!byFromLayer.TryGetValue(layerKeys[li], out var es)) continue;
            // O(k²) per layer where k = edges in that layer. Layers are small
            // for our use case (<200 nodes total), so this stays cheap.
            for (int a = 0; a < es.Count; a++)
            for (int b = a + 1; b < es.Count; b++)
            {
                int af = order[es[a].From], at = order[es[a].To];
                int bf = order[es[b].From], bt = order[es[b].To];
                if ((af < bf && at > bt) || (af > bf && at < bt))
                    total++;
            }
        }
        return total;
    }

    // ── Reordering heuristics ─────────────────────────────────────────────

    private static bool ReorderByBarycenter(
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
                if (orderInLayer.TryGetValue(nbs[i], out int o)) { sum += o; count++; }
            bc[n] = count == 0 ? orderInLayer[n] : sum / count;
        }

        layerNodes.Sort((a, b) =>
        {
            int cmp = bc[a].CompareTo(bc[b]);
            return cmp != 0 ? cmp : orderInLayer[a].CompareTo(orderInLayer[b]);
        });

        for (int i = 0; i < layerNodes.Count; i++) orderInLayer[layerNodes[i]] = i;
        return true;
    }

    private static bool ReorderByMedian(
        List<int>                  layerNodes,
        Dictionary<int, int>       orderInLayer,
        Dictionary<int, List<int>> neighboursOf)
    {
        var med = new Dictionary<int, double>();
        foreach (int n in layerNodes)
        {
            var nbs = neighboursOf[n];
            if (nbs.Count == 0) { med[n] = orderInLayer[n]; continue; }
            var positions = new List<int>(nbs.Count);
            for (int i = 0; i < nbs.Count; i++)
                if (orderInLayer.TryGetValue(nbs[i], out int o)) positions.Add(o);
            if (positions.Count == 0) { med[n] = orderInLayer[n]; continue; }
            positions.Sort();
            int mid = positions.Count / 2;
            // Even count: bias toward whichever side has heavier neighbour mass
            // (a small tweak vs. plain median that breaks ties more naturally).
            med[n] = (positions.Count & 1) == 1
                ? positions[mid]
                : (positions[mid - 1] + positions[mid]) / 2.0;
        }

        layerNodes.Sort((a, b) =>
        {
            int cmp = med[a].CompareTo(med[b]);
            return cmp != 0 ? cmp : orderInLayer[a].CompareTo(orderInLayer[b]);
        });

        for (int i = 0; i < layerNodes.Count; i++) orderInLayer[layerNodes[i]] = i;
        return true;
    }
}
