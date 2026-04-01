using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Server.Analysis;

/// <summary>
/// Converts the engine's <see cref="AnalysisReport"/> into the wire-safe
/// <see cref="AnalysisResultDto"/> that is sent to the client over SignalR.
///
/// All engine types stay on the server; the client only ever sees plain DTOs.
/// </summary>
public static class AnalysisResultMapper
{
    public static AnalysisResultDto ToDto(AnalysisReport report)
    {
        var net = report.Net;

        // ── Scalar properties ─────────────────────────────────────────────
        bool isBounded     = report.StateSpace?.IsBounded()                              ?? false;
        bool isDeadlockFree = report.StateSpace?.IsDeadlockFree()                        ?? false;
        bool isReversible  = report.StateSpace?.IsReversible()                           ?? false;
        bool isSafe        = report.StateSpace?.IsSafe()                                 ?? false;
        bool isLive        = report.StateSpace?.IsLive(net.Transitions.Count)            ?? false;

        // ── Property results ──────────────────────────────────────────────
        var propertyResults = report.PropertyResults.Values
            .Select(MapPropertyResult)
            .ToList();

        // ── Invariants ────────────────────────────────────────────────────
        var pInvariants = (report.Invariants?.PInvariants ?? [])
            .Select(inv => new InvariantDto(inv.Structure))
            .ToList();

        var tInvariants = (report.Invariants?.TInvariants ?? [])
            .Select(inv => new InvariantDto(inv.Structure))
            .ToList();

        // ── Reachability graph (from state-space deduplication) ───────────
        ReachabilityGraphDto? reachGraph = BuildReachabilityGraph(report);

        // ── Reachability tree (BFS unrolling with duplicate flags) ────────
        ReachabilityGraphDto? reachTree = BuildReachabilityTree(report);

        // ── Coverability tree (Karp-Miller, ω = null in DTO) ─────────────
        CoverabilityTreeDto? coverTree = BuildCoverabilityTree(report);

        return new AnalysisResultDto(
            StateCount:           report.StateCount,
            IsBounded:            isBounded,
            IsDeadlockFree:       isDeadlockFree,
            IsReversible:         isReversible,
            IsSafe:               isSafe,
            IsLive:               isLive,
            ClassificationSummary: report.ClassificationSummary,
            PropertyResults:      propertyResults,
            PInvariants:          pInvariants,
            TInvariants:          tInvariants,
            ReachabilityGraph:    reachTree ?? reachGraph,   // prefer tree; fall back to graph
            CoverabilityTree:     coverTree
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PropertyResultDto MapPropertyResult(PropertyTestResult r) =>
        new(
            Property:         r.Property.ToString(),
            Status:           r.Status.ToString(),
            StatusLabel:      r.StatusLabel,
            StatusColor:      r.StatusColor,
            StatusBackground: r.StatusBackground,
            Reasons:          r.Reasons,
            Errors:           r.Errors
        );

    /// <summary>
    /// Build the reachability graph DTO from the state-space analysis.
    /// Returns null if the state space is unavailable (unbounded / error).
    /// </summary>
    private static ReachabilityGraphDto? BuildReachabilityGraph(AnalysisReport report)
    {
        var ss = report.StateSpace;
        if (ss == null || ss.HasErrors)
            return null;

        var net       = report.Net;
        var states    = ss.States;
        var placeNames = net.Places.Select(p => p.Name).ToList();
        var deadlocks = new HashSet<int>(FindDeadlockIndices(ss));

        // ── Nodes ─────────────────────────────────────────────────────────
        // We want to flag duplicate (already-visited) nodes.
        // In BFS the state space has no duplicates — every entry is unique.
        // "IsDuplicate" means the same marking appeared via a different path;
        // since StateSpaceAnalysis already deduplicates, all nodes are unique.
        var nodes = states.Select((marking, id) => new ReachNodeDto(
            Id:          id,
            Marking:     marking,
            IsInitial:   id == 0,
            IsDeadlock:  deadlocks.Contains(id),
            IsDuplicate: false,
            ParentId:    -1    // parent tracking not available without BFS tree; -1 = unknown
        )).ToList();

        // ── Edges ─────────────────────────────────────────────────────────
        // Rebuild edges by re-running a minimal BFS over the state index.
        // StateSpaceAnalysis does not expose its edge list directly, so we
        // reconstruct it from the firing semantics stored in _edges via reflection
        // ... or, more simply, we re-expose it through a dedicated property.
        // For now we call the public edge accessor added in Phase 3.
        var edges = ss.GetEdges()
            .SelectMany((edgeList, from) =>
                edgeList.Select(e => new ReachEdgeDto(
                    From:           from,
                    To:             e.To,
                    TransitionId:   e.TransId,
                    TransitionName: net.TransitionById.TryGetValue(e.TransId, out var t) ? t.Name : e.TransId
                )))
            .ToList();

        return new ReachabilityGraphDto(nodes, edges, placeNames);
    }

    private static IEnumerable<int> FindDeadlockIndices(StateSpaceAnalysis ss)
    {
        var edges = ss.GetEdges();
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i].Count == 0)
                yield return i;
        }
    }

    // ── Reachability tree ─────────────────────────────────────────────────

    private static ReachabilityGraphDto? BuildReachabilityTree(AnalysisReport report)
    {
        var rt = report.ReachabilityTree;
        if (rt == null || rt.HasErrors || rt.Nodes.Count == 0)
            return null;

        var net        = report.Net;
        var placeNames = net.Places.Select(p => p.Name).ToList();

        var nodes = rt.Nodes.Select(n => new ReachNodeDto(
            Id:          n.Id,
            Marking:     n.Marking,
            IsInitial:   n.IsInitial,
            IsDeadlock:  n.IsDeadlock,
            IsDuplicate: n.IsDuplicate,
            ParentId:    n.ParentId
        )).ToList();

        var edges = rt.Edges.Select(e => new ReachEdgeDto(
            From:           e.From,
            To:             e.To,
            TransitionId:   e.TransitionId,
            TransitionName: e.TransitionName
        )).ToList();

        return new ReachabilityGraphDto(nodes, edges, placeNames);
    }

    // ── Coverability tree ─────────────────────────────────────────────────

    private static CoverabilityTreeDto? BuildCoverabilityTree(AnalysisReport report)
    {
        var ct = report.CoverabilityTree;
        if (ct == null || ct.HasErrors || ct.Nodes.Count == 0)
            return null;

        var net        = report.Net;
        var placeNames = net.Places.Select(p => p.Name).ToList();

        var nodes = ct.Nodes.Select(n => new CoverNodeDto(
            Id:          n.Id,
            // int.MaxValue (Omega) → null in the DTO so the client renders "ω"
            Marking:     n.Marking.Select(v => v == CoverabilityTreeBuilder.Omega ? (int?)null : v).ToList(),
            IsInitial:   n.IsInitial,
            IsDeadlock:  n.IsDeadlock,
            IsDuplicate: n.IsDuplicate,
            ParentId:    n.ParentId
        )).ToList();

        var edges = ct.Edges.Select(e => new CoverEdgeDto(
            From:           e.From,
            To:             e.To,
            TransitionId:   e.TransitionId,
            TransitionName: e.TransitionName
        )).ToList();

        return new CoverabilityTreeDto(nodes, edges, placeNames);
    }
}
