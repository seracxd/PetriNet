using PetriEditor.Client.Services;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Mapping;

/// <summary>
/// Converts reachability / coverability DTOs into Cytoscape element lists
/// ready to pass to <see cref="CytoscapeInterop.InitAsync"/>.
/// </summary>
public static class CytoscapeMapper
{
    // ── Reachability graph / tree ─────────────────────────────────────────

    public static IReadOnlyList<CyElement> FromReachabilityGraph(
        ReachabilityGraphDto      graph,
        IReadOnlyList<string>     placeNames)
    {
        var elements = new List<CyElement>();

        foreach (var node in graph.Nodes)
        {
            var label   = MarkingLabel(node.Marking.Select(t => (int?)t).ToList(), placeNames);
            var classes = NodeClasses(node.IsInitial, node.IsDeadlock, node.IsDuplicate, hasOmega: false);

            elements.Add(new CyElement("nodes",
                new CyData(node.Id.ToString(), label, null, null),
                classes));
        }

        foreach (var edge in graph.Edges)
            elements.Add(EdgeElement(edge.From, edge.To, edge.TransitionName));

        return elements;
    }

    // ── Coverability tree ─────────────────────────────────────────────────

    public static IReadOnlyList<CyElement> FromCoverabilityTree(
        CoverabilityTreeDto   tree,
        IReadOnlyList<string> placeNames)
    {
        var elements = new List<CyElement>();

        foreach (var node in tree.Nodes)
        {
            var label   = MarkingLabel(node.Marking, placeNames);
            var hasOmega = node.Marking.Any(m => m is null);
            var classes  = NodeClasses(node.IsInitial, node.IsDeadlock, node.IsDuplicate, hasOmega);

            elements.Add(new CyElement("nodes",
                new CyData(node.Id.ToString(), label, null, null),
                classes));
        }

        foreach (var edge in tree.Edges)
            elements.Add(EdgeElement(edge.From, edge.To, edge.TransitionName));

        return elements;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Format a marking as "p1:2, p2:0, p3:ω".
    /// Null token value = ω (omega, unbounded).
    /// </summary>
    private static string MarkingLabel(IReadOnlyList<int?> marking, IReadOnlyList<string> placeNames)
    {
        var parts = new List<string>(marking.Count);
        for (int i = 0; i < marking.Count; i++)
        {
            var name     = i < placeNames.Count ? placeNames[i] : $"p{i}";
            var tokenStr = marking[i] is null ? "ω" : marking[i]!.Value.ToString();
            parts.Add($"{name}:{tokenStr}");
        }
        return string.Join("\n", parts);
    }

    private static string[]? NodeClasses(bool isInitial, bool isDeadlock, bool isDuplicate, bool hasOmega)
    {
        var classes = new List<string>();
        if (isInitial)   classes.Add("initial");
        if (isDeadlock)  classes.Add("deadlock");
        if (isDuplicate) classes.Add("duplicate");
        if (hasOmega)    classes.Add("omega");
        return classes.Count > 0 ? classes.ToArray() : null;
    }

    private static CyElement EdgeElement(int from, int to, string label) =>
        new("edges", new CyData($"e{from}_{to}_{label}", label, from.ToString(), to.ToString()));
}
