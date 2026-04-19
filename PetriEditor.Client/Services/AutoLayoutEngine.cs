using Blazor.Diagrams.Core.Geometry;

namespace PetriEditor.Client.Services;

public static class AutoLayoutEngine
{
    private const double HSpacing = 180.0;
    private const double VSpacing  = 90.0;

    // ── Hierarchical (Sugiyama + barycenter + GA) ─────────────────────────────

    public static Dictionary<string, Point> Hierarchical(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, (double W, double H)> sizes)
    {
        if (nodeIds.Count == 0) return [];

        var outE = nodeIds.ToDictionary(id => id, _ => new List<string>());
        var inE  = nodeIds.ToDictionary(id => id, _ => new List<string>());
        foreach (var (f, t) in edges)
        {
            if (!outE.ContainsKey(f) || !outE.ContainsKey(t)) continue;
            outE[f].Add(t); inE[t].Add(f);
        }

        // ── cycle removal (iterative DFS back-edges) ──────────────────────────
        var reversed = new HashSet<(string, string)>();
        var st = nodeIds.ToDictionary(id => id, _ => 0);
        foreach (var start in nodeIds)
        {
            if (st[start] != 0) continue;
            var stk = new Stack<(string, int)>();
            stk.Push((start, 0)); st[start] = 1;
            while (stk.Count > 0)
            {
                var (u, ci) = stk.Peek();
                if (ci < outE[u].Count)
                {
                    stk.Pop(); stk.Push((u, ci + 1));
                    var v = outE[u][ci];
                    if (st[v] == 1) reversed.Add((u, v));
                    else if (st[v] == 0) { st[v] = 1; stk.Push((v, 0)); }
                }
                else { stk.Pop(); st[u] = 2; }
            }
        }

        // ── DAG edges ─────────────────────────────────────────────────────────
        var dagOut = nodeIds.ToDictionary(id => id, _ => new List<string>());
        var dagIn  = nodeIds.ToDictionary(id => id, _ => new List<string>());
        foreach (var (f, t) in edges)
        {
            if (!dagOut.ContainsKey(f) || !dagOut.ContainsKey(t)) continue;
            var (from, to) = reversed.Contains((f, t)) ? (t, f) : (f, t);
            if (!dagOut[from].Contains(to)) { dagOut[from].Add(to); dagIn[to].Add(from); }
        }

        // ── longest-path layering ─────────────────────────────────────────────
        var layer = new Dictionary<string, int>(nodeIds.Count);
        var inDeg = nodeIds.ToDictionary(id => id, id => dagIn[id].Count);
        var q = new Queue<string>(nodeIds.Where(id => inDeg[id] == 0));
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            int ul = layer.GetValueOrDefault(u, 0);
            foreach (var v in dagOut[u])
            {
                if (!layer.TryGetValue(v, out int vl) || vl < ul + 1) layer[v] = ul + 1;
                if (--inDeg[v] == 0) q.Enqueue(v);
            }
        }
        foreach (var id in nodeIds) layer.TryAdd(id, 0);

        // ── barycenter crossing reduction (10 passes) ─────────────────────────
        var byLayer = nodeIds.GroupBy(id => layer[id]).OrderBy(g => g.Key)
                             .Select(g => g.ToList()).ToList();

        static double Bary(string id, List<string> nbrs, Dictionary<string, int> pos)
        {
            var hits = nbrs.Where(pos.ContainsKey).ToList();
            return hits.Count == 0 ? -1 : hits.Average(n => pos[n]);
        }

        for (int pass = 0; pass < 10; pass++)
        {
            for (int li = 1; li < byLayer.Count; li++)
            {
                var pp = byLayer[li - 1].Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
                byLayer[li] = [.. byLayer[li]
                    .Select(id => (id, b: Bary(id, dagIn[id], pp)))
                    .OrderBy(x => x.b < 0 ? double.MaxValue : x.b)
                    .Select(x => x.id)];
            }
            for (int li = byLayer.Count - 2; li >= 0; li--)
            {
                var np = byLayer[li + 1].Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
                byLayer[li] = [.. byLayer[li]
                    .Select(id => (id, b: Bary(id, dagOut[id], np)))
                    .OrderBy(x => x.b < 0 ? double.MaxValue : x.b)
                    .Select(x => x.id)];
            }
        }

