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
        var net    = PetriNetMapper.ToSnapshot(dto);
        var report = new AnalysisReport { Net = net };

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageStateSpace, 10, null));

            var ss = new StateSpaceAnalysis();
            ss.Build(net);
            report.StateSpace = ss;

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageInvariants, 30, null));

            var inv = new InvariantAnalysis();
            inv.Compute(net);
            report.Invariants = inv;

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageClassification, 50, null));

            var cls = new ClassificationAnalysis();
            cls.Compute(net);
            report.Classification = cls;

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageCycles, 65, null));

            var cyc = new CyclesAnalysis();
            cyc.Compute(net);
            report.Cycles = cyc;

            var tc = new TrapCotrapAnalysis();
            tc.Compute(net);
            report.TrapCotrap = tc;

            // ── Reachability tree ─────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageReachTree, 70, null));

            var rt = new ReachabilityTreeBuilder();
            rt.Build(net, ct);
            report.ReachabilityTree = rt;

            // ── Coverability tree (Karp-Miller) ───────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageCoverTree, 76, null));

            var coverBuilder = new CoverabilityTreeBuilder();
            coverBuilder.Build(net, ct);
            report.CoverabilityTree = coverBuilder;

            // ── Property tests ────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 83, null));

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

        }, ct);

        progress?.Report(new(AnalysisProgressMessage.StageComplete, 100, null));

        // Convert to DTO — same logic as the server's AnalysisResultMapper
        return BuildDto(report, net);
    }

    // ── DTO construction ──────────────────────────────────────────────────

    private static AnalysisResultDto BuildDto(AnalysisReport report, PetriNetSnapshot net)
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
        if (rt != null && !rt.HasErrors && rt.Nodes.Count > 0)
        {
            var rtNodes = rt.Nodes.Select(n => new ReachNodeDto(
                n.Id, n.Marking, n.IsInitial, n.IsDeadlock, n.IsDuplicate, n.ParentId)).ToList();
            var rtEdges = rt.Edges.Select(e => new ReachEdgeDto(
                e.From, e.To, e.TransitionId, e.TransitionName)).ToList();
            reachTree = new ReachabilityGraphDto(rtNodes, rtEdges, placeNames);
        }

        // ── Coverability tree DTO ─────────────────────────────────────────
        CoverabilityTreeDto? coverTree = null;
        var cb = report.CoverabilityTree;
        if (cb != null && !cb.HasErrors && cb.Nodes.Count > 0)
        {
            var ctNodes = cb.Nodes.Select(n => new CoverNodeDto(
                n.Id,
                n.Marking.Select(v => v == CoverabilityTreeBuilder.Omega ? (int?)null : v).ToList(),
                n.IsInitial, n.IsDeadlock, n.IsDuplicate, n.ParentId)).ToList();
            var ctEdges = cb.Edges.Select(e => new CoverEdgeDto(
                e.From, e.To, e.TransitionId, e.TransitionName)).ToList();
            coverTree = new CoverabilityTreeDto(ctNodes, ctEdges, placeNames);
        }

        return new AnalysisResultDto(
            StateCount:            report.StateCount,
            IsBounded:             ss?.IsBounded()                   ?? false,
            IsDeadlockFree:        ss?.IsDeadlockFree()              ?? false,
            IsReversible:          ss?.IsReversible()                ?? false,
            IsSafe:                ss?.IsSafe()                      ?? false,
            IsLive:                ss?.IsLive(net.Transitions.Count) ?? false,
            ClassificationSummary: report.ClassificationSummary,
            PropertyResults:       propertyResults,
            PInvariants:           pInvariants,
            TInvariants:           tInvariants,
            ReachabilityGraph:     reachTree,
            CoverabilityTree:      coverTree
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
