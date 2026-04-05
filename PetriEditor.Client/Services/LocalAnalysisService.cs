using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Shared.Contracts;
using PetriEditor.Shared.Mapping;

namespace PetriEditor.Client.Services;

/// <summary>
/// Runs all analysis engines directly inside the browser (WebAssembly),
/// with no server round-trip.
///
/// Used as an offline fallback, or when the user explicitly opts for local
/// computation. Implements the same <see cref="IAnalysisService"/> interface
/// as <see cref="ClientAnalysisService"/> so the rest of the app is unaware
/// of where computation happens.
/// </summary>
public sealed class LocalAnalysisService : IAnalysisService
{
    public async Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto                          dto,
        IProgress<AnalysisProgressMessage>?  progress = null,
        CancellationToken                    ct       = default)
    {
        // NOTE: Blazor WASM is single-threaded — Task.Run does NOT offload to a real thread.
        // We must await Task.Yield() between stages so the browser event loop can run,
        // which allows cancellation button clicks to be processed.

        var net    = PetriNetMapper.ToSnapshot(dto);
        var report = new AnalysisReport { Net = net };

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(AnalysisProgressMessage.StageStateSpace, 10, null));
        await Task.Yield();

        var ss = new StateSpaceAnalysis();
        ss.Build(net, ct);
        report.StateSpace = ss;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(AnalysisProgressMessage.StageInvariants, 30, null));
        await Task.Yield();

        var inv = new InvariantAnalysis();
        inv.Compute(net);
        report.Invariants = inv;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(AnalysisProgressMessage.StageClassification, 50, null));
        await Task.Yield();

        var cls = new ClassificationAnalysis();
        cls.Compute(net);
        report.Classification = cls;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(AnalysisProgressMessage.StageCycles, 65, null));
        await Task.Yield();

        var cyc = new CyclesAnalysis();
        cyc.Compute(net);
        report.Cycles = cyc;

        var tc = new TrapCotrapAnalysis();
        tc.Compute(net);
        report.TrapCotrap = tc;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(AnalysisProgressMessage.StageReachTree, 70, null));
        await Task.Yield();

        var rt = new ReachabilityTreeBuilder();
        rt.Build(net, ct);
        report.ReachabilityTree = rt;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(AnalysisProgressMessage.StageCoverTree, 76, null));
        await Task.Yield();

        var coverBuilder = new CoverabilityTreeBuilder();
        coverBuilder.Build(net, ct);
        report.CoverabilityTree = coverBuilder;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 83, null));
        await Task.Yield();

        var bundle = new AnalysisBundle
        {
            Net            = net,
            StateSpace     = ss,
            Invariants     = inv,
            Classification = cls,
            Cycles         = cyc,
            TrapCotrap     = tc,
        };

        var results = bundle.PropertyResults;
        results[NetProperty.Liveness]         = SafeRun(NetProperty.Liveness,         () => new LivenessTest().Run(bundle));
        results[NetProperty.Boundedness]      = SafeRun(NetProperty.Boundedness,       () => new BoundednessTest().Run(bundle));
        results[NetProperty.Safety]           = SafeRun(NetProperty.Safety,            () => new SafetyTest().Run(bundle));
        results[NetProperty.Conservativeness] = SafeRun(NetProperty.Conservativeness,  () => new ConservativenessTest().Run(bundle));
        results[NetProperty.Repetitiveness]   = SafeRun(NetProperty.Repetitiveness,    () => new RepetitivenessTest().Run(bundle));
        results[NetProperty.DeadlockFree]     = SafeRun(NetProperty.DeadlockFree,      () => new DeadlockFreeTest().Run(bundle));
        results[NetProperty.Reversibility]    = SafeRun(NetProperty.Reversibility,     () => new ReversibilityTest().Run(bundle));
        report.PropertyResults = results;

        progress?.Report(new(AnalysisProgressMessage.StageComplete, 100, null));

        // Convert to DTO — same logic as the server's AnalysisResultMapper
        return BuildDto(report, net);
    }

    // ── DTO construction ──────────────────────────────────────────────────

    public static AnalysisResultDto BuildDto(AnalysisReport report, PetriNetSnapshot net)
    {
        var ss = report.StateSpace;

        var propertyResults = report.PropertyResults.Values
            .Select(r => new PropertyResultDto(
                r.Property.ToString(),
                r.Status.ToString(),
                r.StatusLabel,
                r.StatusColor,
                r.StatusBackground,
                r.Reasons,
                r.Errors))
            .ToList();

        var pInvariants = (report.Invariants?.PInvariants ?? [])
            .Select(inv => new InvariantDto(inv.Structure))
            .ToList();

        var tInvariants = (report.Invariants?.TInvariants ?? [])
            .Select(inv => new InvariantDto(inv.Structure))
            .ToList();

        var placeNames = net.Places.Select(p => p.Name).ToList();

        // ── Reachability tree DTO ─────────────────────────────────────────
        ReachabilityGraphDto? reachTree = null;
        var rt = report.ReachabilityTree;
        if (rt != null && (!rt.HasErrors || rt.IsTruncated) && rt.Nodes.Count > 0)
        {
            var rtNodes = rt.Nodes.Select(n => new ReachNodeDto(
                n.Id, n.Marking, n.IsInitial, n.IsDeadlock, n.IsDuplicate,
                rt.TruncatedIds.Contains(n.Id), n.ParentId)).ToList();
            var rtEdges = rt.Edges.Select(e => new ReachEdgeDto(
                e.From, e.To, e.TransitionId, e.TransitionName)).ToList();
            reachTree = new ReachabilityGraphDto(rtNodes, rtEdges, placeNames);
        }

        // ── Coverability tree DTO ─────────────────────────────────────────
        CoverabilityTreeDto? coverTree = null;
        var cb = report.CoverabilityTree;
        if (cb != null && (!cb.HasErrors || cb.IsTruncated) && cb.Nodes.Count > 0)
        {
            var ctNodes = cb.Nodes.Select(n => new CoverNodeDto(
                n.Id,
                n.Marking.Select(v => v == CoverabilityTreeBuilder.Omega ? (int?)null : v).ToList(),
                n.IsInitial, n.IsDeadlock, n.IsDuplicate,
                cb.TruncatedIds.Contains(n.Id), n.ParentId)).ToList();
            var ctEdges = cb.Edges.Select(e => new CoverEdgeDto(
                e.From, e.To, e.TransitionId, e.TransitionName)).ToList();
            coverTree = new CoverabilityTreeDto(ctNodes, ctEdges, placeNames);
        }

        // ── Cycles DTO ────────────────────────────────────────────────────
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

        // ── Traps DTO ─────────────────────────────────────────────────────
        TrapsDto? trapsDto = null;
        var tc = report.TrapCotrap;
        if (tc != null)
        {
            var traps   = tc.Traps.Select(s => new PlaceSubsetDto(s.PlaceIds.ToList(), s.HasToken)).ToList();
            var siphons = tc.Cotraps.Select(s => new PlaceSubsetDto(s.PlaceIds.ToList(), s.HasToken)).ToList();
            trapsDto = new TrapsDto(tc.HasErrors, tc.ErrorMsg, traps, siphons);
        }

        // ── Classification DTO ────────────────────────────────────────────
        var subclasses = (report.Classification?.Classes ?? (IReadOnlySet<Analysis.Engines.NetSubclass>)new HashSet<Analysis.Engines.NetSubclass>())
            .Select(c => c.ToString()).ToList();

        // ── Net structure DTO ─────────────────────────────────────────────
        var netStructure = new NetStructureDto(
            PlaceCount:       net.Places.Count,
            TransitionCount:  net.Transitions.Count,
            NormalArcCount:   net.Arcs.Count(a => a.ArcType == PnArcType.Normal),
            InhibitorArcCount: net.Arcs.Count(a => a.ArcType == PnArcType.Inhibitor),
            ResetArcCount:    net.Arcs.Count(a => a.ArcType == PnArcType.Reset),
            InitialTokenCount: net.Places.Sum(p => p.Tokens));

        return new AnalysisResultDto(
            StateCount:               report.StateCount,
            IsBounded:                ss?.IsBounded()                   ?? false,
            IsDeadlockFree:           ss?.IsDeadlockFree()              ?? false,
            IsReversible:             ss?.IsReversible()                ?? false,
            IsSafe:                   ss?.IsSafe()                      ?? false,
            IsLive:                   ss?.IsLive(net.Transitions.Count) ?? false,
            ClassificationSummary:    report.ClassificationSummary,
            ClassificationSubclasses: subclasses,
            PropertyResults:          propertyResults,
            PInvariants:              pInvariants,
            TInvariants:              tInvariants,
            ReachabilityGraph:        reachTree,
            CoverabilityTree:         coverTree,
            Cycles:                   cyclesDto,
            Traps:                    trapsDto,
            NetStructure:             netStructure
        );
    }

    private static PropertyTestResult SafeRun(NetProperty property, Func<PropertyTestResult> action)
    {
        try { return action(); }
        catch (Exception ex)
        {
            return new PropertyTestResult(
                property,
                TestResultStatus.Undecidable,
                [$"{property} test could not complete due to an internal error."],
                [$"{ex.GetType().Name}: {ex.Message}"]);
        }
    }
}
