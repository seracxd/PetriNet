using PetriEditor.Shared.Contracts;

namespace Analysis.Algorithms;

/// <summary>
/// Builds a <see cref="GraphLayoutDto"/> from raw coverability-tree nodes/edges:
/// merges duplicate markings into a single canonical node, runs layered layout,
/// and assigns BFS labels (M₀, M₁, …).  The result is what the client graph
/// view renders — it does no further layout work.
/// </summary>
public static class GraphLayoutBuilder
{
    public static GraphLayoutDto Build(
        IReadOnlyList<CoverNodeDto> nodes,
        IReadOnlyList<CoverEdgeDto> edges,
        int                         maxDisplayNodes = int.MaxValue)
    {
        if (nodes.Count == 0)
            return new GraphLayoutDto([], [], 0, 0);

        // ── Merge duplicates by marking ──────────────────────────────────
        var markingKey = new Dictionary<int, string>(nodes.Count);
        var canonicalForKey = new Dictionary<string, int>();
        foreach (var n in nodes)
        {
            var key = MarkingKey(n.Marking);
            markingKey[n.Id] = key;
            if (!canonicalForKey.ContainsKey(key)) canonicalForKey[key] = n.Id;
        }
        var canonicalOf = new Dictionary<int, int>(nodes.Count);
        foreach (var n in nodes) canonicalOf[n.Id] = canonicalForKey[markingKey[n.Id]];

        // Root: initial-flagged, else no-parent, else first.
        var rootOrig = nodes.FirstOrDefault(n => n.IsInitial)
                     ?? nodes.FirstOrDefault(n => n.ParentId < 0)
                     ?? nodes[0];
        int rootId = canonicalOf[rootOrig.Id];

        // Canonical node set.
        var canonicalNodes = new List<CoverNodeDto>(nodes.Count);
        foreach (var n in nodes)
            if (canonicalOf[n.Id] == n.Id) canonicalNodes.Add(n);

        // Children map for truncation BFS.
        var canonChildren = new Dictionary<int, HashSet<int>>(canonicalNodes.Count);
        foreach (var n in canonicalNodes) canonChildren[n.Id] = new();
        foreach (var e in edges)
        {
            if (!canonicalOf.TryGetValue(e.From, out int f)) continue;
            if (!canonicalOf.TryGetValue(e.To,   out int t)) continue;
            if (canonChildren.TryGetValue(f, out var set)) set.Add(t);
        }

        // ── Truncate by BFS if too many canonical nodes ──────────────────
        HashSet<int> kept;
        if (canonicalNodes.Count > maxDisplayNodes)
        {
            kept = new();
            var q = new Queue<int>();
            q.Enqueue(rootId); kept.Add(rootId);
            while (q.Count > 0 && kept.Count < maxDisplayNodes)
            {
                int cur = q.Dequeue();
                foreach (int nb in canonChildren[cur])
                    if (kept.Count < maxDisplayNodes && kept.Add(nb))
                        q.Enqueue(nb);
            }
            canonicalNodes = canonicalNodes.Where(n => kept.Contains(n.Id)).ToList();
        }
        else
        {
            kept = new(canonicalNodes.Select(n => n.Id));
        }

        // ── Layout-graph edges (deduped canonical→canonical, skip self-loops) ──
        var layoutEdgeSet   = new HashSet<(int, int)>();
        var edgeLabel       = new Dictionary<(int, int), string>();
        foreach (var e in edges)
        {
            if (!canonicalOf.TryGetValue(e.From, out int f) || !canonicalOf.TryGetValue(e.To, out int t)) continue;
            if (!kept.Contains(f) || !kept.Contains(t)) continue;
            var key = (f, t);
            if (f != t) layoutEdgeSet.Add(key);
            edgeLabel.TryAdd(key, e.TransitionName);
        }

        // ── Run layered layout ──
        var layout = GraphLayoutEngine.Layout(
            rootId,
            canonicalNodes.Select(n => n.Id).ToList(),
            layoutEdgeSet.ToList());

        // ── BFS labels (M₀, M₁, …) ──
        var labels = new Dictionary<int, string>(canonicalNodes.Count);
        int labelIdx = 0;
        var labelSeen = new HashSet<int>();
        var labelQ = new Queue<int>();
        labelQ.Enqueue(rootId); labelSeen.Add(rootId);
        while (labelQ.Count > 0)
        {
            int cur = labelQ.Dequeue();
            labels[cur] = labelIdx == 0 ? "M₀" : $"M{labelIdx}";
            labelIdx++;
            if (!canonChildren.TryGetValue(cur, out var kids)) continue;
            var ordered = kids
                .Where(k => kept.Contains(k))
                .OrderBy(c => layout.Layer.GetValueOrDefault(c, 0))
                .ThenBy(c  => layout.Col.GetValueOrDefault(c, 0));
            foreach (int ch in ordered)
                if (labelSeen.Add(ch)) labelQ.Enqueue(ch);
        }
        foreach (var n in canonicalNodes)
            if (!labels.ContainsKey(n.Id)) labels[n.Id] = $"M{labelIdx++}";

        // ── Emit DTOs ──
        var outNodes = new List<GraphLayoutNodeDto>(canonicalNodes.Count);
        foreach (var n in canonicalNodes)
        {
            outNodes.Add(new GraphLayoutNodeDto(
                Id:          n.Id,
                Layer:       layout.Layer[n.Id],
                Col:         layout.Col[n.Id],
                Label:       labels[n.Id],
                MarkingKey:  markingKey[n.Id],
                Marking:     n.Marking,
                IsInitial:   n.IsInitial,
                IsDeadlock:  n.IsDeadlock,
                IsOmega:     n.Marking.Any(m => m is null),
                IsTruncated: n.IsTruncated));
        }

        var outEdges = new List<GraphLayoutEdgeDto>();
        // Iterate original edges so we keep every transition even if duplicated
        // in the canonical graph (preserves the "which transition fires" info).
        var emitted = new HashSet<(int, int, string)>();
        foreach (var e in edges)
        {
            if (!canonicalOf.TryGetValue(e.From, out int f) || !canonicalOf.TryGetValue(e.To, out int t)) continue;
            if (!kept.Contains(f) || !kept.Contains(t)) continue;
            var key = (f, t, e.TransitionName);
            if (!emitted.Add(key)) continue;

            bool isSelf = f == t;
            bool isBack = isSelf || layout.BackEdges.Contains((f, t));
            outEdges.Add(new GraphLayoutEdgeDto(
                From:           f,
                To:             t,
                TransitionName: e.TransitionName,
                FromLayer:      layout.Layer.GetValueOrDefault(f, 0),
                ToLayer:        layout.Layer.GetValueOrDefault(t, 0),
                IsBack:         isBack,
                IsSelf:         isSelf));
        }

        int maxLayer = outNodes.Count == 0 ? 0 : outNodes.Max(n => n.Layer);
        int maxCol   = outNodes.Count == 0 ? 0 : outNodes.Max(n => n.Col);

        return new GraphLayoutDto(outNodes, outEdges, maxLayer, maxCol);
    }

    private static string MarkingKey(IReadOnlyList<int?> marking)
    {
        var sb = new System.Text.StringBuilder(marking.Count * 3);
        for (int i = 0; i < marking.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var v = marking[i];
            sb.Append(v.HasValue ? v.Value.ToString() : "w");
        }
        return sb.ToString();
    }
}
