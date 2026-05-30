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
        // Definite unboundedness comes from ω in the coverability tree, NOT from
        // !isBounded (which is also true for "state space truncated" where the
        // verdict is unknown).
        bool isDefinitelyUnbounded =
            report.CoverabilityTree?.Nodes
                .Any(n => n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound))
            ?? false;

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

        // ── Cycles DTO ────────────────────────────────────────────────────────
        CyclesDto? cyclesDto = null;
        var cyc = report.Cycles;
        if (cyc != null)
        {
            var cycleDtos = cyc.Cycles.Select(c => new CycleDto(
                c.NodeIds, c.PlaceIds, c.TransitionIds, c.TokensInCycle)).ToList();
            cyclesDto = new CyclesDto(
                cyc.HasErrors, cyc.ErrorMsg, cycleDtos,
                cyc.PlaceCoverageCount(net),
                cyc.TransitionCoverageCount(net));
        }

        // ── Traps DTO ─────────────────────────────────────────────────────────
        TrapsDto? trapsDto = null;
        var tc = report.TrapCotrap;
        if (tc != null)
        {
            var traps   = tc.Traps.Select(s => new PlaceSubsetDto(s.PlaceIds.ToList(), s.HasToken)).ToList();
            var siphons = tc.Cotraps.Select(s => new PlaceSubsetDto(s.PlaceIds.ToList(), s.HasToken)).ToList();
            trapsDto = new TrapsDto(tc.HasErrors, tc.ErrorMsg, traps, siphons);
        }

        // ── Classification subclasses ─────────────────────────────────────────
        var subclasses = (report.Classification?.Classes ?? (IReadOnlySet<NetSubclass>)new HashSet<NetSubclass>())
            .Select(c => c.ToString()).ToList();

        // ── Net structure ─────────────────────────────────────────────────────
        var netStructure = new NetStructureDto(
            PlaceCount:       net.Places.Count,
            TransitionCount:  net.Transitions.Count,
            NormalArcCount:   net.Arcs.Count(a => a.ArcType == PnArcType.Normal),
            InhibitorArcCount: net.Arcs.Count(a => a.ArcType == PnArcType.Inhibitor),
            ResetArcCount:    net.Arcs.Count(a => a.ArcType == PnArcType.Reset),
            InitialTokenCount: net.Places.Sum(p => p.Tokens));

        return new AnalysisResultDto(
            StateCount:               report.StateCount,
            IsBounded:                isBounded,
            IsDeadlockFree:           isDeadlockFree,
            IsReversible:             isReversible,
            IsSafe:                   isSafe,
            IsLive:                   isLive,
            ClassificationSummary:    report.ClassificationSummary,
            ClassificationSubclasses: subclasses,
            PropertyResults:          propertyResults,
            PInvariants:              pInvariants,
            TInvariants:              tInvariants,
            InvariantsTruncated:      report.Invariants?.WasTruncated ?? false,
            // "Available" = engine ran without being skipped (inhibitor/reset) and
            // without an internal error. Empty lists under Available=true means the
            // net legitimately has no non-trivial invariants.
            InvariantsAvailable:      report.Invariants is { WasSkipped: false, HasErrors: false },
            InvariantsMessage:        report.Invariants is { WasSkipped: true } or { HasErrors: true }
                                          ? report.Invariants.ErrorMsg
                                          : null,
            ReachabilityGraph:        reachGraph,
            ReachabilityTree:         reachTree,
            CoverabilityTree:         coverTree,
            Cycles:                   cyclesDto,
            Traps:                    trapsDto,
            NetStructure:             netStructure,
            IsDefinitelyUnbounded:    isDefinitelyUnbounded
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
    public static ReachabilityGraphDto? BuildReachabilityGraphDto(PetriNetSnapshot net, StateSpaceAnalysis ss)
    {
        if (ss.HasErrors && !ss.IsTruncated) return null;
        if (ss.States.Count == 0) return null;
        var placeNames  = net.Places.Select(p => p.Name).ToList();
        var edgesByFrom = ss.GetEdges();
        var zeroEdge    = new HashSet<int>(Enumerable.Range(0, ss.States.Count).Where(i => edgesByFrom[i].Count == 0));
        var deadlocks   = ss.IsTruncated ? new HashSet<int>() : zeroEdge;
        var truncated   = ss.IsTruncated ? zeroEdge : new HashSet<int>();
        var nodes = ss.States.Select((marking, id) => new ReachNodeDto(
            id, marking, id == 0, deadlocks.Contains(id), false, truncated.Contains(id), -1)).ToList();
        var edges = ss.GetEdges()
            .SelectMany((edgeList, from) => edgeList.Select(e => new ReachEdgeDto(
                from, e.To, e.TransId,
                net.TransitionById.TryGetValue(e.TransId, out var t) ? t.Name : e.TransId)))
            .ToList();
        return new ReachabilityGraphDto(nodes, edges, placeNames);
    }

    public static ReachabilityGraphDto? BuildReachabilityTreeDto(PetriNetSnapshot net, ReachabilityTreeBuilder rt)
    {
        if ((rt.HasErrors && !rt.IsTruncated) || rt.Nodes.Count == 0) return null;
        var placeNames = net.Places.Select(p => p.Name).ToList();
        var nodes = rt.Nodes.Select(n => new ReachNodeDto(
            Id: n.Id, Marking: n.Marking, IsInitial: n.IsInitial,
            IsDeadlock: n.IsDeadlock, IsDuplicate: n.IsDuplicate,
            IsTruncated: rt.TruncatedIds.Contains(n.Id), ParentId: n.ParentId)).ToList();
        var edges = rt.Edges.Select(e => new ReachEdgeDto(
            From: e.From, To: e.To, TransitionId: e.TransitionId, TransitionName: e.TransitionName)).ToList();
        return new ReachabilityGraphDto(nodes, edges, placeNames);
    }

    public static CoverabilityTreeDto? BuildCoverabilityTreeDto(PetriNetSnapshot net, CoverabilityTreeBuilder cb)
    {
        if ((cb.HasErrors && !cb.IsTruncated) || cb.Nodes.Count == 0) return null;
        var placeNames = net.Places.Select(p => p.Name).ToList();
        var nodes = cb.Nodes.Select(n => new CoverNodeDto(
            n.Id,
            // ω or the saturation ceiling both render as ω (null) — the place is unbounded.
            n.Marking.Select(v => CoverabilityTreeBuilder.GrowsWithoutBound(v) ? (int?)null : v).ToList(),
            n.IsInitial, n.IsDeadlock, n.IsDuplicate,
            IsTruncated: cb.TruncatedIds.Contains(n.Id), n.ParentId)).ToList();
        var edges = cb.Edges.Select(e => new CoverEdgeDto(
            e.From, e.To, e.TransitionId, e.TransitionName)).ToList();
        var layout = GraphLayoutBuilder.Build(nodes, edges);
        return new CoverabilityTreeDto(nodes, edges, placeNames, layout);
    }

    private static ReachabilityGraphDto? BuildReachabilityGraph(AnalysisReport report)
    {
        var ss = report.StateSpace;
        if (ss == null || ss.HasErrors)
            return null;

        var net        = report.Net;
        var states     = ss.States;
        var placeNames = net.Places.Select(p => p.Name).ToList();
        var zeroEdge   = new HashSet<int>(FindDeadlockIndices(ss));
        // When truncated we cannot tell true deadlocks from unexplored boundary nodes
        var deadlocks  = ss.IsTruncated ? new HashSet<int>() : zeroEdge;
        var truncated  = ss.IsTruncated ? zeroEdge : new HashSet<int>();

        var nodes = states.Select((marking, id) => new ReachNodeDto(
            Id:          id,
            Marking:     marking,
            IsInitial:   id == 0,
            IsDeadlock:  deadlocks.Contains(id),
            IsDuplicate: false,
            IsTruncated: truncated.Contains(id),
            ParentId:    -1
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
        if (rt == null || (rt.HasErrors && !rt.IsTruncated) || rt.Nodes.Count == 0)
            return null;

        var net        = report.Net;
        var placeNames = net.Places.Select(p => p.Name).ToList();

        var nodes = rt.Nodes.Select(n => new ReachNodeDto(
            Id:          n.Id,
            Marking:     n.Marking,
            IsInitial:   n.IsInitial,
            IsDeadlock:  n.IsDeadlock,
            IsDuplicate: n.IsDuplicate,
            IsTruncated: rt.TruncatedIds.Contains(n.Id),
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
        if (ct == null || (ct.HasErrors && !ct.IsTruncated) || ct.Nodes.Count == 0)
            return null;

        var net        = report.Net;
        var placeNames = net.Places.Select(p => p.Name).ToList();

        var nodes = ct.Nodes.Select(n => new CoverNodeDto(
            Id:          n.Id,
            // ω, or the saturation ceiling reached when ω-acceleration is disabled,
            // both → null in the DTO so the client renders "ω" (place is unbounded).
            Marking:     n.Marking.Select(v => CoverabilityTreeBuilder.GrowsWithoutBound(v) ? (int?)null : v).ToList(),
            IsInitial:   n.IsInitial,
            IsDeadlock:  n.IsDeadlock,
            IsDuplicate: n.IsDuplicate,
            IsTruncated: ct.TruncatedIds.Contains(n.Id),
            ParentId:    n.ParentId
        )).ToList();

        var edges = ct.Edges.Select(e => new CoverEdgeDto(
            From:           e.From,
            To:             e.To,
            TransitionId:   e.TransitionId,
            TransitionName: e.TransitionName
        )).ToList();

        var layout = GraphLayoutBuilder.Build(nodes, edges);
        return new CoverabilityTreeDto(nodes, edges, placeNames, layout);
    }
}
