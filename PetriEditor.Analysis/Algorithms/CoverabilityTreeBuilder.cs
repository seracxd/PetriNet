using Analysis.Engines;
using PetriEditor.Shared.Contracts;

namespace Analysis.Algorithms;

/// <summary>
/// Builds the Karp-Miller coverability tree for a Petri net.
///
/// The coverability tree handles potentially unbounded nets by replacing
/// unbounded token counts with ω (<c>int.MaxValue</c> sentinel). It always
/// terminates because the ω-introduction rule ensures no infinite ascending
/// chains can form.
///
/// Algorithm (Karp-Miller, 1969):
///  1. Root = initial marking.
///  2. Process each leaf node <c>n</c> with marking <c>m</c> in BFS order:
///     For each enabled transition <c>t</c>:
///       a. Compute <c>m' = Fire(t, m)</c> (omega tokens propagate).
///       b. Walk the ancestor chain from <c>n</c> to the root.
///          For every ancestor <c>a</c> with marking <c>ma</c>:
///          if <c>ma[i] ≤ m'[i]</c> for all i  AND  <c>ma[j] &lt; m'[j]</c> for some j,
///          set <c>m'[j] = ω</c> for every j where <c>ma[j] &lt; m'[j]</c>.
///       c. If <c>m'</c> equals any node already in the tree →
///          create a duplicate leaf (no further expansion).
///       d. Otherwise add a new node and enqueue it.
///  3. Stop when <see cref="MaxNodes"/> is reached.
/// </summary>
public sealed class CoverabilityTreeBuilder
{
    public const int MaxNodes = AnalysisLimits.MaxMarkings;
    public const int Omega    = int.MaxValue;   // ω sentinel

    /// <summary>
    /// Saturation ceiling for finite token counts (<c>int.MaxValue / 2</c>, mirrors
    /// <c>FireUtils.MaxFiniteTokens</c>). When ω-acceleration is disabled — which it
    /// is for inhibitor/reset nets, where Karp-Miller monotonicity does not hold —
    /// an unbounded place keeps accumulating tokens until <see cref="Fire"/> clamps it
    /// here. A marking value at or above this ceiling is therefore a witness that the
    /// place is not k-bounded for any practical k (Def. 2.15): it cannot be confused
    /// with an ordinary finite count.
    /// </summary>
    public const int Saturated = int.MaxValue / 2;

    /// <summary>
    /// True if <paramref name="value"/> is a coverability-tree witness of a place that
    /// grows without bound: either the Karp-Miller ω sentinel or the saturation ceiling
    /// reached when ω-acceleration is disabled. Both prove the place exceeds every finite
    /// bound, so downstream boundedness/safety verdicts can treat them identically.
    /// </summary>
    public static bool GrowsWithoutBound(int value) => value >= Saturated;

