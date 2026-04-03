using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Server.Analysis;
using PetriEditor.Shared.Contracts;
using PetriEditor.Shared.Mapping;

namespace PetriEditor.Server.Analysis;

/// <summary>
/// Runs all analysis engines in sequence and reports progress after each stage.
/// Called by <see cref="Hubs.AnalysisHub"/> on a background thread.
///
/// Progress is reported via <paramref name="progress"/>; each message carries
/// a stage name and a completion percentage so the client can update a progress bar.
/// </summary>
public sealed class AnalysisOrchestrator
{
    public async Task<AnalysisResultDto> RunAsync(
        PetriNetDto                          dto,
        IProgress<AnalysisProgressMessage>?  progress,
        CancellationToken                    ct)
    {
        var net    = PetriNetMapper.ToSnapshot(dto);
        var report = new AnalysisReport { Net = net };

        bool cancelled = false;

        await Task.Run(() =>
        {
            if (ct.IsCancellationRequested) { cancelled = true; return; }
            progress?.Report(new(AnalysisProgressMessage.StageStateSpace, 5, null));
            var ss = new StateSpaceAnalysis();
            ss.Build(net, ct);
            report.StateSpace = ss;

            if (ct.IsCancellationRequested) { cancelled = true; return; }
            progress?.Report(new(AnalysisProgressMessage.StageInvariants, 30, null));
            var inv = new InvariantAnalysis();
            inv.Compute(net);
            report.Invariants = inv;

            if (ct.IsCancellationRequested) { cancelled = true; return; }
            progress?.Report(new(AnalysisProgressMessage.StageClassification, 50, null));
            var cls = new ClassificationAnalysis();
            cls.Compute(net);
            report.Classification = cls;

            if (ct.IsCancellationRequested) { cancelled = true; return; }
            progress?.Report(new(AnalysisProgressMessage.StageCycles, 65, null));
            var cyc = new CyclesAnalysis();
            cyc.Compute(net);
            report.Cycles = cyc;
            var tc = new TrapCotrapAnalysis();
            tc.Compute(net);
            report.TrapCotrap = tc;

            if (ct.IsCancellationRequested) { cancelled = true; return; }
            progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 70, null));
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
        });

        if (cancelled)
            throw new OperationCanceledException(ct);

        return AnalysisResultMapper.ToDto(report);
    }

    public async Task<GraphResultDto> RunGraphAsync(
        PetriNetDto       dto,
        bool              coverability,
        CancellationToken ct,
        int               maxStates = StateSpaceAnalysis.MaxStates,
        bool              wantGraph = false)
    {
        var net = PetriNetMapper.ToSnapshot(dto);

        ReachabilityGraphDto? reachDto     = null;
        ReachabilityGraphDto? reachTreeDto = null;
        CoverabilityTreeDto?  coverDto     = null;
        StateSpaceSummaryDto? ssDto        = null;
        string? error = null;

        await Task.Run(() =>
        {
            try
            {
                if (coverability)
                {
                    var cb = new CoverabilityTreeBuilder();
                    cb.Build(net, ct);
                    if (cb.HasErrors) error = cb.ErrorMessage;
                    else
                    {
                        coverDto = AnalysisResultMapper.BuildCoverabilityTreeDto(net, cb);
                        ssDto = new StateSpaceSummaryDto(
                            StateCount:     cb.Nodes.Count,
                            IsBounded:      false,
                            IsSafe:         false,
                            IsDeadlockFree: cb.Nodes.Any() && !cb.Nodes.Any(n => n.IsDeadlock),
                            IsReversible:   false,
                            ExceededLimit:  false);
                    }
                }
                else
                {
                    var ss = new StateSpaceAnalysis();
                    ss.Build(net, ct, maxStates);
                    if (ss.HasErrors) { error = ss.ErrorMsg; return; }

                    // Only build the Cytoscape graph DTO if the caller explicitly wants it
                    if (wantGraph)
                        reachDto = AnalysisResultMapper.BuildReachabilityGraphDto(net, ss);

                    ssDto = new StateSpaceSummaryDto(
                        StateCount:     ss.States.Count,
                        IsBounded:      ss.IsBounded(),
                        IsSafe:         ss.IsSafe(),
                        IsDeadlockFree: ss.IsDeadlockFree(),
                        IsReversible:   ss.IsReversible(),
                        ExceededLimit:  ss.States.Count >= maxStates);

                    if (!ct.IsCancellationRequested)
                    {
                        var rt = new ReachabilityTreeBuilder();
                        rt.Build(net, ct);
                        if (!rt.HasErrors)
                            reachTreeDto = AnalysisResultMapper.BuildReachabilityTreeDto(net, rt);
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
        });

        var result = new GraphResultDto(reachDto, reachTreeDto, coverDto, error, ssDto);
        // Release large state-space objects before returning — they're no longer needed
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
        return result;
    }

    private static PropertyTestResult SafeRun(NetProperty property, Func<PropertyTestResult> action)
    {
        try
        {
            return action();
        }
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