        // ── GA refinement per layer ────────────────────────────────────────────
        var rng = new Random(42);
        byLayer = byLayer.Select(lyr => GaRefineLayer(lyr, byLayer, dagOut, dagIn, rng)).ToList();

        // ── assign coordinates ─────────────────────────────────────────────────
        var result = new Dictionary<string, Point>();
        double x = 0;
        foreach (var lyr in byLayer)
        {
            double maxW   = lyr.Max(id => sizes.TryGetValue(id, out var s) ? s.W : 60);
            double totalH = lyr.Sum(id => sizes.TryGetValue(id, out var s) ? s.H : 60)
                          + VSpacing * (lyr.Count - 1);
            double y = -totalH / 2;
            foreach (var id in lyr)
            {
                var (w, h) = sizes.TryGetValue(id, out var sz) ? sz : (60.0, 60.0);
                result[id] = new Point(x + (maxW - w) / 2, y);
                y += h + VSpacing;
            }
            x += maxW + HSpacing;
        }
        return result;
    }

    /// <summary>Returns all logically reversed edges that need waypoints above the layout.</summary>
    public static HashSet<(string, string)> GetBackEdges(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<(string From, string To)> edges)
    {
        var outE = nodeIds.ToDictionary(id => id, _ => new List<string>());
        foreach (var (f, t) in edges)
            if (outE.ContainsKey(f) && outE.ContainsKey(t)) outE[f].Add(t);

        var reversed = new HashSet<(string, string)>();
        var st = nodeIds.ToDictionary(id => id, _ => 0);
        foreach (var start in nodeIds)
        {
            if (st[start] != 0) continue;
            var stk = new Stack<(string, int)>();
            stk.Push((start, 0)); st[start] = 1;
            while (stk.Count > 0)
            {
                var (u, ci) = stk.Peek();
                if (ci < outE[u].Count)
                {
                    stk.Pop(); stk.Push((u, ci + 1));
                    var v = outE[u][ci];
                    if (st[v] == 1) reversed.Add((u, v));
                    else if (st[v] == 0) { st[v] = 1; stk.Push((v, 0)); }
                }
                else { stk.Pop(); st[u] = 2; }
            }
        }
        return reversed;
    }

    // ── GA refinement ─────────────────────────────────────────────────────────

    private static List<string> GaRefineLayer(
        List<string> layer, List<List<string>> allLayers,
        Dictionary<string, List<string>> dagOut, Dictionary<string, List<string>> dagIn,
        Random rng)
    {
        if (layer.Count <= 2) return layer;
        int layerIdx = allLayers.IndexOf(layer);
        var prevPos = layerIdx > 0
            ? allLayers[layerIdx - 1].Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i)
            : (Dictionary<string, int>)[];
        var nextPos = layerIdx < allLayers.Count - 1
            ? allLayers[layerIdx + 1].Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i)
            : (Dictionary<string, int>)[];

        int CrossCount(List<string> order)
        {
            var pos = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            int count = 0;
            for (int i = 0; i < order.Count; i++)
            for (int j = i + 1; j < order.Count; j++)
            {
                int pi = pos[order[i]], pj = pos[order[j]];
                foreach (var ni in dagOut[order[i]].Where(nextPos.ContainsKey))
                foreach (var nj in dagOut[order[j]].Where(nextPos.ContainsKey))
                    if ((pi < pj) != (nextPos[ni] < nextPos[nj])) count++;
                foreach (var ni in dagIn[order[i]].Where(prevPos.ContainsKey))
                foreach (var nj in dagIn[order[j]].Where(prevPos.ContainsKey))
                    if ((pi < pj) != (prevPos[ni] < prevPos[nj])) count++;
            }
            return count;
        }

        const int PopSize = 12, Gens = 40;
        var pop = new List<List<string>>(PopSize) { new(layer) };
        for (int i = 1; i < PopSize; i++)
        {
            var p = new List<string>(layer);
            for (int j = p.Count - 1; j > 0; j--) { int k = rng.Next(j + 1); (p[j], p[k]) = (p[k], p[j]); }
            pop.Add(p);
        }
        var fitness = pop.Select(CrossCount).ToList();

        for (int gen = 0; gen < Gens; gen++)
        {
            int ai = rng.Next(PopSize), bi = rng.Next(PopSize);
            var child = new List<string>(pop[fitness[ai] <= fitness[bi] ? ai : bi]);
            for (int s = 0; s < 1 + rng.Next(3); s++)
                (child[rng.Next(child.Count)], child[rng.Next(child.Count)]) = (child[rng.Next(child.Count)], child[rng.Next(child.Count)]);
            int cf = CrossCount(child);
            int worst = fitness.Select((f, i) => (f, i)).MaxBy(x => x.f).i;
            if (cf <= fitness[worst]) { pop[worst] = child; fitness[worst] = cf; }
        }
        return pop[fitness.Select((f, i) => (f, i)).MinBy(x => x.f).i];
    }

    // ── Force-Directed with crossing penalty ──────────────────────────────────

    public static Dictionary<string, Point> ForceDirected(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, (double W, double H)> sizes)
    {
        const int Iterations  = 350;
        const double K        = 150.0;
        const double Temp0    = 500.0;
        const double Gravity  = 0.03;
        const double K_Cross  = 600.0;   // crossing penalty constant

        var rng = new Random(42);
        // pos = node CENTER
        var pos  = nodeIds.ToDictionary(id => id,
            _ => new[] { rng.NextDouble() * 800 - 400, rng.NextDouble() * 600 - 300 });
        var disp = nodeIds.ToDictionary(id => id, _ => new double[2]);

        // pre-compute sizes (fallback 60×60)
        var sz = nodeIds.ToDictionary(id => id,
            id => sizes.TryGetValue(id, out var s) ? s : (60.0, 60.0));

        // unique, valid edges
        var validEdges = edges
            .Where(e => pos.ContainsKey(e.From) && pos.ContainsKey(e.To) && e.From != e.To)
            .Distinct().ToList();

        for (int iter = 0; iter < Iterations; iter++)
        {
            double temp = Temp0 * Math.Pow(1.0 - (double)iter / Iterations, 1.5);
            foreach (var v in nodeIds) { disp[v][0] = 0; disp[v][1] = 0; }

            // ── repulsion with overlap penalty ────────────────────────────────
            for (int i = 0; i < nodeIds.Count; i++)
            for (int j = i + 1; j < nodeIds.Count; j++)
            {
                var u = nodeIds[i]; var v = nodeIds[j];
                double dx = pos[v][0] - pos[u][0];
                double dy = pos[v][1] - pos[u][1];
                double dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1.0);
                // minimum distance between centres (half-widths + padding)
                double minD = (sz[u].Item1 + sz[v].Item1) / 2 + (sz[u].Item2 + sz[v].Item2) / 2 + 20;
                double rep = K * K / dist;
                if (dist < minD) rep += 6 * K * K / dist;  // strong overlap push
                disp[u][0] -= rep * dx / dist; disp[u][1] -= rep * dy / dist;
                disp[v][0] += rep * dx / dist; disp[v][1] += rep * dy / dist;
            }

            // ── attraction ────────────────────────────────────────────────────
            foreach (var (f, t) in validEdges)
            {
                double dx = pos[t][0] - pos[f][0];
                double dy = pos[t][1] - pos[f][1];
                double dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1.0);
                double att = dist * dist / K;
                double fx = att * dx / dist, fy = att * dy / dist;
                disp[f][0] += fx; disp[f][1] += fy;
                disp[t][0] -= fx; disp[t][1] -= fy;
            }

            // ── crossing penalty (every iteration) ───────────────────────────
            for (int i = 0; i < validEdges.Count; i++)
            for (int j = i + 1; j < validEdges.Count; j++)
            {
                var (f1, t1) = validEdges[i];
                var (f2, t2) = validEdges[j];
                if (f1 == f2 || f1 == t2 || t1 == f2 || t1 == t2) continue;

                // arc centres (node centres)
                double ax = pos[f1][0], ay = pos[f1][1];
                double bx = pos[t1][0], by = pos[t1][1];
                double cx = pos[f2][0], cy = pos[f2][1];
                double dx = pos[t2][0], dy = pos[t2][1];

                if (!SegmentsIntersect(ax, ay, bx, by, cx, cy, dx, dy)) continue;

                // push midpoints of crossing arcs apart
                double mx1 = (ax + bx) * 0.5, my1 = (ay + by) * 0.5;
                double mx2 = (cx + dx) * 0.5, my2 = (cy + dy) * 0.5;
                double mdx = mx1 - mx2, mdy = my1 - my2;
                double mdist = Math.Max(Math.Sqrt(mdx * mdx + mdy * mdy), 1.0);
                double force = K_Cross / mdist;
                double fx = force * mdx / mdist, fy = force * mdy / mdist;
                disp[f1][0] += fx; disp[f1][1] += fy;
                disp[t1][0] += fx; disp[t1][1] += fy;
                disp[f2][0] -= fx; disp[f2][1] -= fy;
                disp[t2][0] -= fx; disp[t2][1] -= fy;
            }

            // ── gravity ───────────────────────────────────────────────────────
            foreach (var v in nodeIds)
            {
                disp[v][0] -= Gravity * pos[v][0];
                disp[v][1] -= Gravity * pos[v][1];
            }

            // ── apply with cooling ────────────────────────────────────────────
            foreach (var v in nodeIds)
            {
                double dx = disp[v][0], dy = disp[v][1];
                double len = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1.0);
                double move = Math.Min(len, temp);
                pos[v][0] += move * dx / len;
                pos[v][1] += move * dy / len;
            }
        }

        // center result; output is top-left (pos is centre, subtract half-size)
        double cx0 = pos.Values.Average(p => p[0]);
        double cy0 = pos.Values.Average(p => p[1]);
        return nodeIds.ToDictionary(id => id,
            id => new Point(pos[id][0] - cx0 - sz[id].Item1 / 2,
                            pos[id][1] - cy0 - sz[id].Item2 / 2));
    }

    // ── Bipartite circular (places inner, transitions outer) ──────────────────

    public static Dictionary<string, Point> Bipartite(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlySet<string> placeIds)
    {
        var places = nodeIds.Where(placeIds.Contains).ToList();
        var trans  = nodeIds.Where(id => !placeIds.Contains(id)).ToList();

        if (places.Count == 0 || trans.Count == 0)
            return Circular(nodeIds, edges, sizes);

        // undirected adjacency for barycenter
        var adj = nodeIds.ToDictionary(id => id, _ => new List<string>());
        foreach (var (f, t) in edges)
        {
            if (adj.ContainsKey(f)) adj[f].Add(t);
            if (adj.ContainsKey(t)) adj[t].Add(f);
        }

        // ring radii
        double maxP = places.Max(id => sizes.TryGetValue(id, out var s) ? Math.Max(s.W, s.H) : 60);
        double maxT = trans.Max(id  => sizes.TryGetValue(id, out var s) ? Math.Max(s.W, s.H) : 20);
        double rP = Math.Max(maxP * places.Count / (2 * Math.PI) + 50, 100);
        double rT = Math.Max(maxT * trans.Count  / (2 * Math.PI) + 50, rP + 120);

        var po = new List<string>(places);
        var to = new List<string>(trans);

        // barycenter crossing reduction (10 passes)
        for (int pass = 0; pass < 10; pass++)
        {
            var pa = po.Select((id, i) => (id, a: 2 * Math.PI * i / po.Count))
                       .ToDictionary(x => x.id, x => x.a);
            to = [.. to.Select(id => {
                    var nb = adj[id].Where(pa.ContainsKey).ToList();
                    return (id, b: nb.Count == 0 ? 0.0 : nb.Average(n => pa[n]));
                }).OrderBy(x => x.b).Select(x => x.id)];

            var ta = to.Select((id, i) => (id, a: 2 * Math.PI * i / to.Count))
                       .ToDictionary(x => x.id, x => x.a);
            po = [.. po.Select(id => {
                    var nb = adj[id].Where(ta.ContainsKey).ToList();
                    return (id, b: nb.Count == 0 ? 0.0 : nb.Average(n => ta[n]));
                }).OrderBy(x => x.b).Select(x => x.id)];
        }

        var result = new Dictionary<string, Point>();
        for (int i = 0; i < po.Count; i++)
        {
            double a = 2 * Math.PI * i / po.Count - Math.PI / 2;
            var (w, h) = sizes.TryGetValue(po[i], out var s) ? s : (60.0, 60.0);
            result[po[i]] = new Point(rP * Math.Cos(a) - w / 2, rP * Math.Sin(a) - h / 2);
        }
        for (int i = 0; i < to.Count; i++)
        {
            double a = 2 * Math.PI * i / to.Count - Math.PI / 2;
            var (w, h) = sizes.TryGetValue(to[i], out var s) ? s : (60.0, 60.0);
            result[to[i]] = new Point(rT * Math.Cos(a) - w / 2, rT * Math.Sin(a) - h / 2);
        }
        return result;
    }

    // ── Circular fallback (BFS-ordered) ───────────────────────────────────────

    private static Dictionary<string, Point> Circular(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, (double W, double H)> sizes)
    {
        int n = nodeIds.Count;
        if (n == 0) return [];
        var deg = nodeIds.ToDictionary(id => id, _ => 0);
        var adj = nodeIds.ToDictionary(id => id, _ => new HashSet<string>());
        foreach (var (f, t) in edges)
        {
            if (adj.ContainsKey(f)) { adj[f].Add(t); deg[f]++; }
            if (adj.ContainsKey(t)) { adj[t].Add(f); deg[t]++; }
        }
        var ordered = new List<string>(); var seen = new HashSet<string>();
        var bfsQ = new Queue<string>();
        var s0 = nodeIds.MaxBy(id => deg[id])!;
        bfsQ.Enqueue(s0); seen.Add(s0);
        while (bfsQ.Count > 0)
        {
            var u = bfsQ.Dequeue(); ordered.Add(u);
            foreach (var v in adj[u].Where(v => seen.Add(v))) bfsQ.Enqueue(v);
        }
        foreach (var id in nodeIds.Where(id => !seen.Contains(id))) ordered.Add(id);
        double maxD = nodeIds.Max(id => { var (w, h) = sizes.TryGetValue(id, out var s) ? s : (60.0, 60.0); return Math.Max(w, h); });
        double radius = Math.Max(maxD * n / (2 * Math.PI) + 80, 120);
        var result = new Dictionary<string, Point>();
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n - Math.PI / 2;
            var (w, h) = sizes.TryGetValue(ordered[i], out var s2) ? s2 : (60.0, 60.0);
            result[ordered[i]] = new Point(radius * Math.Cos(a) - w / 2, radius * Math.Sin(a) - h / 2);
        }
        return result;
    }

    // ── Radial (BFS rings from highest-degree source) ─────────────────────────

    public static Dictionary<string, Point> Radial(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, (double W, double H)> sizes)
    {
        if (nodeIds.Count == 0) return [];

        var adj   = nodeIds.ToDictionary(id => id, _ => new HashSet<string>());
        var inDeg = nodeIds.ToDictionary(id => id, _ => 0);
        foreach (var (f, t) in edges)
        {
            if (!adj.ContainsKey(f) || !adj.ContainsKey(t)) continue;
            adj[f].Add(t); adj[t].Add(f);
            inDeg[t]++;
        }

        var sources = nodeIds.Where(id => inDeg[id] == 0).ToList();
        string root = sources.Count > 0
            ? sources.MaxBy(id => adj[id].Count)!
            : nodeIds.MaxBy(id => adj[id].Count)!;

        var depth = new Dictionary<string, int> { [root] = 0 };
        var q = new Queue<string>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            foreach (var v in adj[u])
                if (!depth.ContainsKey(v)) { depth[v] = depth[u] + 1; q.Enqueue(v); }
        }
        int maxD = depth.Values.DefaultIfEmpty(0).Max();
        foreach (var id in nodeIds)
            if (!depth.ContainsKey(id)) depth[id] = maxD + 1;

        var rings = depth.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                         .Select(g => g.Select(kv => kv.Key).ToList()).ToList();

        var radii = new List<double>();
        double r = 0;
        for (int li = 0; li < rings.Count; li++)
        {
            var ring = rings[li];
            if (li == 0 && ring.Count == 1) { radii.Add(0); continue; }

            double maxDim = ring.Max(id => {
                var (w, h) = sizes.TryGetValue(id, out var s) ? s : (60.0, 60.0);
                return Math.Max(w, h);
            });
            double circSpace = ring.Count * (maxDim + 40);
            r = Math.Max(r + maxDim + 90, circSpace / (2 * Math.PI));
            radii.Add(r);
        }

        var angle = new Dictionary<string, double>();
        for (int li = 0; li < rings.Count; li++)
        {
            var ring = rings[li];
            if (li == 0)
            {
                if (ring.Count == 1) angle[ring[0]] = -Math.PI / 2;
                else for (int i = 0; i < ring.Count; i++)
                    angle[ring[i]] = 2 * Math.PI * i / ring.Count - Math.PI / 2;
                continue;
            }

            var ordered = ring.Select(id => {
                var parents = adj[id].Where(p => depth.ContainsKey(p) && depth[p] == li - 1 && angle.ContainsKey(p)).ToList();
                double a;
                if (parents.Count == 0)
                    a = 2 * Math.PI * ring.IndexOf(id) / Math.Max(1, ring.Count) - Math.PI / 2;
                else
                {
                    double sx = 0, sy = 0;
                    foreach (var p in parents) { sx += Math.Cos(angle[p]); sy += Math.Sin(angle[p]); }
                    a = Math.Atan2(sy, sx);
                }
                return (id, a);
            }).OrderBy(x => x.a).Select(x => x.id).ToList();

            // compute ring barycenter angle and center the uniform distribution around it
            double bx = 0, by = 0, bc = 0;
            foreach (var id in ordered)
            {
                var parents = adj[id].Where(p => depth.ContainsKey(p) && depth[p] == li - 1 && angle.ContainsKey(p)).ToList();
                foreach (var p in parents) { bx += Math.Cos(angle[p]); by += Math.Sin(angle[p]); bc++; }
            }
            double centerAngle = bc > 0 ? Math.Atan2(by / bc, bx / bc) : -Math.PI / 2;
            int n = ordered.Count;
            double step = 2 * Math.PI / n;
            double firstAngle = centerAngle - step * (n - 1) / 2.0;
            for (int i = 0; i < n; i++)
                angle[ordered[i]] = firstAngle + step * i;
        }

        var result = new Dictionary<string, Point>();
        for (int li = 0; li < rings.Count; li++)
        {
            double rr = radii[li];
            foreach (var id in rings[li])
            {
                var (w, h) = sizes.TryGetValue(id, out var s) ? s : (60.0, 60.0);
                double a = angle[id];
                result[id] = new Point(rr * Math.Cos(a) - w / 2, rr * Math.Sin(a) - h / 2);
            }
        }
        return result;
    }

    // ── Arc-over-node avoidance (insert detour waypoints) ─────────────────────

    /// <summary>
    /// For every arc whose polyline crosses a non-endpoint node's padded bbox,
    /// inserts a waypoint that routes the arc around the node on the shorter side.
    /// <paramref name="seed"/> contains waypoints already committed (e.g. back-edge
    /// routes). Iterative: adds at most one waypoint per arc per round until no
    /// segment collides or rounds are exhausted.
    /// </summary>
    public static Dictionary<TLink, List<Point>> DetourAroundNodes<TLink>(
        IReadOnlyList<(TLink Link, string SrcId, string TgtId)> linkInfos,
        IReadOnlyDictionary<string, Point> layout,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<TLink, List<Point>>? seed = null,
        double padding = 22.0,
        int rounds = 8) where TLink : notnull
    {
        var wps = linkInfos.ToDictionary(
            li => li.Link,
            li => seed != null && seed.TryGetValue(li.Link, out var s)
                ? new List<Point>(s) : new List<Point>());

        Point Center(string id)
        {
            var (w, h) = sizes.TryGetValue(id, out var s) ? s : (60.0, 60.0);
            return layout.TryGetValue(id, out var p)
                ? new Point(p.X + w / 2, p.Y + h / 2)
                : new Point(0, 0);
        }

        List<Point> Poly(string src, string tgt, List<Point> existing)
        {
            var sc = Center(src); var tc = Center(tgt);
            double dx = tc.X - sc.X, dy = tc.Y - sc.Y;
            var pts = new List<Point>(existing.Count + 2) { sc };
            pts.AddRange(existing.OrderBy(wp => (wp.X - sc.X) * dx + (wp.Y - sc.Y) * dy));
            pts.Add(tc);
            return pts;
        }

        static bool SegVsBox(Point a, Point b, double bxl, double byt, double bxr, double byb)
        {
            if (Math.Max(a.X, b.X) < bxl || Math.Min(a.X, b.X) > bxr) return false;
            if (Math.Max(a.Y, b.Y) < byt || Math.Min(a.Y, b.Y) > byb) return false;
            if (a.X >= bxl && a.X <= bxr && a.Y >= byt && a.Y <= byb) return true;
            if (b.X >= bxl && b.X <= bxr && b.Y >= byt && b.Y <= byb) return true;
            return SegmentsIntersect(a.X, a.Y, b.X, b.Y, bxl, byt, bxr, byt) ||
                   SegmentsIntersect(a.X, a.Y, b.X, b.Y, bxr, byt, bxr, byb) ||
                   SegmentsIntersect(a.X, a.Y, b.X, b.Y, bxr, byb, bxl, byb) ||
                   SegmentsIntersect(a.X, a.Y, b.X, b.Y, bxl, byb, bxl, byt);
        }

        var nodeIds = layout.Keys.ToList();

        for (int round = 0; round < rounds; round++)
        {
            bool changed = false;
            foreach (var (link, srcId, tgtId) in linkInfos)
            {
                if (!layout.ContainsKey(srcId) || !layout.ContainsKey(tgtId)) continue;
                var poly = Poly(srcId, tgtId, wps[link]);

                Point detour = default!;
                bool found = false;
                for (int si = 0; si < poly.Count - 1 && !found; si++)
                {
                    var a = poly[si]; var b = poly[si + 1];
                    foreach (var nid in nodeIds)
                    {
                        if (nid == srcId || nid == tgtId) continue;
                        var (w, h) = sizes.TryGetValue(nid, out var s) ? s : (60.0, 60.0);
                        var np = layout[nid];
                        double bxl = np.X - padding, byt = np.Y - padding;
                        double bxr = np.X + w + padding, byb = np.Y + h + padding;
                        if (!SegVsBox(a, b, bxl, byt, bxr, byb)) continue;

                        double cx = np.X + w / 2, cy = np.Y + h / 2;
                        double sdx = b.X - a.X, sdy = b.Y - a.Y;
                        double slen = Math.Max(Math.Sqrt(sdx * sdx + sdy * sdy), 1.0);
                        double nx = -sdy / slen, ny = sdx / slen;

                        double radius = Math.Abs(nx) * (w / 2 + padding)
                                      + Math.Abs(ny) * (h / 2 + padding) + 18;

                        double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
                        double proj = (cx - mx) * nx + (cy - my) * ny;
                        double sign = proj >= 0 ? 1 : -1;

                        detour = new Point(cx + nx * sign * radius, cy + ny * sign * radius);
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    wps[link].Add(detour);
                    changed = true;
                }
            }
            if (!changed) break;
        }

        return wps;
    }

    // ── Segment geometry helpers ──────────────────────────────────────────────

    private static double Cross2D(double ax, double ay, double bx, double by, double px, double py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    public static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double d1 = Cross2D(cx, cy, dx, dy, ax, ay);
        double d2 = Cross2D(cx, cy, dx, dy, bx, by);
        double d3 = Cross2D(ax, ay, bx, by, cx, cy);
        double d4 = Cross2D(ax, ay, bx, by, dx, dy);
        return Math.Sign(d1) != Math.Sign(d2) && Math.Sign(d3) != Math.Sign(d4);
    }

    public static Point IntersectionPoint(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double denom = (ax - bx) * (cy - dy) - (ay - by) * (cx - dx);
        if (Math.Abs(denom) < 1e-10) return new Point((ax + bx) / 2, (ay + by) / 2);
        double t = ((ax - cx) * (cy - dy) - (ay - cy) * (cx - dx)) / denom;
        return new Point(ax + t * (bx - ax), ay + t * (by - ay));
    }

    // ── Arc crossing routing ──────────────────────────────────────────────────

    /// <summary>
    /// Multi-round crossing resolution. For each crossing arc pair, adds a
    /// perpendicular waypoint to the more-crossed arc to route it around the other.
    /// <paramref name="seed"/> contains waypoints already committed (e.g. back-edge
    /// routes for hierarchical layout). Returns the final accumulated waypoints.
    /// </summary>
    public static Dictionary<TLink, List<Point>> RouteArcs<TLink>(
        IReadOnlyList<(TLink Link, string SrcId, string TgtId)> linkInfos,
        IReadOnlyDictionary<string, Point> layout,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<TLink, List<Point>>? seed = null,
        int rounds = 5) where TLink : notnull
    {
        // accumulated waypoints (mutable), seeded with any pre-computed routes
        var wps = linkInfos.ToDictionary(
            li => li.Link,
            li => seed != null && seed.TryGetValue(li.Link, out var s) ? new List<Point>(s) : new List<Point>());

        // fast lookup by link key
        var info = linkInfos.ToDictionary(li => li.Link);

        Point Center(string id)
        {
            var (w, h) = sizes.TryGetValue(id, out var s) ? s : (60.0, 60.0);
            return layout.TryGetValue(id, out var p)
                ? new Point(p.X + w / 2, p.Y + h / 2)
                : new Point(0, 0);
        }

        // build sorted polyline for a link (source → sorted waypoints → target)
        List<Point> Poly(string src, string tgt, List<Point> existing)
        {
            var sc = Center(src); var tc = Center(tgt);
            double dx = tc.X - sc.X, dy = tc.Y - sc.Y;
            var pts = new List<Point>(existing.Count + 2) { sc };
            pts.AddRange(existing.OrderBy(wp => (wp.X - sc.X) * dx + (wp.Y - sc.Y) * dy));
            pts.Add(tc);
            return pts;
        }

        for (int round = 0; round < rounds; round++)
        {
            var crossCount = linkInfos.ToDictionary(li => li.Link, _ => 0);
            // (link-to-route, crossing point, midpoint of the other arc's crossing segment)
            var crosses = new List<(TLink Route, Point CrossPt, Point OtherMid)>();

            for (int i = 0; i < linkInfos.Count; i++)
            for (int j = i + 1; j < linkInfos.Count; j++)
            {
                var (l1, s1, t1) = linkInfos[i];
                var (l2, s2, t2) = linkInfos[j];
                // skip arcs that share a node
                if (s1 == s2 || s1 == t2 || t1 == s2 || t1 == t2) continue;
                // skip reverse-parallel (same pair, opposite direction)
                if (s1 == t2 && t1 == s2) continue;

                var poly1 = Poly(s1, t1, wps[l1]);
                var poly2 = Poly(s2, t2, wps[l2]);

                bool found = false;
                for (int si = 0; si < poly1.Count - 1 && !found; si++)
                for (int sj = 0; sj < poly2.Count - 1 && !found; sj++)
                {
                    double ax = poly1[si].X,   ay = poly1[si].Y;
                    double bx = poly1[si+1].X, by = poly1[si+1].Y;
                    double cx = poly2[sj].X,   cy = poly2[sj].Y;
                    double ddx = poly2[sj+1].X, ddy = poly2[sj+1].Y;
                    if (!SegmentsIntersect(ax, ay, bx, by, cx, cy, ddx, ddy)) continue;

                    var cp   = IntersectionPoint(ax, ay, bx, by, cx, cy, ddx, ddy);
                    var mid2 = new Point((cx + ddx) / 2, (cy + ddy) / 2);
                    crossCount[l1]++; crossCount[l2]++;
                    // defer decision: store both candidates, pick later
                    crosses.Add((l1, cp, mid2));
                    crosses.Add((l2, cp, new Point((ax + bx) / 2, (ay + by) / 2)));
                    found = true;
                }
            }

            if (crosses.Count == 0) break;

            // keep one entry per link — the one with the highest cross count (most tangled first)
            var best = crosses
                .GroupBy(x => x.Route, EqualityComparer<TLink>.Default)
                .Select(g => (Link: g.Key, Entry: g.First(), Score: crossCount[g.Key]))
                .OrderByDescending(x => x.Score)
                .ToList();

            var routed = new HashSet<TLink>(EqualityComparer<TLink>.Default);
            foreach (var (link, (_, crossPt, otherMid), _) in best)
            {
                if (!routed.Add(link)) continue;

                var (_, toSrc, toTgt) = info[link];
                var A = Center(toSrc); var B = Center(toTgt);
                double dx2 = B.X - A.X, dy2 = B.Y - A.Y;
                double len = Math.Max(Math.Sqrt(dx2 * dx2 + dy2 * dy2), 1.0);
                double px = -dy2 / len, py = dx2 / len;  // left perpendicular

                // offset away from the crossing arc midpoint
                double dot = (otherMid.X - crossPt.X) * px + (otherMid.Y - crossPt.Y) * py;
                double sign = dot > 0 ? -1.0 : 1.0;
                const double Offset = 75.0;
                wps[link].Add(new Point(crossPt.X + px * Offset * sign,
                                        crossPt.Y + py * Offset * sign));
            }
        }

        return wps;
    }
}