    public bool   HasErrors    { get; private set; }
    public bool   IsTruncated  { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<CoverTreeNode> Nodes        => _nodes;
    public IReadOnlyList<CoverTreeEdge> Edges        => _edges;
    public IReadOnlySet<int>            TruncatedIds => _truncatedIds;

    private readonly List<CoverTreeNode> _nodes        = [];
    private readonly List<CoverTreeEdge> _edges        = [];
    private readonly HashSet<int>        _truncatedIds = [];

    // Marking → node index, for O(1) duplicate detection during build.
    private readonly Dictionary<int[], int> _markingIndex = new(TokenArrayComparer.Instance);

    public void Build(
        PetriNetSnapshot  net,
        CancellationToken ct                       = default,
        int               maxNodes                 = MaxNodes,
        bool              disableOmegaAcceleration = false)
    {
        // Karp-Miller's ω-acceleration assumes monotone firing semantics.
        // Inhibitor and reset arcs break that, so for special-arc nets we
        // skip the acceleration and produce a plain bounded-reachability
        // tree with cycle detection instead. The caller passes
        // disableOmegaAcceleration=true when the net has any non-Normal arc.
        _nodes.Clear();
        _edges.Clear();
        _truncatedIds.Clear();
        _markingIndex.Clear();
        HasErrors    = false;
        IsTruncated  = false;
        ErrorMessage = null;

        if (!net.Places.Any() || !net.Transitions.Any())
        {
            HasErrors    = true;
            ErrorMessage = "Net has no places or transitions.";
            return;
        }

        var initial = net.Places.Select(p => p.Tokens).ToArray();
        var root    = new CoverTreeNode(0, initial, IsInitial: true, IsDeadlock: false, IsDuplicate: false, ParentId: -1);
        _nodes.Add(root);
        _markingIndex[initial] = 0;

        var pIdx = net.Places
            .Select((p, i) => (p.Id, i))
            .ToDictionary(x => x.Id, x => x.i);

        // BFS queue carries nodeId; marking is read from _nodes[nodeId]
        var queue = new Queue<int>();
        queue.Enqueue(0);

        // The cap counts UNIQUE markings (canonical nodes). Reference (duplicate)
        // nodes are cheap leaves and don't count — that matches the slider's user-
        // facing semantics. The root is the first unique marking.
        int uniqueCount = 1;
        int iterCounter = 0;
        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                HasErrors    = true;
                ErrorMessage = "Analysis cancelled.";
                return;
            }

            // Memory-pressure guard: stop building before we OOM.
            if ((++iterCounter % PetriEditor.Shared.Contracts.AnalysisLimits.HeapSampleInterval) == 0)
            {
                long heap = GC.GetTotalMemory(false);
                if (heap > PetriEditor.Shared.Contracts.AnalysisLimits.MaxBuildHeapBytes)
                {
                    IsTruncated  = true;
                    ErrorMessage = $"Coverability tree stopped: heap usage exceeded {PetriEditor.Shared.Contracts.AnalysisLimits.MaxBuildHeapBytes / (1024 * 1024)} MB.";
                    return;
                }
            }

            int parentId    = queue.Dequeue();
            var parentNode  = _nodes[parentId];
            var marking     = parentNode.Marking;
            bool anyFired   = false;

            foreach (var t in FireUtils.GetFireableTransitions(net, pIdx, marking))
            {
                if (ct.IsCancellationRequested)
                {
                    HasErrors    = true;
                    ErrorMessage = "Analysis cancelled.";
                    return;
                }

                anyFired = true;

                // Step a: fire the transition
                var next = FireUtils.Fire(net, pIdx, marking, t.Id);

                // Step b: walk ancestors and promote to omega where needed
                if (!disableOmegaAcceleration)
                    PropagateOmega(next, parentId);

                // Step c/d: O(1) duplicate check via marking index
                bool isDup = _markingIndex.ContainsKey(next);

                // Cap check: only unique markings count. Duplicate-reference leaves
                // pass through freely so we don't lose loop-back indicators.
                if (!isDup && uniqueCount >= maxNodes)
                {
                    IsTruncated  = true;
                    ErrorMessage = $"Coverability tree exceeded {maxNodes} unique markings.";
                    _truncatedIds.Add(parentId);
                    continue;
                }

                int newId = _nodes.Count;

                _nodes.Add(new CoverTreeNode(newId, next,
                    IsInitial:   false,
                    IsDeadlock:  false,
                    IsDuplicate: isDup,
                    ParentId:    parentId));

                if (!isDup)
                {
                    _markingIndex[next] = newId;
                    uniqueCount++;
                }

                _edges.Add(new CoverTreeEdge(parentId, newId, t.Id, t.Name));

                if (!isDup)
                    queue.Enqueue(newId);
            }

            if (!anyFired)
            {
                var existing = _nodes[parentId];
                _nodes[parentId] = existing with { IsDeadlock = true };
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Walk the ancestor chain of <paramref name="parentId"/> and apply
    /// the omega-introduction rule to <paramref name="next"/>.
    /// </summary>
    private void PropagateOmega(int[] next, int parentId)
    {
        int cur = parentId;
        while (cur >= 0)
        {
            var ancestor = _nodes[cur];
            var am       = ancestor.Marking;

            // Check: ancestor ≤ next (component-wise), with at least one strict increase.
            // Omega (int.MaxValue) compares correctly as larger than any finite value.
            bool dominated = true;
            bool strict    = false;
            for (int i = 0; i < am.Length; i++)
            {
                if (am[i] > next[i]) { dominated = false; break; }
                if (am[i] < next[i]) strict = true;
            }

            if (dominated && strict)
            {
                // Set omega wherever ancestor is strictly smaller
                for (int i = 0; i < am.Length; i++)
                    if (am[i] < next[i])
                        next[i] = Omega;
            }

            cur = ancestor.ParentId;
        }
    }
}

public sealed record CoverTreeNode(
    int   Id,
    int[] Marking,      // int.MaxValue = ω
    bool  IsInitial,
    bool  IsDeadlock,
    bool  IsDuplicate,
    int   ParentId);

public sealed record CoverTreeEdge(int From, int To, string TransitionId, string TransitionName);
