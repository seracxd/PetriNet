namespace Analysis.Algorithms;

/// <summary>
/// Builds the reachability <em>tree</em> (BFS unrolling) for a Petri net.
///
/// Unlike <see cref="Analysis.Engines.StateSpaceAnalysis"/> which builds a graph
/// (each unique marking appears exactly once), the tree includes a node for
/// every firing step. When a marking is encountered that is already present
/// somewhere in the tree, a new node is created with <c>IsDuplicate = true</c>
/// and BFS expansion stops at that node. This gives a tree structure where the
/// path from root to any leaf is a valid firing sequence.
///
/// Stops when <see cref="MaxNodes"/> is reached and sets <see cref="HasErrors"/>.
/// </summary>
public sealed class ReachabilityTreeBuilder
{
    public const int MaxNodes = 100_000;

    public bool   HasErrors    { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<ReachTreeNode> Nodes => _nodes;
    public IReadOnlyList<ReachTreeEdge> Edges => _edges;

    private readonly List<ReachTreeNode> _nodes = [];
    private readonly List<ReachTreeEdge> _edges = [];

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

        // Track known markings to detect duplicates
        var knownMarkings = new HashSet<string>();

        var initial   = net.Places.Select(p => p.Tokens).ToArray();
        var rootKey   = MarkingKey(initial);
        knownMarkings.Add(rootKey);

        var root = new ReachTreeNode(0, initial, IsInitial: true, IsDeadlock: false, IsDuplicate: false, ParentId: -1);
        _nodes.Add(root);

        // BFS queue carries (nodeId, marking)
        var queue = new Queue<(int NodeId, int[] Marking)>();
        queue.Enqueue((0, initial));

        var pIdx = net.Places
            .Select((p, i) => (p.Id, i))
            .ToDictionary(x => x.Id, x => x.i);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (parentId, marking) = queue.Dequeue();
            bool anyFired = false;

            foreach (var t in net.Transitions)
            {
                if (!FireUtils.IsEnabled(net, pIdx, marking, t.Id))
                    continue;

                anyFired = true;
                var next = FireUtils.Fire(net, pIdx, marking, t.Id);

                if (_nodes.Count >= MaxNodes)
                {
                    HasErrors    = true;
                    ErrorMessage = $"Reachability tree exceeded {MaxNodes} nodes — net may be unbounded or have very deep reachability.";
                    return;
                }

                var key       = MarkingKey(next);
                bool isDup    = !knownMarkings.Add(key);
                int  newId    = _nodes.Count;
                bool isDeadlock = false; // determined after full expansion; duplicates are not expanded

                _nodes.Add(new ReachTreeNode(newId, next,
                    IsInitial:   false,
                    IsDeadlock:  false,        // placeholder — set below if no children
                    IsDuplicate: isDup,
                    ParentId:    parentId));

                _edges.Add(new ReachTreeEdge(parentId, newId, t.Id, t.Name));

                if (!isDup)
                    queue.Enqueue((newId, next));
            }

            // If the node was dequeued but nothing fired it is a deadlock
            if (!anyFired)
            {
                var existing = _nodes[parentId];
                _nodes[parentId] = existing with { IsDeadlock = true };
            }
        }
    }

    private static string MarkingKey(int[] marking) =>
        string.Join(",", marking);
}

public sealed record ReachTreeNode(
    int   Id,
    int[] Marking,
    bool  IsInitial,
    bool  IsDeadlock,
    bool  IsDuplicate,
    int   ParentId);

public sealed record ReachTreeEdge(int From, int To, string TransitionId, string TransitionName);
