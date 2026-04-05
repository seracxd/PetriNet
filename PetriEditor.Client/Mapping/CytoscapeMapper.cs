using PetriEditor.Client.Services;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Mapping;

public static class CytoscapeMapper
{
    // ── Reachability graph ────────────────────────────────────────────────

    public static IReadOnlyList<CyElement> FromReachabilityGraph(
        ReachabilityGraphDto  graph,
        IReadOnlyList<string> placeNames)
    {
        var elements = new List<CyElement>();

        // Merge duplicate nodes: map each duplicate's Id → the canonical node Id
        var canonical = BuildCanonicalMap(
            graph.Nodes.Select(n => (n.Id, n.IsDuplicate, Key: string.Join(",", n.Marking))));

        var indexMap = BfsIndex(
            graph.Nodes.Where(n => !n.IsDuplicate || canonical[n.Id] == n.Id)
                       .Select(n => (n.Id, n.IsInitial)),
            graph.Edges.Select(e => (canonical[e.From], canonical[e.To])));

        foreach (var node in graph.Nodes)
        {
            if (canonical[node.Id] != node.Id) continue; // skip duplicates

            int idx    = indexMap.GetValueOrDefault(node.Id, node.Id);
            string lbl = idx == 0 ? "M\u2080" : $"M{idx}";
            var classes = NodeClasses(node.IsInitial, node.IsDeadlock, false, false, node.IsTruncated);

            elements.Add(new CyElement("nodes",
                new CyData(node.Id.ToString(), lbl, null, null,
                    Marking: node.Marking.ToArray(), PlaceNames: placeNames.ToArray()),
                classes));
        }

        // Deduplicate edges after merging (same from→to can now appear multiple times)
        var seenEdges = new HashSet<string>();
        foreach (var edge in graph.Edges)
        {
            int from = canonical[edge.From];
            int to   = canonical[edge.To];
            string key = $"{from}→{to}:{edge.TransitionName}";
            if (seenEdges.Add(key))
                elements.Add(EdgeElement(from, to, edge.TransitionName));
        }

        return elements;
    }

    // ── Coverability tree ─────────────────────────────────────────────────

    public static IReadOnlyList<CyElement> FromCoverabilityTree(
        CoverabilityTreeDto   tree,
        IReadOnlyList<string> placeNames)
    {
        var elements = new List<CyElement>();

        var canonical = BuildCanonicalMap(
            tree.Nodes.Select(n => (n.Id, n.IsDuplicate,
                Key: string.Join(",", n.Marking.Select(m => m.HasValue ? m.Value.ToString() : "w")))));

        var indexMap = BfsIndex(
            tree.Nodes.Where(n => canonical[n.Id] == n.Id)
                       .Select(n => (n.Id, n.IsInitial)),
            tree.Edges.Select(e => (canonical[e.From], canonical[e.To])));

        foreach (var node in tree.Nodes)
        {
            if (canonical[node.Id] != node.Id) continue;

            int idx    = indexMap.GetValueOrDefault(node.Id, node.Id);
            string lbl = idx == 0 ? "M\u2080" : $"M{idx}";
            bool hasOmega  = node.Marking.Any(m => m is null);
            var classes    = NodeClasses(node.IsInitial, node.IsDeadlock, false, hasOmega, node.IsTruncated);
            var markingArr = node.Marking.Select(m => m ?? -1).ToArray();

            elements.Add(new CyElement("nodes",
                new CyData(node.Id.ToString(), lbl, null, null,
                    Marking: markingArr, PlaceNames: placeNames.ToArray()),
                classes));
        }

        var seenEdges = new HashSet<string>();
        foreach (var edge in tree.Edges)
        {
            int from = canonical[edge.From];
            int to   = canonical[edge.To];
            if (from == to) continue; // self-loops after merge — skip
            string key = $"{from}→{to}:{edge.TransitionName}";
            if (seenEdges.Add(key))
                elements.Add(EdgeElement(from, to, edge.TransitionName));
        }

        return elements;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// For each node, returns its canonical Id — the first node seen with the same marking key.
    /// Non-duplicate nodes map to themselves.
    /// </summary>
    private static Dictionary<int, int> BuildCanonicalMap(
        IEnumerable<(int Id, bool IsDuplicate, string Key)> nodes)
    {
        var firstSeen = new Dictionary<string, int>(); // key → first node id
        var result    = new Dictionary<int, int>();

        foreach (var (id, isDup, key) in nodes)
        {
            if (firstSeen.TryGetValue(key, out int canonId))
            {
                result[id] = canonId;
            }
            else
            {
                firstSeen[key] = id;
                result[id] = id;
            }
        }
        return result;
    }

    private static Dictionary<int, int> BfsIndex(
        IEnumerable<(int Id, bool IsInitial)> nodes,
        IEnumerable<(int From, int To)> edges)
    {
        var nodeList   = nodes.ToList();
        var childrenOf = new Dictionary<int, List<int>>();
        foreach (var (id, _) in nodeList) childrenOf[id] = new();
        foreach (var (from, to) in edges)
        {
            if (!childrenOf.ContainsKey(from)) childrenOf[from] = new();
            childrenOf[from].Add(to);
        }

        var root    = nodeList.FirstOrDefault(n => n.IsInitial).Id;
        var result  = new Dictionary<int, int>();
        var queue   = new Queue<int>();
        var visited = new HashSet<int>();
        queue.Enqueue(root);
        visited.Add(root);
        int idx = 0;
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            result[cur] = idx++;
            foreach (int ch in childrenOf.GetValueOrDefault(cur, new()))
                if (visited.Add(ch)) queue.Enqueue(ch);
        }
        foreach (var (id, _) in nodeList)
            if (!result.ContainsKey(id)) result[id] = idx++;
        return result;
    }

    private static string[]? NodeClasses(bool isInitial, bool isDeadlock, bool isDuplicate, bool hasOmega, bool isCutOff = false)
    {
        var c = new List<string>();
        if (isInitial)   c.Add("initial");
        if (isDeadlock)  c.Add("deadlock");
        if (isDuplicate) c.Add("duplicate");
        if (hasOmega)    c.Add("omega");
        if (isCutOff)    c.Add("cutoff");
        return c.Count > 0 ? c.ToArray() : null;
    }

    private static CyElement EdgeElement(int from, int to, string label) =>
        new("edges", new CyData($"e{from}_{to}_{label}", label, from.ToString(), to.ToString()));
}
